using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Auth;

internal sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 11;

    public string Hash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return BCrypt.Net.BCrypt.HashPassword(plaintext, WorkFactor);
    }

    public bool Verify(string plaintext, string hash)
    {
        if (string.IsNullOrEmpty(plaintext) || string.IsNullOrEmpty(hash))
        {
            return false;
        }
        try
        {
            return BCrypt.Net.BCrypt.Verify(plaintext, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
