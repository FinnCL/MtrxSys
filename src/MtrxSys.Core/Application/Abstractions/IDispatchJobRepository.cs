using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDispatchJobRepository
{
    Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct);
    Task AddAsync(DispatchJob job, CancellationToken ct);
    Task UpdateAsync(DispatchJob job, CancellationToken ct);
    Task<DispatchStats> GetStatsAsync(CancellationToken ct);
    Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct);
}

public sealed record DispatchStats(int Pending, int Sent, int Failed, int Skipped);
