using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest req,
            IUserRepository users,
            IPasswordHasher hasher,
            ITokenService tokens,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            {
                return Results.Problem("email and password are required", statusCode: 400);
            }

            var user = await users.GetByEmailAsync(req.Email, ct);
            if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
            {
                return Results.Problem("invalid credentials", statusCode: 401);
            }

            user.RegisterLogin(clock.UtcNow);
            await users.UpdateAsync(user, ct);
            await uow.SaveChangesAsync(ct);

            var token = tokens.Issue(user);
            return Results.Ok(new LoginResponse(token.Token, token.ExpiresAt, user.Id, user.Email, user.DisplayName));
        });

        return app;
    }

    public sealed record LoginRequest(string Email, string Password);

    public sealed record LoginResponse(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        Guid UserId,
        string Email,
        string DisplayName);
}
