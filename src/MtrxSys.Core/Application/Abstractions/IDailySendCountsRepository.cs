using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDailySendCountsRepository
{
    Task<DailySendCount?> GetAsync(DateOnly dateUtc, CancellationToken ct);
    Task<int> IncrementAsync(DateOnly dateUtc, int warmupDayIndex, CancellationToken ct);
}
