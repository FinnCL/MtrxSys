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
        req.Content = JsonContent.Create(new { name = sessionId, start = true }, options: Json);
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
                var groupJid = groupKey.Contains('@', StringComparison.Ordinal) ? groupKey : groupKey + "@g.us";
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
        var groupJid = groupId.Contains('@', StringComparison.Ordinal) ? groupId : groupId + "@g.us";
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

    public async Task<WahaGroup> JoinGroupByInviteAsync(string sessionId, string inviteCodeOrUrl, CancellationToken ct)
    {
        var code = ExtractInviteCode(inviteCodeOrUrl);
        using var req = NewRequest(HttpMethod.Post, $"api/{Esc(sessionId)}/groups/join");
        req.Content = JsonContent.Create(new { code }, options: Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JoinResponseDto>(Json, ct);
        return new WahaGroup(body?.Id ?? code, body?.Name ?? "", null);
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

        var payload = new
        {
            name = sessionId,
            config = new
            {
                webhooks = new[]
                {
                    new
                    {
                        url = webhookUrl,
                        events = events.ToArray(),
                        retries = new { delaySeconds = 2, attempts = 3 },
                    },
                },
            },
        };

        using var putReq = NewRequest(HttpMethod.Put, $"api/sessions/{Esc(sessionId)}");
        putReq.Content = JsonContent.Create(payload, options: Json);
        using var putResp = await http.SendAsync(putReq, ct);
        putResp.EnsureSuccessStatusCode();
        return true;
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
    private sealed record ChatLastMessageDto(string? Body, long? Timestamp);
    private sealed record ChatOverviewDto(string? Id, string? Name, ChatLastMessageDto? LastMessage);
    private sealed record MessageDto(string? Id, string? From, string? Author, bool? FromMe, string? Body, long? Timestamp);
    private sealed record GroupIdDto(string? Server, string? User, string? RawId);
    private sealed record GroupDto(GroupIdDto? Id, string? Name, List<ParticipantDto>? Participants);
    private sealed record ParticipantDto(GroupIdDto? Id, string? PushName, string? Role);
    private sealed record JoinResponseDto(string? Id, string? Name);
}
