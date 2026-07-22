using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Grupos WAHA: listar (filtrando os que o número já não participa), participantes, sair e
/// entrar por convite. Depende de <see cref="WahaSessionClient"/> só pra descobrir o próprio número
/// (decidir participação) — acoplamento explícito, não escondido.</summary>
internal sealed class WahaGroupsClient(WahaHttp http, WahaSessionClient session, IMemoryCache? cache = null)
{
    // Máximo de checagens de /participants EM VOO ao listar grupos: uma conta pode estar em dezenas de
    // grupos, e disparar todas de uma vez marteleria o WAHA local (timeouts/queda). Poucas-em-paralelo
    // é o meio-termo entre latência e não sobrecarregar. Cacheado por ~10s por grupo (CachedIsMember).
    private const int ParticipantCheckConcurrency = 8;

    public async Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct)
    {
        var candidates = await ListGroupsRawAsync(sessionId, ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        // Número conectado, pra decidir participação. Sem ele, não dá pra filtrar → devolve cru
        // (melhor-esforço: preferimos exibir um a mais do que esconder um real).
        string? ownDigits = null;
        try
        {
            var ownE164 = await session.GetOwnPhoneE164Async(sessionId, ct);
            ownDigits = ownE164?.TrimStart('+');
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch
        {
            // sessão degradada: cai no fallback "não filtra".
        }
#pragma warning restore CA1031
        if (string.IsNullOrEmpty(ownDigits))
        {
            return candidates;
        }

        // FILTRO DE PARTICIPAÇÃO — vale pros DOIS engines. Antes o NOWEB PULAVA isto, assumindo que
        // "/groups só lista grupos dos quais ainda sou membro" — FALSO: o cache de metadados do NOWEB
        // (Baileys) mantém grupos que você SAIU ou que sumiram do aparelho, e eles voltavam na lista a
        // cada Atualizar (inclusive DUPLICADOS: JID velho stale + o atual). Esconde quem já não é membro
        // consultando os /participants ao vivo (lista vazia = saí), ciente de @lid e preservando em
        // ambiguidade/erro. Cacheado ~10s por grupo pra não estourar o WAHA em Atualizar seguidos.
        // Concorrência LIMITADA (ver ParticipantCheckConcurrency). A ordem não importa — o endpoint
        // ordena por nome depois; por isso um ConcurrentBag basta.
        var kept = new ConcurrentBag<WahaGroup>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = ParticipantCheckConcurrency, CancellationToken = ct },
            async (g, token) =>
            {
                try
                {
                    if (await CachedIsMemberAsync(sessionId, WahaParsing.EnsureGroupJid(g.Id), ownDigits, token))
                    {
                        kept.Add(g);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch
                {
                    kept.Add(g); // falha na checagem deste grupo → preserva
                }
#pragma warning restore CA1031
            });

        return kept.ToList();
    }

    // Lista CRUA do /groups (os DOIS engines), SEM o filtro de participação. Uso interno onde filtrar
    // ATRAPALHARIA — em especial resolver o grupo recém-ENTRADO (TryResolveJoinedGroupAsync): logo após
    // o join você É membro, mas o /participants pode ainda não ter sincronizado; filtrar aqui esconderia
    // o grupo recém-entrado e o import cairia pra 0 EM SILÊNCIO (quebrando a coleta anti-ban).
    private async Task<List<WahaGroup>> ListGroupsRawAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/groups");
        using var resp = await http.SendAsync(req, ct);
        // Sessão desconectada/não-WORKING: o WAHA responde 422. Degrada pra lista vazia (a aba "Grupos"
        // mostra "nenhum grupo") em vez de estourar 500. Mesmo padrão do GetMessagesAsync.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        // Normaliza os DOIS formatos de engine: NOWEB devolve um OBJETO indexado por JID (nome em
        // `subject`, participants inline); WEBJS devolve um ARRAY (nome em `name`).
        return root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject()
                .Select(prop => WahaParsing.MapNowebGroup(prop.Value))
                .Where(g => !string.IsNullOrEmpty(g.Id))
                .ToList()
            : (root.Deserialize<List<GroupDto>>(WahaHttp.Json) ?? [])
                .Select(g => new WahaGroup(g.Id?.User ?? g.Id?.Server ?? "", g.Name ?? "", g.Participants?.Count))
                .Where(g => !string.IsNullOrEmpty(g.Id))
                .ToList();
    }

    // A checagem de participação é a parte CARA do ListGroups (1 GET /participants por grupo = N+1).
    // Cacheia ~10s por (sessão, grupo, número): em polls seguidos da aba "Grupos" a rajada de chamadas
    // ao WAHA some, sem stalear a LISTA de grupos (essa segue fresca) — só a decisão de membro, que
    // muda raramente. Sem cache (testes), chama direto.
    private Task<bool> CachedIsMemberAsync(string sessionId, string groupJid, string ownDigits, CancellationToken ct)
    {
        if (cache is null)
        {
            return IsCurrentMemberOfAsync(sessionId, groupJid, ownDigits, ct);
        }
        return cache.GetOrCreateAsync($"waha:grpmember:{sessionId}:{groupJid}:{ownDigits}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10);
            return IsCurrentMemberOfAsync(sessionId, groupJid, ownDigits, ct);
        })!;
    }

    // Decide se o número conectado ainda é participante deste grupo, pela resposta CRUA do
    // /participants — parseada com PhoneFromParticipant, que resolve o número REAL nas DUAS engines
    // (NOWEB expõe em `phoneNumber` mesmo com o id vindo como @lid; WEBJS traz no id). Era exatamente
    // o buraco que impedia o NOWEB de filtrar: o ParticipantDto tipado não tem `phoneNumber`, então o
    // próprio número nunca casava e o filtro era inútil lá (por isso o NOWEB pulava o filtro).
    // Observado: grupo em que você participa devolve a lista (incluindo você); grupo do qual você saiu
    // devolve LISTA VAZIA (perde a visibilidade). Lista vazia = saí. Preserva (true) em ambiguidade
    // real: erro HTTP, shape inesperado, ou lista SÓ com @lid (nenhum número decifrável).
    private async Task<bool> IsCurrentMemberOfAsync(string sessionId, string groupJid, string ownDigits, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/groups/{WahaHttp.Esc(groupJid)}/participants");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return true; // erro de leitura → ambíguo → preserva
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return true; // shape inesperado → preserva
        }
        var total = 0;
        var resolvedAnyRealNumber = false;
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            total++;
            var phone = WahaParsing.PhoneFromParticipant(p);
            if (phone is null)
            {
                continue; // @lid / sem número decifrável
            }
            resolvedAnyRealNumber = true;
            if (string.Equals(phone.TrimStart('+'), ownDigits, StringComparison.Ordinal))
            {
                return true; // meu número está na lista → sou membro
            }
        }
        if (total == 0)
        {
            return false; // lista vazia = saí (perdi a visibilidade dos membros)
        }
        // Tem gente mas ninguém decifrável (só @lid) → não dá pra decidir → preserva.
        // Tem números reais e o meu NÃO está entre eles → já não sou membro (saí / fantasma).
        return !resolvedAnyRealNumber;
    }

    public async Task<IReadOnlyList<WahaParticipant>> ListGroupParticipantsAsync(string sessionId, string groupId, CancellationToken ct)
    {
        // A WAHA (engine WEBJS) resolve o grupo via getChatById, que exige o JID completo.
        // No app circula só o número do grupo (sem sufixo), então garantimos o @g.us aqui;
        // sem isso o getChatById não encontra o grupo e a WAHA devolve 500.
        var groupJid = WahaParsing.EnsureGroupJid(groupId);
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/groups/{WahaHttp.Esc(groupJid)}/participants");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<WahaParticipant>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var phone = WahaParsing.PhoneFromParticipant(p);
            if (phone is null)
            {
                continue;
            }
            var name = p.TryGetProperty("pushName", out var nmEl) ? nmEl.GetString() : null;
            result.Add(new WahaParticipant(
                Id: phone.TrimStart('+'),
                PhoneE164: phone,
                Name: string.IsNullOrWhiteSpace(name) ? null : name,
                IsAdmin: WahaParsing.IsAdminRole(p)));
        }
        return result;
    }

    public async Task<WahaGroup> CreateGroupAsync(
        string sessionId, string name, IReadOnlyCollection<string> participantsE164, CancellationToken ct)
    {
        // Contrato da WAHA pra criar: sessão como SEGMENTO (`/api/{session}/groups`) — ao contrário
        // dos CONTATOS, que a mesma API expõe com a sessão em query param. Não uniformizar: dá 404.
        // Participantes vão como objetos {id}, com o id no formato <dígitos>@c.us.
        using var req = http.NewRequest(HttpMethod.Post, $"api/{WahaHttp.Esc(sessionId)}/groups");
        var participants = participantsE164
            .Select(p => new { id = WahaParsing.ToChatId(p) })
            .ToArray();
        req.Content = JsonContent.Create(new { name, participants }, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        // SEM tolerância aqui, ao contrário do listar: criar é uma AÇÃO. Se falhou, o operador tem
        // que saber — engolir viraria um "grupo criado" que não existe, e o registro local ficaria
        // apontando pro vazio.
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var id = WahaParsing.ReadCreatedGroupId(doc.RootElement);
        if (string.IsNullOrEmpty(id))
        {
            // Sem id não dá pra registrar como "meu" nem importar membros depois — e o grupo JÁ foi
            // criado no WhatsApp. Falhar alto é melhor que devolver um id vazio que vira grupo órfão.
            throw new InvalidOperationException(
                "A WAHA criou o grupo mas não devolveu o id. Veja em Grupos e registre manualmente.");
        }
        return new WahaGroup(id, name, participantsE164.Count + 1); // +1 = o próprio chip
    }

    public async Task LeaveGroupAsync(string sessionId, string groupId, CancellationToken ct)
    {
        var groupJid = WahaParsing.EnsureGroupJid(groupId);
        using var req = http.NewRequest(HttpMethod.Post, $"api/{WahaHttp.Esc(sessionId)}/groups/{WahaHttp.Esc(groupJid)}/leave");
        using var resp = await http.SendAsync(req, ct);
        // SÓ 404 conta como sucesso: significa que o número já não é membro (grupo inexistente ou
        // listagem-fantasma) — o objetivo de "não estar mais no grupo" está atingido.
        // 422/409/5xx NÃO são tolerados de propósito: num leave, esses códigos não garantem a saída,
        // e tolerá-los daria um falso "saiu" deixando o usuário ainda no grupo. Deixamos o erro subir
        // pra ele saber que não funcionou (a linha permanece, com a mensagem de erro).
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        resp.EnsureSuccessStatusCode();
    }

    public async Task<WahaGroup> JoinGroupByInviteAsync(string sessionId, string inviteCodeOrUrl, CancellationToken ct)
    {
        var code = WahaParsing.ExtractInviteCode(inviteCodeOrUrl);
        using var req = http.NewRequest(HttpMethod.Post, $"api/{WahaHttp.Esc(sessionId)}/groups/join");
        req.Content = JsonContent.Create(new { code }, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JoinResponseDto>(WahaHttp.Json, ct);

        // O id do grupo é o JID (<número>@g.us) — NUNCA o código do convite (base62, com letras).
        // Cair pro código aqui faria o import chamar /participants num JID inexistente e importar 0
        // EM SILÊNCIO. Se o join não trouxe um JID utilizável (resposta mínima de alguns engines),
        // resolvemos pelo /groups casando o nome — daí o import recebe o id certo.
        var name = body?.Name;
        if (WahaParsing.LooksLikeGroupJid(body?.Id))
        {
            return new WahaGroup(body!.Id!, name ?? string.Empty, null);
        }
        var resolved = await TryResolveJoinedGroupAsync(sessionId, name, ct);
        return new WahaGroup(resolved?.Id ?? string.Empty, resolved?.Name ?? name ?? string.Empty, null);
    }

    // Best-effort: descobre o JID do grupo recém-entrado casando o NOME no /groups (que só lista
    // grupos dos quais ainda sou membro). Casamento ambíguo (>1 com o mesmo nome) ou nenhum → null,
    // pra NUNCA devolver id errado — preferimos "sem id" (import desabilitado) a "id de outro grupo".
    private async Task<WahaGroup?> TryResolveJoinedGroupAsync(string sessionId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        try
        {
            // Lista CRUA (sem filtro de participação): logo após o join o /participants pode não ter
            // sincronizado, e o filtro esconderia o grupo recém-entrado → import 0 em silêncio.
            var groups = await ListGroupsRawAsync(sessionId, ct);
            var matches = groups
                .Where(g => !string.IsNullOrEmpty(g.Id)
                    && string.Equals(g.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
