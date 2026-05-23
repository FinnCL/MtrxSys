namespace MtrxSys.Core.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string plaintext);
    bool Verify(string plaintext, string hash);
}
