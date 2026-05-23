using MtrxSys.Core.Domain.Users;

namespace MtrxSys.Core.Application.Abstractions;

public interface ITokenService
{
    AccessToken Issue(User user);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
