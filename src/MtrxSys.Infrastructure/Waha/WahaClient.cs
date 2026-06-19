using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Waha;

internal sealed class WahaClient(HttpClient http, IOptions<WahaOptions> opts) : IWahaClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WahaSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken ct)
    {
        // Delega pro snapshot (uma leitura só da sessão) e devolve apenas o status — evita duplicar
        // a mesma lógica de 404→Stopped / não-sucesso→Unknown / parse em dois métodos.
        return (await GetSessionSnapshotAsync(sessionId, ct)).Status;
    }

    public async Task<string?> ResolveLidToPhoneE164Async(string sessionId, string lid, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/lids/{Esc(lid)}");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await resp.Content.ReadFromJsonAsync<LidDto>(Json, ct);
        return PhoneFromChatId(body?.Pn); // "5511921404487@c.us" -> "+5511921404487"
    }

    private static string? PhoneFromChatId(string? chatId)
    {
        if (string.IsNullOrEmpty(chatId))
        {
            return null;
        }
        var at = chatId.IndexOf('@', StringComparison.Ordinal);
        var raw = at > 0 ? chatId[..at] : chatId;
        var digits = new string([.. raw.Where(char.IsDigit)]);
        return string.IsNullOrEmpty(digits) ? null : "+" + digits;
    }

    // Extrai o telefone real (+DDDnúmero) de um participante de grupo, tolerante às duas engines da
    // WAHA: a NOWEB expõe o número em `phoneNumber` (e o `id` vira o @lid oculto), enquanto a WEBJS
    // trazia no próprio `id` (objeto {user, server}). Retorna null pra quem não tem número real —
    // participante só-@lid ou pseudo-id sem dígito significativo —, que viraria contato-lixo.
    private static string? PhoneFromParticipant(JsonElement p)
    {
        if (p.TryGetProperty("phoneNumber", out var pn) && pn.ValueKind == JsonValueKind.String)
        {
            return RealPhoneOrNull(PhoneFromChatId(pn.GetString()));
        }
        if (!p.TryGetProperty("id", out var id))
        {
            return null;
        }
        if (id.ValueKind == JsonValueKind.String)
        {
            var raw = id.GetString() ?? string.Empty;
            return raw.Contains("@lid", StringComparison.OrdinalIgnoreCase)
                ? null
                : RealPhoneOrNull(PhoneFromChatId(raw));
        }
        if (id.ValueKind == JsonValueKind.Object)
        {
            var server = id.TryGetProperty("server", out var sv) ? sv.GetString() : null;
            if (string.Equals(server, "lid", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var user = id.TryGetProperty("user", out var us) ? us.GetString() : null;
            return RealPhoneOrNull(PhoneFromChatId(user));
        }
        return null;
    }

    // Descarta números sem nenhum dígito significativo (ex.: "+0", "+000"): pseudo-ids que
    // virariam contato-lixo no disparo. PhoneFromChatId já garante ao menos um dígito.
    private static string? RealPhoneOrNull(string? phone) =>
        phone is not null && phone.Any(ch => char.IsDigit(ch) && ch != '0') ? phone : null;

    // Admin do grupo, tolerante às duas engines: NOWEB usa `admin` ("admin"/"superadmin"),
    // WEBJS usava `role` ("ADMIN"/"SUPERADMIN").
    private static bool IsAdminRole(JsonElement p)
    {
        var role = (p.TryGetProperty("admin", out var a) ? a.GetString() : null)
            ?? (p.TryGetProperty("role", out var r) ? r.GetString() : null);
        return string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "superadmin", StringComparison.OrdinalIgnoreCase);
    }

    // Mapeia um grupo no formato NOWEB (objeto com `id` string, nome em `subject` e `participants`
    // inline) pro modelo do app. O número do grupo é o trecho antes do @ do JID.
    private static WahaGroup MapNowebGroup(JsonElement g)
    {
        var jid = g.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : string.Empty;
        var at = jid.IndexOf('@', StringComparison.Ordinal);
        var groupNumber = at > 0 ? jid[..at] : jid;
        var name = g.TryGetProperty("subject", out var subjEl) ? subjEl.GetString() ?? string.Empty : string.Empty;
        int? count = g.TryGetProperty("participants", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array
            ? partsEl.GetArrayLength()
            : null;
        return new WahaGroup(groupNumber, name, count);
    }

    public async Task<string?> GetOwnPhoneE164Async(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/sessions/{Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>(Json, ct);
        return PhoneFromChatId(body?.Me?.Id); // ex.: "5511999999999@c.us" -> "+5511999999999"
    }

    public async Task<WahaSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/sessions/{Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new WahaSessionSnapshot(WahaSessionStatus.Stopped, null);
        }
        if (!resp.IsSuccessStatusCode)
        {
            return new WahaSessionSnapshot(WahaSessionStatus.Unknown, null);
        }
        var body = await resp.Content.ReadFromJsonAsync<SessionDto>(Json, ct);
        var status = ParseStatus(body?.Status);
        var phone = PhoneFromChatId(body?.Me?.Id);
        var identity = phone is null
            ? null
            : new WahaIdentity(phone, string.IsNullOrWhiteSpace(body?.Me?.PushName) ? null : body!.Me!.PushName);
        return new WahaSessionSnapshot(status, identity);
    }

    public async Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, $"api/sessions/{Esc(sessionId)}/start");
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
        using var req = NewRequest(HttpMethod.Post, "api/sessions");
        // Proxy JÁ na criação: o WAHA conecta no WhatsApp logo no start; sem o proxy aqui, a 1ª
        // conexão sairia pelo IP da máquina (vazamento) antes de o ensurer aplicar o config depois.
        var proxy = ProxyConfigOrNull();
        object payload = proxy is null
            ? new { name = sessionId, start = true }
            : new { name = sessionId, start = true, config = new { proxy } };
        req.Content = JsonContent.Create(payload, options: Json);
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
        using var req = NewRequest(HttpMethod.Post, $"api/sessions/{Esc(sessionId)}/restart");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task LogoutSessionAsync(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, $"api/sessions/{Esc(sessionId)}/logout");
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
        using var req = NewRequest(HttpMethod.Delete, $"api/sessions/{Esc(sessionId)}");
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
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/auth/qr?format=image");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string> GetQrRawAsync(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/auth/qr?format=raw");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<QrRawDto>(Json, ct);
        return dto?.Value ?? string.Empty;
    }

    public async Task<string> RequestPairingCodeAsync(string sessionId, string phoneNumber, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, $"api/{Esc(sessionId)}/auth/request-code");
        req.Content = JsonContent.Create(new { phoneNumber }, options: Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<PairingCodeDto>(Json, ct);
        return dto?.Code ?? string.Empty;
    }

    public async Task<IReadOnlyList<WahaChat>> ListChatsOverviewAsync(string sessionId, int limit, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/chats/overview?limit={limit}");
        using var resp = await http.SendAsync(req, ct);
        // Histórico indisponível (ex.: NOWEB sem o "Store" → 400) = "sem chats", não erro fatal. O
        // sync vira no-op (nada a importar) em vez de estourar 500 no /api/waha/sync.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        var body = await resp.Content.ReadFromJsonAsync<List<ChatOverviewDto>>(Json, ct) ?? [];
        return body.Select(c => new WahaChat(
            Id: c.Id ?? string.Empty,
            Name: c.Name ?? c.Id ?? "(sem nome)",
            LastMessagePreview: c.LastMessage?.Body,
            LastMessageAt: c.LastMessage?.Timestamp is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
            IsGroup: IsGroupChat(c.Id))).ToList();
    }

    public async Task<IReadOnlyList<WahaMessage>> GetChatMessagesAsync(string sessionId, string chatId, int limit, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/chats/{Esc(chatId)}/messages?limit={limit}&downloadMedia=false");
        using var resp = await http.SendAsync(req, ct);
        // Mesma resiliência do overview: histórico indisponível (NOWEB sem store) → sem mensagens.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        var body = await resp.Content.ReadFromJsonAsync<List<MessageDto>>(Json, ct) ?? [];
        return body
            .OrderBy(m => m.Timestamp ?? 0)
            .Select(m => new WahaMessage(
                Id: m.Id ?? string.Empty,
                ChatId: chatId,
                FromMe: m.FromMe ?? false,
                Author: m.Author ?? m.From ?? "",
                Body: m.Body ?? string.Empty,
                Timestamp: DateTimeOffset.FromUnixTimeSeconds(m.Timestamp ?? 0)))
            .ToList();
    }

    public async Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/groups");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
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
                .Select(prop => MapNowebGroup(prop.Value))
                .Where(g => !string.IsNullOrEmpty(g.Id))
                .ToList();
        }

        var body = root.Deserialize<List<GroupDto>>(Json) ?? [];

        // Esconde grupos onde o número conectado já não é mais membro (saiu pelo celular). A WAHA
        // continua listando esse grupo enquanto o chat não for "Apagar conversa" no celular —
        // sem o filtro, ele aparece aqui como se você ainda participasse. Tudo é melhor-esforço:
        // se não der pra decidir com certeza, preserva o grupo (preferimos exibir um a mais do
        // que esconder um real).
        string? ownDigits = null;
        try
        {
            var ownE164 = await GetOwnPhoneE164Async(sessionId, ct);
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
                var groupJid = EnsureGroupJid(groupKey);
                var isMember = await IsCurrentMemberOfAsync(sessionId, groupJid, ownDigits, ct);
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

    // Decide se o número conectado ainda é participante deste grupo. Lê direto a resposta crua
    // da WAHA (sem o filtro de @lid que `ListGroupParticipantsAsync` aplica), pra que o próprio
    // número, se vier mascarado como LID, ainda detone o fallback ambíguo em vez de marcar como
    // "não-membro" por engano.
    // Observado empiricamente na WEBJS: pra um grupo onde você participa, a WAHA devolve a
    // lista completa (incluindo você); pra um grupo do qual você saiu, devolve LISTA VAZIA
    // (você não tem mais visibilidade dos membros). Lista vazia = saí.
    // Só preserva (devolve true) em ambiguidade real: erro HTTP ou lista populada só com LIDs.
    private async Task<bool> IsCurrentMemberOfAsync(string sessionId, string groupJid, string ownDigits, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/groups/{Esc(groupJid)}/participants");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return true;
        }
        var body = await resp.Content.ReadFromJsonAsync<List<ParticipantDto>>(Json, ct) ?? [];
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
        var groupJid = EnsureGroupJid(groupId);
        using var req = NewRequest(HttpMethod.Get, $"api/{Esc(sessionId)}/groups/{Esc(groupJid)}/participants");
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
            var phone = PhoneFromParticipant(p);
            if (phone is null)
            {
                continue;
            }
            var name = p.TryGetProperty("pushName", out var nmEl) ? nmEl.GetString() : null;
            result.Add(new WahaParticipant(
                Id: phone.TrimStart('+'),
                PhoneE164: phone,
                Name: string.IsNullOrWhiteSpace(name) ? null : name,
                IsAdmin: IsAdminRole(p)));
        }
        return result;
    }

    public async Task LeaveGroupAsync(string sessionId, string groupId, CancellationToken ct)
    {
        var groupJid = EnsureGroupJid(groupId);
        using var req = NewRequest(HttpMethod.Post, $"api/{Esc(sessionId)}/groups/{Esc(groupJid)}/leave");
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
        var code = ExtractInviteCode(inviteCodeOrUrl);
        using var req = NewRequest(HttpMethod.Post, $"api/{Esc(sessionId)}/groups/join");
        req.Content = JsonContent.Create(new { code }, options: Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JoinResponseDto>(Json, ct);

        // O id do grupo é o JID (<número>@g.us) — NUNCA o código do convite (base62, com letras).
        // Cair pro código aqui faria o import chamar /participants num JID inexistente e importar 0
        // EM SILÊNCIO. Se o join não trouxe um JID utilizável (resposta mínima de alguns engines),
        // resolvemos pelo /groups casando o nome — daí o import recebe o id certo.
        var name = body?.Name;
        if (LooksLikeGroupJid(body?.Id))
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

    // Um id de GRUPO é o JID (contém @g.us) ou o número do grupo (só dígitos, formato novo; dígitos
    // com '-' no legado). O código do convite é base62 (tem letras) e não casa nenhum — é assim que
    // distinguimos um id de grupo real de um código de convite indevidamente colocado no lugar.
    private static bool LooksLikeGroupJid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }
        if (id.Contains("@g.us", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return id.Length >= 15 && id.All(c => char.IsDigit(c) || c == '-');
    }

    public async Task<string> SendTextAsync(string sessionId, string phoneOrChatId, string text, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, "api/sendText");
        req.Content = JsonContent.Create(new
        {
            chatId = ToChatId(phoneOrChatId),
            text,
            session = sessionId,
        }, options: Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await ReadSentMessageIdAsync(resp, ct);
    }

    public async Task<string> SendImageAsync(
        string sessionId, string phoneOrChatId, byte[] imageData, string mimeType, string caption, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, "api/sendImage");
        req.Content = JsonContent.Create(new
        {
            chatId = ToChatId(phoneOrChatId),
            caption,
            file = new
            {
                data = Convert.ToBase64String(imageData),
                mimetype = mimeType,
                filename = "image" + ExtensionFor(mimeType),
            },
            session = sessionId,
        }, options: Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await ReadSentMessageIdAsync(resp, ct);
    }

    // O sucesso do ENVIO já foi decidido pelo status HTTP (EnsureSuccessStatusCode acima); extrair
    // o id da mensagem é best-effort e NÃO pode lançar. A forma da resposta varia entre engines
    // (NOWEB devolve `id` como string ou objeto {id, _serialized}; WEBJS, objeto) — um formato
    // inesperado retorna id vazio, jamais exceção: a mensagem já saiu (irreversível) e lançar aqui
    // a marcaria como falha, gerando reenvio duplicado no retry.
    private static async Task<string> ReadSentMessageIdAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("id", out var id))
            {
                return string.Empty;
            }
            if (id.ValueKind == JsonValueKind.String)
            {
                return id.GetString() ?? string.Empty;
            }
            if (id.ValueKind == JsonValueKind.Object)
            {
                if (id.TryGetProperty("_serialized", out var ser) && ser.ValueKind == JsonValueKind.String)
                {
                    return ser.GetString() ?? string.Empty;
                }
                if (id.TryGetProperty("id", out var inner) && inner.ValueKind == JsonValueKind.String)
                {
                    return inner.GetString() ?? string.Empty;
                }
            }
            return string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            // Corpo ausente/ilegível ou formato inesperado: id vazio, sem comprometer o envio.
            return string.Empty;
        }
