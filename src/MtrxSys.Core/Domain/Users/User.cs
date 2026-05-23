using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Users;

public sealed class User : Entity<Guid>
{
    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    private User() { }

    public static User Create(Guid id, string email, string passwordHash, string displayName, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new User
        {
            Id = id,
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = passwordHash,
            DisplayName = displayName,
            CreatedAt = createdAt,
        };
    }

    public void RegisterLogin(DateTimeOffset at) => LastLoginAt = at;

    public void RotatePasswordHash(string newHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);
        PasswordHash = newHash;
    }
}
