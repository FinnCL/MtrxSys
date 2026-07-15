using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Warmup;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class WarmupCircleRepository(MtrxDbContext db) : IWarmupCircleRepository
{
    public async Task<IReadOnlyList<WarmupCircleMember>> ListAsync(CancellationToken ct) =>
        await db.WarmupCircle.AsNoTracking().OrderBy(m => m.AddedAt).ToListAsync(ct);

    // SEM AsNoTracking: quem chama pode mutar (Rename) e salvar pelo UoW.
    public Task<WarmupCircleMember?> GetByPhoneAsync(string phoneE164, CancellationToken ct) =>
        db.WarmupCircle.FirstOrDefaultAsync(m => m.PhoneE164 == phoneE164, ct);

    public async Task AddAsync(WarmupCircleMember member, CancellationToken ct) =>
        await db.WarmupCircle.AddAsync(member, ct);

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        var existing = await db.WarmupCircle.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (existing is null)
        {
            return false;
        }
        db.WarmupCircle.Remove(existing);
        return true;
    }
}