#pragma warning restore CA1031
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".bin",
    };

    public Task StartTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct) =>
        PostPresenceAsync(sessionId, phoneOrChatId, "api/startTyping", ct);

    public Task StopTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct) =>
        PostPresenceAsync(sessionId, phoneOrChatId, "api/stopTyping", ct);

    public async Task<bool> EnsureWebhookConfiguredAsync(string sessionId, string webhookUrl, IReadOnlyList<string> events, CancellationToken ct)
    {
        using var getReq = NewRequest(HttpMethod.Get, $"api/sessions/{Esc(sessionId)}?all=true");
        using var getResp = await http.SendAsync(getReq, ct);
        if (getResp.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        getResp.EnsureSuccessStatusCode();
        using var jsonStream = await getResp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
        var root = doc.RootElement;

        var webhookPresent = WebhookPresent(root, webhookUrl);
        // O proxy só vale na (re)conexão e SÓ pega via config de sessão (a env var WHATSAPP_PROXY_SERVER
        // é ignorada no WAHA 2026.x CORE/NOWEB — comprovado). Compara o que está gravado com o desejado.
        var desiredProxy = NormalizedProxyServer();
        var currentProxy = CurrentProxyServer(root);
        var proxyMatches = string.Equals(desiredProxy, currentProxy, StringComparison.OrdinalIgnoreCase);

        // Nada a fazer: webhook já no lugar e proxy já bate (inclusive ambos ausentes).
        if (webhookPresent && proxyMatches)
        {
            return true;
        }

        // PUT substitui o config — então mandamos webhook E proxy juntos, pra um não apagar o outro.
        var proxy = ProxyConfigOrNull();
        object config = proxy is null
            ? new { webhooks = BuildWebhooks(webhookUrl, events) }
            : new { webhooks = BuildWebhooks(webhookUrl, events), proxy };

        using var putReq = NewRequest(HttpMethod.Put, $"api/sessions/{Esc(sessionId)}");
        putReq.Content = JsonContent.Create(new { name = sessionId, config }, options: Json);
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

    private static object[] BuildWebhooks(string webhookUrl, IReadOnlyList<string> events) =>
    [
        new
        {
            url = webhookUrl,
            events = events.ToArray(),
            retries = new { delaySeconds = 2, attempts = 3 },
        },
    ];

    private static bool WebhookPresent(JsonElement root, string webhookUrl)
    {
        if (root.TryGetProperty("config", out var cfg) &&
            cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("webhooks", out var hooks) &&
            hooks.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in hooks.EnumerateArray())
            {
                if (entry.TryGetProperty("url", out var urlProp) &&
                    string.Equals(urlProp.GetString(), webhookUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Lê config.proxy.server da sessão (null se não houver proxy configurado).
    private static string? CurrentProxyServer(JsonElement root)
    {
        if (root.TryGetProperty("config", out var cfg) &&
            cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            proxy.TryGetProperty("server", out var server) &&
            server.ValueKind == JsonValueKind.String)
        {
            return NormalizeServer(server.GetString());
        }
        return null;
    }

    // Objeto de proxy pro config de sessão do WAHA (null = sem proxy). Inclui credenciais só quando
    // houver — proxy sem auth manda só o server.
    private object? ProxyConfigOrNull()
    {
        var server = NormalizedProxyServer();
        if (server is null)
        {
            return null;
        }
        var user = opts.Value.ProxyUsername;
        var pass = opts.Value.ProxyPassword;
        // Só manda credenciais se as DUAS existirem. Meia-credencial (só user ou só pass) cairia
        // como "server sem auth" — que falha de forma VISÍVEL (chip não conecta) em vez de enviar
        // um "password":null malformado. O scripts/check-proxy-env.ps1 já alerta o .env meio-preenchido.
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            return new { server, username = user, password = pass };
        }
        return new { server };
    }

    private string? NormalizedProxyServer() => NormalizeServer(opts.Value.ProxyServer);

    // host:porta sem espaços e sem esquema (o WAHA quer "host:porta", não "http://host:porta").
    private static string? NormalizeServer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var s = raw.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }
        return s.Length == 0 ? null : s;
    }

    // Religa a sessão pra o proxy novo valer. Tolerante: 404/422/409 (sessão inexistente/em estado
    // que recusa restart) não derruba o startup — o proxy já está gravado no config e vale no próximo start.
    private async Task TryRestartForProxyAsync(string sessionId, CancellationToken ct)
    {
        // Melhor-esforço: não chamamos EnsureSuccessStatusCode de propósito — qualquer status (incl.
        // 404/422/409 de sessão inexistente/em estado que recusa restart) é tolerado; o proxy já está
        // gravado no config e vale no próximo start. Só não engolimos o cancelamento.
        using var req = NewRequest(HttpMethod.Post, $"api/sessions/{Esc(sessionId)}/restart");
        using var resp = await http.SendAsync(req, ct);
    }

    private async Task PostPresenceAsync(string sessionId, string phoneOrChatId, string path, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, path);
        req.Content = JsonContent.Create(new
        {
            chatId = ToChatId(phoneOrChatId),
            session = sessionId,
        }, options: Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relativeUri)
    {
        var req = new HttpRequestMessage(method, relativeUri);
        if (!string.IsNullOrWhiteSpace(opts.Value.ApiKey))
        {
            req.Headers.Add("X-Api-Key", opts.Value.ApiKey);
        }
        return req;
    }

    private static string Esc(string segment) => Uri.EscapeDataString(segment);

    // Garante o JID completo do grupo: a WAHA exige <numero>@g.us, mas no app circula só o número.
    private static string EnsureGroupJid(string groupId) =>
        groupId.Contains('@', StringComparison.Ordinal) ? groupId : groupId + "@g.us";

    private static bool IsGroupChat(string? id) =>
        id is not null && WahaChatIdentifier.IsGroup(id);

    internal static string ToChatId(string phoneOrChatId)
    {
        if (phoneOrChatId.Contains('@', StringComparison.Ordinal))
        {
            return phoneOrChatId;
        }
        var digits = phoneOrChatId.TrimStart('+');
        return digits + WahaChatIdentifier.IndividualSuffix;
    }

    private static string ExtractInviteCode(string inviteCodeOrUrl)
    {
        if (Uri.TryCreate(inviteCodeOrUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("whatsapp.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Segments.LastOrDefault()?.Trim('/') ?? inviteCodeOrUrl;
        }
        return inviteCodeOrUrl.Trim();
    }

    private static WahaSessionStatus ParseStatus(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return WahaSessionStatus.Unknown;
        if (raw.Equals("STOPPED", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Stopped;
        if (raw.Equals("STARTING", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Starting;
        if (raw.Equals("SCAN_QR_CODE", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.ScanQrCode;
        if (raw.Equals("WORKING", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Working;
        if (raw.Equals("FAILED", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Failed;
        return WahaSessionStatus.Unknown;
    }

    private sealed record SessionDto(string? Name, string? Status, MeDto? Me);
    private sealed record MeDto(string? Id, string? PushName);
    private sealed record LidDto(string? Lid, string? Pn);
    private sealed record QrRawDto(string? Value);
    private sealed record PairingCodeDto(string? Code);
    private sealed record ChatLastMessageDto(string? Body, long? Timestamp);
    private sealed record ChatOverviewDto(string? Id, string? Name, ChatLastMessageDto? LastMessage);
    private sealed record MessageDto(string? Id, string? From, string? Author, bool? FromMe, string? Body, long? Timestamp);
    private sealed record GroupIdDto(string? Server, string? User, string? RawId);
    private sealed record GroupDto(GroupIdDto? Id, string? Name, List<ParticipantDto>? Participants);
    private sealed record ParticipantDto(GroupIdDto? Id, string? PushName, string? Role);
    private sealed record JoinResponseDto(string? Id, string? Name);
}
