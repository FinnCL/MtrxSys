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
    public async Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/groups");
        using var resp = await http.SendAsync(req, ct);
        // Sessão desconectada/não-WORKING: o WAHA responde 422. Degrada pra lista vazia (a aba "Grupos"
        // mostra "nenhum grupo") em vez de estourar 500 — que ainda apareceria no browser como erro de
        // CORS (o header some na resposta de erro). Mesmo padrão do GetMessagesAsync.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        // Engine NOWEB: /groups devolve um OBJETO indexado por JID ({ "<jid>@g.us": {...} }), com o
        // nome em `subject` e os `participants` inline — e só lista grupos dos quais ainda sou
        // membro. Mapeia direto, sem as chamadas extras de /participants que a WEBJS exige pra
        // decidir a participação. (WEBJS devolvia um ARRAY; tratado no caminho abaixo.)
        if (root.ValueKind == JsonValueKind.Object)
        {
            return root.EnumerateObject()
                .Select(prop => WahaParsing.MapNowebGroup(prop.Value))
                .Where(g => !string.IsNullOrEmpty(g.Id))
                .ToList();
        }

        var body = root.Deserialize<List<GroupDto>>(WahaHttp.Json) ?? [];

        // Esconde grupos onde o número conectado já não é mais membro (saiu pelo celular). A WAHA
        // continua listando esse grupo enquanto o chat não for "Apagar conversa" no celular —
        // sem o filtro, ele aparece aqui como se você ainda participasse. Tudo é melhor-esforço:
        // se não der pra decidir com certeza, preserva o grupo (preferimos exibir um a mais do
        // que esconder um real).
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
            // sessão degradada ou JSON inesperado: cai no fallback "não filtra".
        }
#pragma warning restore CA1031

        // Sem o próprio número (ou nenhum grupo retornado), nada a filtrar — devolve cru.
        if (string.IsNullOrEmpty(ownDigits) || body.Count == 0)
        {
            return body
                .Select(g => new WahaGroup(g.Id?.User ?? g.Id?.Server ?? "", g.Name ?? "", g.Participants?.Count))
                .ToList();
        }

        // A WAHA WEBJS NÃO inclui o array `Participants` no /groups overview (vem null), então
        // pra decidir se ainda sou membro precisamos consultar /groups/{id}/participants de
        // cada grupo. Em paralelo pra não serializar latência — uso esperado é localhost com
        // poucos grupos.
        var checks = await Task.WhenAll(body.Select(async g =>
        {
            var groupKey = g.Id?.User ?? g.Id?.Server ?? "";
            if (string.IsNullOrEmpty(groupKey))
            {
                return (Group: g, IsMember: true);
            }
            try
            {
                var groupJid = WahaParsing.EnsureGroupJid(groupKey);
                var isMember = await CachedIsMemberAsync(sessionId, groupJid, ownDigits, ct);
                return (Group: g, IsMember: isMember);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch
            {
                // falha na consulta deste grupo: mantém na lista (preserva comportamento atual)
                return (Group: g, IsMember: true);
            }
#pragma warning restore CA1031
        }));

        return checks
            .Where(c => c.IsMember)
            .Select(c => new WahaGroup(c.Group.Id?.User ?? c.Group.Id?.Server ?? "", c.Group.Name ?? "", c.Group.Participants?.Count))
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

    // Decide se o número conectado ainda é participante deste grupo. Lê direto a resposta crua
    // da WAHA (sem o filtro de @lid que ListGroupParticipantsAsync aplica), pra que o próprio
    // número, se vier mascarado como LID, ainda detone o fallback ambíguo em vez de marcar como
    // "não-membro" por engano.
    // Observado empiricamente na WEBJS: pra um grupo onde você participa, a WAHA devolve a
    // lista completa (incluindo você); pra um grupo do qual você saiu, devolve LISTA VAZIA
    // (você não tem mais visibilidade dos membros). Lista vazia = saí.
    // Só preserva (devolve true) em ambiguidade real: erro HTTP ou lista populada só com LIDs.
    private async Task<bool> IsCurrentMemberOfAsync(string sessionId, string groupJid, string ownDigits, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/groups/{WahaHttp.Esc(groupJid)}/participants");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return true;
        }
        var body = await resp.Content.ReadFromJsonAsync<List<ParticipantDto>>(WahaHttp.Json, ct) ?? [];
        if (body.Count == 0)
        {
            return false;
        }
        var hasNonLidParticipant = body.Any(p => p.Id is not null
            && !string.Equals(p.Id.Server, "lid", StringComparison.OrdinalIgnoreCase)
            && !(p.Id.RawId?.Contains("@lid", StringComparison.OrdinalIgnoreCase) ?? false));
        if (!hasNonLidParticipant)
        {
            return true;
        }
        return body.Any(p => string.Equals(p.Id?.User, ownDigits, StringComparison.Ordinal));
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
            var groups = await ListGroupsAsync(sessionId, ct);
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
