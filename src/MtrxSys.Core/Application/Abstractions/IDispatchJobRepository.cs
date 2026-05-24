using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDispatchJobRepository
{
    Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct);
    Task AddAsync(DispatchJob job, CancellationToken ct);
    Task UpdateAsync(DispatchJob job, CancellationToken ct);
    Task<DispatchStats> GetStatsAsync(CancellationToken ct);
    Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<DispatchReportItem>> ListReportAsync(DispatchStatus? status, int limit, CancellationToken ct);
}

public sealed record DispatchStats(int Pending, int Sent, int Failed, int Skipped);

public sealed record DispatchReportItem(
    string? Phone,
    string? Name,
    string Status,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? SentAt,
    string? ErrorReason);
