using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

/// <summary>
/// Assina/verifica o token do link de opt-out de 1 clique. Stateless (nada no banco) e à prova de
/// forja: o token é <c>{contactId}.{expiraUnix}.{hmac}</c>, onde o HMAC-SHA256 cobre os dois primeiros
/// campos. Reusa a <c>Jwt:SigningKey</c> (já é por-stack e compartilhada entre api e dispatcher) — o
/// dispatcher assina ao compor a mensagem, a api verifica no endpoint /sair.
/// <para>
/// Formato CURTO (~56 chars): contactId em base64url (16 bytes crus = 22 chars, no lugar de 32 hex) e
/// tag HMAC TRUNCADA a 128 bits (22 chars, no lugar de 43) — encurta o token ~35% sem enfraquecer
/// (128 bits de tag HMAC é padrão, inforjável na prática). O <see cref="TryVerify"/> aceita TAMBÉM o
/// formato ANTIGO (32-hex + tag de 256 bits) pra não invalidar links já enviados (validade 90d).
/// </para>
/// </summary>
public sealed class OptOutLinkSigner(IOptions<JwtOptions> jwt)
{
    // Tag HMAC truncada a 128 bits. RFC 2104 permite truncar; 128 bits sobra pra um link de opt-out.
    private const int TagBytes = 16;
    private const string Context = "mtrxsys.optout.v1";

    public string Sign(Guid contactId, DateTimeOffset expiresAt)
    {
        var payload = $"{ToBase64Url(contactId.ToByteArray())}.{expiresAt.ToUnixTimeSeconds()}";
        return $"{payload}.{ToBase64Url(FullHash(payload)[..TagBytes])}";
    }

    /// <summary>Verifica assinatura E expiração. Retorna false (sem lançar) pra qualquer token
    /// malformado/adulterado/expirado — o endpoint trata como "link inválido". Aceita o formato curto
    /// (novo) e o longo (antigo), pra links de 90d emitidos antes da troca continuarem valendo.</summary>
    public bool TryVerify(string? token, DateTimeOffset now, out Guid contactId)
    {
        contactId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        var parts = token.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var expUnix))
        {
            return false;
        }
        if (!TryParseContactId(parts[0], out var id))
        {
            return false;
        }
        if (DateTimeOffset.FromUnixTimeSeconds(expUnix) < now)
        {
            return false; // expirado
        }
        if (!TagMatches($"{parts[0]}.{parts[1]}", parts[2]))
        {
            return false;
        }
        contactId = id;
        return true;
    }

    // Separação de domínio: a assinatura cobre um contexto fixo + o payload. Mesmo reusando a
    // Jwt:SigningKey, um token de link nunca colide/se confunde com outro uso da mesma chave.
    // HashData (estático one-shot): não aloca objeto HMAC por chamada — idioma moderno do .NET.
    private byte[] FullHash(string payload) =>
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(jwt.Value.SigningKey),
            Encoding.UTF8.GetBytes($"{Context}.{payload}"));

    // Aceita a tag NOVA (128-bit) e a ANTIGA (256-bit): links já enviados no formato antigo continuam
    // válidos. Compara em tempo fixo os dois candidatos (sem early-out) — não vaza qual casou.
    private bool TagMatches(string payload, string received)
    {
        var full = FullHash(payload);
        var receivedBytes = Encoding.UTF8.GetBytes(received);
        var newTag = Encoding.UTF8.GetBytes(ToBase64Url(full[..TagBytes]));
        var oldTag = Encoding.UTF8.GetBytes(ToBase64Url(full));
        var matchNew = receivedBytes.Length == newTag.Length
            && CryptographicOperations.FixedTimeEquals(receivedBytes, newTag);
        var matchOld = receivedBytes.Length == oldTag.Length
            && CryptographicOperations.FixedTimeEquals(receivedBytes, oldTag);
        return matchNew || matchOld;
    }

    // contactId: base64url de 16 bytes (novo) OU 32 hex (antigo). Sem ambiguidade: 32-hex casa o Guid
    // "N" e não decodifica pra 16 bytes; o base64url de 16 bytes (22 chars) não é hex de 32.
    private static bool TryParseContactId(string s, out Guid id)
    {
        if (Guid.TryParseExact(s, "N", out id))
        {
            return true; // formato antigo (32 hex)
        }
        if (TryFromBase64Url(s, out var bytes) && bytes.Length == 16)
        {
            id = new Guid(bytes);
            return true;
        }
        id = Guid.Empty;
        return false;
    }

    // base64url (URL-safe, sem padding) — entra direto em ?t=<token>.
    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string s, out byte[] bytes)
    {
        bytes = [];
        var b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 1: return false; // comprimento impossível pra base64
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        try
        {
            bytes = Convert.FromBase64String(b64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
