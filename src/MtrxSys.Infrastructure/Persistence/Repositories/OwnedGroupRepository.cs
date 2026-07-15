using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class OwnedGroupRepository(MtrxDbContext db) : IOwnedGroupRepository
{
    public async Task<IReadOnlyList<OwnedGroup>> ListAsync(CancellationToken ct) =>
        await db.OwnedGroups.AsNoTracking().OrderByDescending(g => g.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlySet<string>> ListWaGroupIdsAsync(CancellationToken ct)
    {
        // Só a coluna do id: a listagem de grupos precisa apenas responder "é meu?" por linha.
        var ids = await db.OwnedGroups.AsNoTracking().Select(g => g.WaGroupId).ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    public Task<OwnedGroup?> GetByWaGroupIdAsync(string waGroupId, CancellationToken ct) =>
        db.OwnedGroups.AsNoTracking().FirstOrDefaultAsync(g => g.WaGroupId == waGroupId, ct);

    // RASTREADO de propósito (sem AsNoTracking) e com os membros: quem chama isto vai MUTAR o grupo.
    public Task<OwnedGroup?> GetForUpdateAsync(string waGroupId, CancellationToken ct) =>
        db.OwnedGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.WaGroupId == waGroupId, ct);

    public async Task<IReadOnlySet<string>> ListExemptWaGroupIdsAsync(CancellationToken ct)
    {
        var ids = await db.OwnedGroups.AsNoTracking()
            .Where(g => g.DispatchExemptionEnabled)
            .Select(g => g.WaGroupId)
            .ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> ListExemptPhonesAsync(CancellationToken ct)
    {
        // Só a coluna do telefone, e só dos grupos LIGADOS. Com a isenção desligada (o default), o
        // banco devolve zero linhas e o disparo segue exatamente como antes.
        var phones = await db.OwnedGroups.AsNoTracking()
            .Where(g => g.DispatchExemptionEnabled)
            .SelectMany(g => g.Members)
            .Select(m => m.PhoneE164)
            .ToListAsync(ct);
        return phones.ToHashSet(StringComparer.Ordinal);
    }

    public async Task AddAsync(OwnedGroup group, CancellationToken ct) =>
        await db.OwnedGroups.AddAsync(group, ct);

    public async Task<bool> RemoveAsync(string waGroupId, CancellationToken ct)
    {
        var existing = await db.OwnedGroups.FirstOrDefaultAsync(g => g.WaGroupId == waGroupId, ct);
        if (existing is null)
        {
            return false;
        }
        db.OwnedGroups.Remove(existing);
        return true;
    }
}
