using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class WarmupManagerTests
{
    private readonly IDailySendCountsRepository _counts = Substitute.For<IDailySendCountsRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private WarmupManager Build(WarmupOptions opts)
    {
        return new WarmupManager(_counts, _clock, Options.Create(opts));
    }

    private void SetToday(DateOnly today) =>
        _clock.UtcNow.Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    [Fact]
    public void TodayLimit_uses_curve_indexed_by_days_since_start()
    {
        var start = new DateOnly(2026, 1, 1);
        SetToday(new DateOnly(2026, 1, 3));
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80, 150, 250], StartedOnUtc = start });

        svc.TodayLimit().Should().Be(80);
    }

    [Fact]
    public void TodayLimit_clamps_to_last_curve_value_after_curve_ends()
    {
        var start = new DateOnly(2026, 1, 1);
        SetToday(new DateOnly(2026, 1, 30));
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80, 150, 250], StartedOnUtc = start });

        svc.TodayLimit().Should().Be(250);
    }

    [Fact]
    public void TodayLimit_uses_day_zero_when_StartedOnUtc_is_null()
    {
        SetToday(new DateOnly(2026, 1, 1));
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80], StartedOnUtc = null });

        svc.TodayLimit().Should().Be(20);
    }

    [Fact]
    public async Task CanSend_returns_true_when_under_limit()
    {
        var today = new DateOnly(2026, 1, 1);
        SetToday(today);
        _counts.GetAsync(today, Arg.Any<CancellationToken>())
            .Returns(DailySendCount.Create(today, 0));
        var svc = Build(new WarmupOptions { Curve = [10] });

        (await svc.CanSendAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task CanSend_returns_false_when_at_limit()
    {
        var today = new DateOnly(2026, 1, 1);
        SetToday(today);
        var count = DailySendCount.Create(today, 0);
        for (var i = 0; i < 10; i++)
        {
            // Use reflection-free way: increment via internal state through helper
            // Since SentCount has private setter, we simulate via repository returning an entity with that count
        }
        _counts.GetAsync(today, Arg.Any<CancellationToken>())
            .Returns(BuildCountWith(today, 10));
        var svc = Build(new WarmupOptions { Curve = [10] });

        (await svc.CanSendAsync(CancellationToken.None)).Should().BeFalse();
    }

    private static DailySendCount BuildCountWith(DateOnly date, int sent)
    {
        var entity = DailySendCount.Create(date, 0);
        // SentCount is private setter; use reflection for test only
        var prop = typeof(DailySendCount).GetProperty(nameof(DailySendCount.SentCount))!;
        prop.SetValue(entity, sent);
        return entity;
    }
}
