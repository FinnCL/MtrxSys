using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

public sealed class WarmupManager(IDailySendCountsRepository counts, IClock clock, IOptions<WarmupOptions> opts)
{
    public async Task<bool> CanSendAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var existing = await counts.GetAsync(today, ct);
        var sent = existing?.SentCount ?? 0;
        return sent < TodayLimit();
    }

    public async Task IncrementAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        await counts.IncrementAsync(today, WarmupDayIndex(today), ct);
    }

    public int TodayLimit()
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var index = WarmupDayIndex(today);
        var curve = opts.Value.Curve;
        if (curve.Length == 0) return int.MaxValue;
        return index >= curve.Length ? curve[^1] : curve[index];
    }

    private int WarmupDayIndex(DateOnly today)
    {
        var startedOn = opts.Value.StartedOnUtc ?? today;
        var days = today.DayNumber - startedOn.DayNumber;
        return Math.Max(0, days);
    }
}
