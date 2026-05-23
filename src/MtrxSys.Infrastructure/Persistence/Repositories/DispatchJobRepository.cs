using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class DispatchJobRepository(MtrxDbContext db) : IDispatchJobRepository
{
    public Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct) =>
        db.DispatchJobs
            .Where(j => j.Status == DispatchStatus.Pending && j.ScheduledAt <= until)
            .OrderBy(j => j.ScheduledAt)
            .FirstOrDefaultAsync(ct);

    public async Task AddAsync(DispatchJob job, CancellationToken ct) =>
        await db.DispatchJobs.AddAsync(job, ct);

    public Task UpdateAsync(DispatchJob job, CancellationToken ct)
    {
        if (db.Entry(job).State == EntityState.Detached)
        {
            db.DispatchJobs.Update(job);
        }
        return Task.CompletedTask;
    }

    public async Task<DispatchStats> GetStatsAsync(CancellationToken ct)
    {
        var grouped = await db.DispatchJobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var pending = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Pending)?.Count ?? 0;
        var sent = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Sent)?.Count ?? 0;
        var failed = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Failed)?.Count ?? 0;
        var skipped = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Skipped)?.Count ?? 0;
        return new DispatchStats(pending, sent, failed, skipped);
    }

    public async Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct) =>
        await db.DispatchJobs
            .OrderByDescending(j => j.SentAt ?? j.ScheduledAt)
            .Take(limit)
            .ToListAsync(ct);
}
