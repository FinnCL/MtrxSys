using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Persistence;

internal sealed class UnitOfWork(MtrxDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public void DiscardChanges() => db.ChangeTracker.Clear();
}
