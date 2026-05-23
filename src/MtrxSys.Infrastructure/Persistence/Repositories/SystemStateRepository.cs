using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class SystemStateRepository(MtrxDbContext db) : ISystemStateRepository
{
    public async Task<SystemStateAggregate> GetAsync(CancellationToken ct)
    {
        var state = await db.SystemState.FirstOrDefaultAsync(s => s.Id == SystemStateAggregate.SingletonId, ct);
        if (state is null)
        {
            state = SystemStateAggregate.CreateInitial();
            await db.SystemState.AddAsync(state, ct);
        }
        return state;
    }

    public Task UpdateAsync(SystemStateAggregate state, CancellationToken ct)
    {
        if (db.Entry(state).State == EntityState.Detached)
        {
            db.SystemState.Update(state);
        }
        return Task.CompletedTask;
    }
}
