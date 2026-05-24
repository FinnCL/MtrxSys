using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class SystemStateRepository(MtrxDbContext db) : ISystemStateRepository
{
    public async Task<SystemStateAggregate> GetAsync(CancellationToken ct)
    {
        // FindAsync checa primeiro as entidades já rastreadas no contexto e só consulta o
        // banco se não achar. Isso evita recriar/adicionar a mesma chave quando GetAsync é
        // chamado mais de uma vez no mesmo ciclo (ex.: IsOpenAsync + RecordSuccessAsync),
        // que antes estourava "another instance with the same key is already being tracked".
        var state = await db.SystemState.FindAsync([SystemStateAggregate.SingletonId], ct);
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
