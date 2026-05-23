using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface ISystemStateRepository
{
    Task<SystemStateAggregate> GetAsync(CancellationToken ct);
    Task UpdateAsync(SystemStateAggregate state, CancellationToken ct);
}
