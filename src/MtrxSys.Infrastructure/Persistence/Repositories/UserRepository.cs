using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Users;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(MtrxDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct)
    {
        var key = email.Trim().ToLowerInvariant();
        return db.Users.FirstOrDefaultAsync(u => u.Email == key, ct);
    }

    public async Task AddAsync(User user, CancellationToken ct) =>
        await db.Users.AddAsync(user, ct);

    public Task UpdateAsync(User user, CancellationToken ct)
    {
        if (db.Entry(user).State == EntityState.Detached)
        {
            db.Users.Update(user);
        }
        return Task.CompletedTask;
    }
}
