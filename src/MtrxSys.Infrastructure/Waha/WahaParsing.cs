using System.Text.Json;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Helpers PUROS de parsing/mapeamento da WAHA (sem HTTP): JID↔telefone, mapeamento de
/// grupos/participantes tolerante às engines NOWEB/WEBJS, status, e leitura do id da mensagem enviada.
/// Extraídos do WahaClient pra separar "interpretar a resposta" de "fazer a chamada" — e dá pra testar
/// isolado.</summary>
internal static class WahaParsing
{
    public static string? PhoneFromChatId(string? chatId)
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
    public static string? PhoneFromParticipant(JsonElement p)
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
    public static string? RealPhoneOrNull(string? phone) =>
        phone is not null && phone.Any(ch => char.IsDigit(ch) && ch != '0') ? phone : null;

    // Admin do grupo, tolerante às duas engines: NOWEB usa `admin` ("admin"/"superadmin"),
    // WEBJS usava `role` ("ADMIN"/"SUPERADMIN").
    public static bool IsAdminRole(JsonElement p)
    {
        var role = (p.TryGetProperty("admin", out var a) ? a.GetString() : null)
            ?? (p.TryGetProperty("role", out var r) ? r.GetString() : null);
        return string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "superadmin", StringComparison.OrdinalIgnoreCase);
    }

    // Mapeia um grupo no formato NOWEB (objeto com `id` string, nome em `subject` e `participants`
    // inline) pro modelo do app. O número do grupo é o trecho antes do @ do JID.
    public static WahaGroup MapNowebGroup(JsonElement g)
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

    public static bool IsGroupChat(string? id) =>
        id is not null && WahaChatIdentifier.IsGroup(id);

    public static string ToChatId(string phoneOrChatId)
    {
        if (phoneOrChatId.Contains('@', StringComparison.Ordinal))
        {
            return phoneOrChatId;
        }
        var digits = phoneOrChatId.TrimStart('+');
        return digits + WahaChatIdentifier.IndividualSuffix;
    }

    // Garante o JID completo do grupo: a WAHA exige <numero>@g.us, mas no app circula só o número.
    public static string EnsureGroupJid(string groupId) =>
        groupId.Contains('@', StringComparison.Ordinal) ? groupId : groupId + "@g.us";

    public static string ExtractInviteCode(string inviteCodeOrUrl)
    {
        if (Uri.TryCreate(inviteCodeOrUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("whatsapp.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Segments.LastOrDefault()?.Trim('/') ?? inviteCodeOrUrl;
        }
        return inviteCodeOrUrl.Trim();
    }

    // Um id de GRUPO é o JID (contém @g.us) ou o número do grupo (só dígitos, formato novo; dígitos
    // com '-' no legado). O código do convite é base62 (tem letras) e não casa nenhum — é assim que
    // distinguimos um id de grupo real de um código de convite indevidamente colocado no lugar.
    public static bool LooksLikeGroupJid(string? id)
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

    public static WahaSessionStatus ParseStatus(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return WahaSessionStatus.Unknown;
        if (raw.Equals("STOPPED", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Stopped;
        if (raw.Equals("STARTING", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Starting;
        if (raw.Equals("SCAN_QR_CODE", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.ScanQrCode;
        if (raw.Equals("WORKING", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Working;
        if (raw.Equals("FAILED", StringComparison.OrdinalIgnoreCase)) return WahaSessionStatus.Failed;
        return WahaSessionStatus.Unknown;
    }

    public static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".bin",
    };

    // Confere se a sessão já tem o webhook da URL configurado. Quando um token é esperado exige
    // o customHeader X-Webhook-Token com o valor certo. Assim uma sessão antiga que tem a URL mas não
    // o header (ou com token defasado) não é considerada "pronta" — força regravar o config.
    public static bool WebhookConfigured(JsonElement root, string webhookUrl, string? token)
    {
        if (!root.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object ||
            !cfg.TryGetProperty("webhooks", out var hooks) || hooks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var entry in hooks.EnumerateArray())
        {
            if (!entry.TryGetProperty("url", out var urlProp) ||
                !string.Equals(urlProp.GetString(), webhookUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return string.IsNullOrWhiteSpace(token) || HasWebhookHeader(entry, "X-Webhook-Token", token);
        }
        return false;
    }

    private static bool HasWebhookHeader(JsonElement webhookEntry, string name, string value)
    {
        if (!webhookEntry.TryGetProperty("customHeaders", out var headers) || headers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var h in headers.EnumerateArray())
        {
            if (h.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase) &&
                h.TryGetProperty("value", out var v) &&
                string.Equals(v.GetString(), value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // Lê config.proxy.server da sessão (null se não houver proxy configurado).
    public static string? CurrentProxyServer(JsonElement root)
    {
        if (root.TryGetProperty("config", out var cfg) &&
            cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("proxy", out var proxy) &&
            proxy.ValueKind == JsonValueKind.Object &&
            proxy.TryGetProperty("server", out var server) &&
            server.ValueKind == JsonValueKind.String)
        {
            return WahaHttp.NormalizeServer(server.GetString());
        }
        return null;
    }

    // Monta o config de webhook da sessão. Com token, adiciona o customHeader X-Webhook-Token pra o
    // WAHA REAL enviá-lo em cada callback — sem isso o endpoint (que valida o token) rejeitaria todo
    // inbound. Sem token, omite customHeaders (o endpoint então não exige — modo dev/aberto).
    public static object[] BuildWebhooks(string webhookUrl, IReadOnlyList<string> events, string? token = null)
    {
        var retries = new { delaySeconds = 2, attempts = 3 };
        if (string.IsNullOrWhiteSpace(token))
        {
            return [new { url = webhookUrl, events = events.ToArray(), retries }];
        }
        return
        [
            new
            {
                url = webhookUrl,
                events = events.ToArray(),
                customHeaders = new[] { new { name = "X-Webhook-Token", value = token } },
                retries,
            },
        ];
    }

    // O sucesso do ENVIO já foi decidido pelo status HTTP (EnsureSuccessStatusCode no chamador); extrair
    // o id da mensagem é best-effort e NÃO pode lançar. A forma da resposta varia entre engines
    // (NOWEB devolve `id` como string ou objeto {id, _serialized}; WEBJS, objeto) — um formato
    // inesperado retorna id vazio, jamais exceção: a mensagem já saiu (irreversível) e lançar aqui
    // a marcaria como falha, gerando reenvio duplicado no retry.
    public static async Task<string> ReadSentMessageIdAsync(HttpResponseMessage resp, CancellationToken ct)
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
}
