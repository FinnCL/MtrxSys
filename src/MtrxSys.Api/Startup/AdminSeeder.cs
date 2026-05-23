using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Users;
using MtrxSys.Infrastructure.Persistence;

namespace MtrxSys.Api.Startup;

public static class AdminSeeder
{
    public sealed class AdminSeedOptions
    {
        public const string SectionName = "Seed:Admin";
        public string Email { get; init; } = "admin@local";
        public string Password { get; init; } = "admin123!";
        public string DisplayName { get; init; } = "Admin";
    }

    public static async Task SeedAdminIfEmptyAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var db = services.GetRequiredService<MtrxDbContext>();
        if (await db.Users.AnyAsync(ct))
        {
            return;
        }

        var opts = services.GetRequiredService<IConfiguration>()
            .GetSection(AdminSeedOptions.SectionName)
            .Get<AdminSeedOptions>() ?? new AdminSeedOptions();

        var hasher = services.GetRequiredService<IPasswordHasher>();
        var users = services.GetRequiredService<IUserRepository>();
        var uow = services.GetRequiredService<IUnitOfWork>();
        var clock = services.GetRequiredService<IClock>();

        var user = User.Create(
            id: Guid.NewGuid(),
            email: opts.Email,
            passwordHash: hasher.Hash(opts.Password),
            displayName: opts.DisplayName,
            createdAt: clock.UtcNow);
        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        logger.LogWarning(
            "Seeded initial admin user {Email} (dev credentials — change in production)",
            opts.Email);
    }
}
