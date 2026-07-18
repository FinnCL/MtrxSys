using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Funnel;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class FunnelInviteRepository(MtrxDbContext db) : IFunnelInviteRepository
{
    public async Task AddAsync(FunnelInvite invite, CancellationToken ct) =>
        await db.FunnelInvites.AddAsync(invite, ct);

    // Convite aberto (ainda não engajou) mais recente do contato — o que a auto-resposta usa.
    public Task<FunnelInvite?> GetOpenByContactIdAsync(Guid contactId, CancellationToken ct) =>
        db.FunnelInvites
            .Where(f => f.ContactId == contactId && f.EngagedAt == null)
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);

    // Convites abertos destes contatos num único SELECT (rastreados: o chamador atualiza os textos e
    // o SaveChanges persiste). Sem AsNoTracking de propósito — a geração reusa e MUTA estes convites.
    public async Task<IReadOnlyList<FunnelInvite>> ListOpenByContactIdsAsync(
        IReadOnlyCollection<Guid> contactIds, CancellationToken ct)
    {
        if (contactIds.Count == 0)
        {
            return [];
        }
        return await db.FunnelInvites
            .Where(f => f.EngagedAt == null && contactIds.Contains(f.ContactId))
            .ToListAsync(ct);
    }

    public Task UpdateAsync(FunnelInvite invite, CancellationToken ct)
    {
        if (db.Entry(invite).State == EntityState.Detached)
        {
            db.FunnelInvites.Update(invite);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<FunnelInvite>> ListRecentAsync(int limit, CancellationToken ct) =>
        await db.FunnelInvites
            .AsNoTracking()
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);
}
