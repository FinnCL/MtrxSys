using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Ciclo de vida da sessão WAHA: status/snapshot/identidade, start/restart/logout/delete,
/// QR e código de pareamento, e o config de webhook+proxy. Uma responsabilidade só do antigo WahaClient.</summary>
internal sealed class WahaSessionClient(WahaHttp http)
{
    public async Task<WahaSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken ct)
    {
        // Delega pro snapshot (uma leitura só da sessão) e devolve apenas o status — evita duplicar
        // a mesma lógica de 404→Stopped / não-sucesso→Unknown / parse em dois métodos.
        return (await GetSessionSnapshotAsync(sessionId, ct)).Status;
    }

    public async Task<string?> GetOwnPhoneE164Async(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/sessions/{WahaHttp.Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>(WahaHttp.Json, ct);
        return WahaParsing.PhoneFromChatId(body?.Me?.Id); // ex.: "5511999999999@c.us" -> "+5511999999999"
    }

    public async Task<WahaSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/sessions/{WahaHttp.Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new WahaSessionSnapshot(WahaSessionStatus.Stopped, null);
        }
        if (!resp.IsSuccessStatusCode)
        {
            return new WahaSessionSnapshot(WahaSessionStatus.Unknown, null);
        }
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>(WahaHttp.Json, ct);
        var status = WahaParsing.ParseStatus(body?.Status);
        var phone = WahaParsing.PhoneFromChatId(body?.Me?.Id);
        var identity = phone is null
            ? null
            : new WahaIdentity(phone, string.IsNullOrWhiteSpace(body?.Me?.PushName) ? null : body!.Me!.PushName);
        // Proxy REALMENTE aplicado na sessão (config.proxy.server) — pro indicador honesto na aba.
        var appliedProxy = body?.Config?.Proxy?.Server;
        return new WahaSessionSnapshot(
            status, identity, string.IsNullOrWhiteSpace(appliedProxy) ? null : appliedProxy);
    }

    // TRAVA anti-vazamento: a sessão só está "pronta pra parear" quando o proxy DESEJADO já está no
    // config (ou quando não há proxy configurado neste ambiente — nada a esperar). Compara o desejado
    // (Waha:ProxyServer normalizado) com o AppliedProxyServer da sessão. O qr.png usa isto pra NÃO
    // servir o QR de uma sessão sem proxy (senão o chip conectaria sem proxy, saindo pelo IP da máquina).
    public async Task<bool> IsProxyReadyAsync(string sessionId, CancellationToken ct)
    {
        var desired = http.NormalizedProxyServer();
        if (desired is null)
        {
            return true;
        }
        var snap = await GetSessionSnapshotAsync(sessionId, ct);
        return string.Equals(desired, snap.AppliedProxyServer, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> ResolveLidToPhoneE164Async(string sessionId, string lid, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/lids/{WahaHttp.Esc(lid)}");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await resp.Content.ReadFromJsonAsync<LidDto>(WahaHttp.Json, ct);
        return WahaParsing.PhoneFromChatId(body?.Pn); // "5511921404487@c.us" -> "+5511921404487"
    }

    public async Task<string?> ResolvePhoneToLidAsync(string sessionId, string phoneDigits, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/lids/pn/{WahaHttp.Esc(phoneDigits)}");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null; // 404 (WAHA sem esse número mapeado / versão velha) → o chamador cai no @c.us
        }
        var body = await resp.Content.ReadFromJsonAsync<LidDto>(WahaHttp.Json, ct);
        return string.IsNullOrWhiteSpace(body?.Lid) ? null : body.Lid; // já vem "{lid}@lid"
    }

    public async Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct)
    {
        // Lê a sessão UMA vez (existe? tem config? qual status?). NÃO confiamos no POST /start pra
        // criar: algumas versões do WAHA (2026.4.x) CRIAM uma sessão "pelada" (config nulo — sem
        // webhook/token nem noweb.store) no /start em vez de devolver 404. Aí o /start "dá certo" mas
        // a sessão nasce sem o token do webhook (inbound/opt-out viram 401) e sem store.fullSync (chat
        // 400, 463 a co-membro), e o QR fica instável. Então: probe + cria com o config COMPLETO
        // quando falta, em vez de assumir que /start faz a coisa certa.
        var dto = await TryGetSessionAsync(sessionId, ct);
        var status = WahaParsing.ParseStatus(dto?.Status);

        // Conectada: NUNCA recria nem apaga (isso deslogaria a conta). Nada a fazer.
        if (status is WahaSessionStatus.Working)
        {
            return;
        }

        // Existe COM config (webhook/store/proxy já gravados): só (re)inicia, preservando a credencial
        // guardada — uma sessão parada pode reconectar a MESMA conta. O restart cobre também FAILED (o
        // WAHA recusa o /start puro em FAILED) e ScanQrCode/Stopped, sem apagar nada.
        if (dto?.Config is not null)
        {
            using var req = http.NewRequest(HttpMethod.Post, $"api/sessions/{WahaHttp.Esc(sessionId)}/restart");
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                await CreateSessionAsync(sessionId, ct); // sumiu entre o probe e o restart
                return;
            }
            // 422/409 = estado que recusa restart no momento (ex.: já iniciando) — tolera; o poll segue.
            if (resp.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
            {
                return;
            }
            resp.EnsureSuccessStatusCode();
            return;
        }

        // Não existe, ou existe "pelada" (config nulo): recria com o config completo. Apagar aqui é
        // seguro — fora de Working não há conta pareada a perder, e a sessão pelada nunca pareou direito.
        if (dto is not null)
        {
            await DeleteSessionAsync(sessionId, ct);
        }
        await CreateSessionAsync(sessionId, ct);
    }

    private async Task<SessionDto?> TryGetSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/sessions/{WahaHttp.Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<SessionDto>(WahaHttp.Json, ct)
            : null;
    }

    private async Task CreateSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, "api/sessions");
        // Proxy E WEBHOOK já na CRIAÇÃO. Proxy: o WAHA conecta no start; sem ele aqui, a 1ª conexão
        // sairia pelo IP da máquina (vazamento). Webhook: este WAHA só aplica o customHeader (token)
        // na criação/start — adicioná-lo por PUT depois numa sessão rodando NÃO pega (webhooks saem
        // 401 sem token → opt-out/inbound/sensor quebrados). Criar com o config completo fecha os dois.
        var config = new Dictionary<string, object>();
        var proxy = http.ProxyConfigOrNull();
        if (proxy is not null)
        {
            config["proxy"] = proxy;
        }
        var webhooks = http.WebhookConfigOrNull();
        if (webhooks is not null)
        {
            config["webhooks"] = webhooks;
        }
        config["noweb"] = http.NowebConfig();
        object payload = config.Count == 0
            ? new { name = sessionId, start = true }
            : new { name = sessionId, start = true, config };
        req.Content = JsonContent.Create(payload, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        // 422/409 = corrida: a sessão já foi criada nesse meio-tempo. Considera concluído.
        if (resp.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    public async Task RestartSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, $"api/sessions/{WahaHttp.Esc(sessionId)}/restart");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task LogoutSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, $"api/sessions/{WahaHttp.Esc(sessionId)}/logout");
        using var resp = await http.SendAsync(req, ct);
        // Idempotente: se a sessão já está parada/não existe, considera concluído.
        if (resp.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity
            or HttpStatusCode.Conflict)
        {
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Delete, $"api/sessions/{WahaHttp.Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        // Idempotente/tolerante: 404 = já não existe; 422/409 = estado em que o WAHA recusa o
        // delete (ex.: sessão parando/engine instável) — não derruba o reset. O start seguinte
        // recria/reinicia a sessão de qualquer forma.
        if (resp.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity
            or HttpStatusCode.Conflict)
        {
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> GetQrPngAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/auth/qr?format=image");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string> GetQrRawAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/auth/qr?format=raw");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<QrRawDto>(WahaHttp.Json, ct);
        return dto?.Value ?? string.Empty;
    }

    public async Task<string> RequestPairingCodeAsync(string sessionId, string phoneNumber, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, $"api/{WahaHttp.Esc(sessionId)}/auth/request-code");
        req.Content = JsonContent.Create(new { phoneNumber }, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<PairingCodeDto>(WahaHttp.Json, ct);
        return dto?.Code ?? string.Empty;
    }

    public async Task<bool> EnsureWebhookConfiguredAsync(
        string sessionId, string webhookUrl, IReadOnlyList<string> events, string? webhookToken, CancellationToken ct)
    {
        using var getReq = http.NewRequest(HttpMethod.Get, $"api/sessions/{WahaHttp.Esc(sessionId)}?all=true");
        using var getResp = await http.SendAsync(getReq, ct);
        if (getResp.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        getResp.EnsureSuccessStatusCode();
        using var jsonStream = await getResp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
        var root = doc.RootElement;

        // "Presente" exige a URL E — se há token — o customHeader X-Webhook-Token já gravado. Sem
        // isso, uma sessão antiga com a URL mas sem o header nunca receberia o token (o WAHA não
        // mandaria o header e o endpoint rejeitaria o inbound); forçamos o PUT pra aplicá-lo.
        var webhookPresent = WahaParsing.WebhookConfigured(root, webhookUrl, webhookToken);
        // O proxy só vale na (re)conexão e SÓ pega via config de sessão (a env var WHATSAPP_PROXY_SERVER
        // é ignorada no WAHA 2026.x CORE/NOWEB — comprovado). Compara o que está gravado com o desejado.
        var desiredProxy = http.NormalizedProxyServer();
        var currentProxy = WahaParsing.CurrentProxyServer(root);
        var proxyMatches = string.Equals(desiredProxy, currentProxy, StringComparison.OrdinalIgnoreCase);
        // Status atual da sessão: NÃO religamos uma conta JÁ CONECTADA só pra aplicar proxy (ver abaixo).
        var currentStatus = root.TryGetProperty("status", out var statusEl)
            ? WahaParsing.ParseStatus(statusEl.GetString())
            : WahaSessionStatus.Unknown;

        // Mexe no proxy SÓ em SCAN_QR_CODE (pareando, SEM conta a perder). NUNCA em WORKING: aplicar
        // (PUT) OU religar proxy num chip JÁ CONECTADO DESLOGA. O eea312f achou que aplicar no Working
        // era seguro, mas DESLOGOU 2x no restart da api (o WahaProxyEnsure de 60s mexeu na sessão WORKING
        // por timing). O disparo continua saindo por BR mesmo assim: o proxy é setado NA CRIAÇÃO da
        // sessão e PERSISTE no WORKING — não precisa (nem pode) re-aplicar. Em WORKING, com o webhook já
        // presente, o PUT é PULADO (linha abaixo) → a sessão fica intocada. É a volta ao comportamento
        // do 0fd381f ("só aplicar proxy em sessão SEM conta"), que existia justamente pra evitar isto.
        var proxyableState = currentStatus is WahaSessionStatus.ScanQrCode;

        // GUARD DURO (anti-logout): só é seguro dar PUT/re-config numa sessão em SCAN_QR_CODE (pareando,
        // SEM conta a perder). Em QUALQUER outro estado — WORKING sobretudo — um PUT DESLOGA o companion.
        // Foi a CAUSA RAIZ de um logout real: o WahaProxyEnsure roda a cada 60s e, no restart da api, com
        // o webhook lido como "sem token", caía no PUT e reescrevia o config da sessão WORKING → logout →
        // SCAN_QR_CODE. Então, fora de SCAN_QR_CODE, NUNCA mexe: retorna sem tocar na sessão. Um webhook
        // sem token numa sessão viva (raro — nasce com token na criação) só custa o inbound até o próximo
        // pareamento, o que é MENOS grave que perder o chip com um PUT. (Bug antigo: o short-circuit era
        // `webhookPresent && (proxyMatches || !proxyableState)`, que em WORKING virava só `webhookPresent`
        // — deixando o PUT disparar quando o token faltava e derrubando a sessão conectada.)
        if (!proxyableState)
        {
            return true;
        }
        // Daqui pra baixo a sessão está em SCAN_QR_CODE (PUT é seguro). Se já está tudo certo, pula.
        if (webhookPresent && proxyMatches)
        {
            return true;
        }

        // Aplica o proxy nos estados proxyable (SCAN_QR_CODE + WORKING). Fora deles, o PUT leva SÓ o
        // webhook. O PUT SUBSTITUI o config, então mandamos webhook e (quando permitido) proxy juntos —
        // era justamente o PUT só-webhook em Working que ANTES apagava o proxy quando a conta ficava viva.
        var proxy = http.ProxyConfigOrNull();
        var applyProxy = proxy is not null && proxyableState;
        var config = new Dictionary<string, object>
        {
            ["webhooks"] = WahaParsing.BuildWebhooks(webhookUrl, events, webhookToken),
        };
        if (applyProxy)
        {
            config["proxy"] = proxy!;
        }
        config["noweb"] = http.NowebConfig();

        using var putReq = http.NewRequest(HttpMethod.Put, $"api/sessions/{WahaHttp.Esc(sessionId)}");
        putReq.Content = JsonContent.Create(new { name = sessionId, config }, options: WahaHttp.Json);
        using var putResp = await http.SendAsync(putReq, ct);
        putResp.EnsureSuccessStatusCode();

        // Religa a sessão SÓ em SCAN_QR_CODE (pareando, SEM conta a perder). NUNCA religa uma sessão
        // WORKING por proxy: religar um chip JÁ CONECTADO DESLOGA — foi a causa raiz de um logout real
        // (o WahaProxyEnsure roda a cada 60s; no restart da api leu um proxy "divergente" por timing de
        // startup e RELIGOU a sessão WORKING → logout → SCAN_QR_CODE). Em WORKING o proxy JÁ vale (setado
        // na criação + mantido no PUT acima), então NÃO precisa religar — o PUT sozinho basta e é seguro.
        if (applyProxy && !proxyMatches && currentStatus is WahaSessionStatus.ScanQrCode)
        {
            await TryRestartForProxyAsync(sessionId, ct);
        }
        return true;
    }

    // Religa a sessão pra o proxy novo valer. Tolerante: 404/422/409 (sessão inexistente/em estado
    // que recusa restart) não derruba o startup — o proxy já está gravado no config e vale no próximo start.
    private async Task TryRestartForProxyAsync(string sessionId, CancellationToken ct)
    {
        // Melhor-esforço: não chamamos EnsureSuccessStatusCode de propósito — qualquer status (incl.
        // 404/422/409 de sessão inexistente/em estado que recusa restart) é tolerado; o proxy já está
        // gravado no config e vale no próximo start. Só não engolimos o cancelamento.
        using var req = http.NewRequest(HttpMethod.Post, $"api/sessions/{WahaHttp.Esc(sessionId)}/restart");
        using var resp = await http.SendAsync(req, ct);
    }
}
