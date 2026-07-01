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
        return new WahaSessionSnapshot(status, identity);
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

    public async Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, $"api/sessions/{WahaHttp.Esc(sessionId)}/start");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            return;
        }
        // Engine NOWEB: o /start NÃO cria a sessão — só inicia uma que já existe. Quando ela ainda
        // não foi criada (ex.: stack novo, ou logo após um delete no reset), o WAHA responde 404.
        // Nesse caso criamos a sessão já iniciando; o webhook é aplicado em seguida pelo ensurer.
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            await CreateSessionAsync(sessionId, ct);
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    private async Task CreateSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, "api/sessions");
        // Proxy JÁ na criação: o WAHA conecta no WhatsApp logo no start; sem o proxy aqui, a 1ª
        // conexão sairia pelo IP da máquina (vazamento) antes de o ensurer aplicar o config depois.
        var proxy = http.ProxyConfigOrNull();
        object payload = proxy is null
            ? new { name = sessionId, start = true }
            : new { name = sessionId, start = true, config = new { proxy } };
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

        // Nada a fazer: webhook já no lugar e proxy já bate (inclusive ambos ausentes).
        if (webhookPresent && proxyMatches)
        {
            return true;
        }

        // PUT substitui o config — então mandamos webhook E proxy juntos, pra um não apagar o outro.
        var proxy = http.ProxyConfigOrNull();
        object config = proxy is null
            ? new { webhooks = WahaParsing.BuildWebhooks(webhookUrl, events, webhookToken) }
            : new { webhooks = WahaParsing.BuildWebhooks(webhookUrl, events, webhookToken), proxy };

        using var putReq = http.NewRequest(HttpMethod.Put, $"api/sessions/{WahaHttp.Esc(sessionId)}");
        putReq.Content = JsonContent.Create(new { name = sessionId, config }, options: WahaHttp.Json);
        using var putResp = await http.SendAsync(putReq, ct);
        putResp.EnsureSuccessStatusCode();

        // Proxy mudou: religa a sessão (reusa a auth salva — SEM QR) pra o chip reconectar pelo IP
        // novo. Melhor-esforço: uma sessão sem auth/instável que recuse o restart não trava o startup.
        if (!proxyMatches)
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
