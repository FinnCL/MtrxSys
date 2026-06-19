using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

/// <summary>
/// Assina/verifica o token do link de opt-out de 1 clique. Stateless (nada no banco) e à prova de
/// forja: o token é <c>{contactId:N}.{expiraUnix}.{hmac}</c>, onde o HMAC-SHA256 cobre os dois
/// primeiros campos. Reusa a <c>Jwt:SigningKey</c> (já é por-stack e compartilhada entre api e
/// dispatcher) — o dispatcher assina ao compor a mensagem, a api verifica no endpoint /sair.
/// </summary>
public sealed class OptOutLinkSigner(IOptions<JwtOptions> jwt)
{
    public string Sign(Guid contactId, DateTimeOffset expiresAt)
    {
        var payload = $"{contactId:N}.{expiresAt.ToUnixTimeSeconds()}";
        return $"{payload}.{ComputeSig(payload)}";
    }

    /// <summary>Verifica assinatura E expiração. Retorna false (sem lançar) pra qualquer token
    /// malformado/adulterado/expirado — o endpoint trata como "link inválido".</summary>
    public bool TryVerify(string? token, DateTimeOffset now, out Guid contactId)
    {
        contactId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }
        var parts = token.Split('.');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var id)
            || !long.TryParse(parts[1], out var expUnix))
        {
            return false;
        }
        if (DateTimeOffset.FromUnixTimeSeconds(expUnix) < now)
        {
            return false; // expirado
        }
        var expected = Encoding.UTF8.GetBytes(ComputeSig($"{parts[0]}.{parts[1]}"));
        var received = Encoding.UTF8.GetBytes(parts[2]);
        if (expected.Length != received.Length || !CryptographicOperations.FixedTimeEquals(expected, received))
        {
            return false;
        }
        contactId = id;
        return true;
    }

    // Separação de domínio: a assinatura cobre um contexto fixo + o payload. Mesmo reusando a
    // Jwt:SigningKey, um token de link nunca colide/se confunde com outro uso da mesma chave.
    private const string Context = "mtrxsys.optout.v1";

    private string ComputeSig(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(jwt.Value.SigningKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{Context}.{payload}"));
        // base64url (URL-safe, sem padding) — entra direto em ?t=<token>.
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
