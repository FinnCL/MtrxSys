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
    private readonly ISystemStateRepository _state = Substitute.For<ISystemStateRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public WarmupManagerTests()
    {
        // Por padrão, sem data de início no banco → cai no StartedOnUtc das options.
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(SystemStateAggregate.CreateInitial());
    }

    private WarmupManager Build(WarmupOptions opts)
    {
        return new WarmupManager(_counts, _state, _clock, Options.Create(opts));
    }

    private void SetToday(DateOnly today) =>
        _clock.UtcNow.Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    private async Task<int> TodayLimit(WarmupManager svc) =>
        (await svc.GetSnapshotAsync(CancellationToken.None)).TodayLimit;

    [Fact]
    public async Task TodayLimit_uses_curve_indexed_by_days_since_start()
    {
        var start = new DateOnly(2026, 1, 1);
        SetToday(new DateOnly(2026, 1, 3));
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80, 150, 250], StartedOnUtc = start });

        (await TodayLimit(svc)).Should().Be(80);
    }

    [Fact]
    public async Task TodayLimit_clamps_to_last_curve_value_after_curve_ends()
    {
        var start = new DateOnly(2026, 1, 1);
        SetToday(new DateOnly(2026, 1, 30));
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80, 150, 250], StartedOnUtc = start });

        (await TodayLimit(svc)).Should().Be(250);
    }

    [Fact]
    public async Task TodayLimit_uses_day_zero_when_StartedOnUtc_is_null()
    {
        SetToday(new DateOnly(2026, 1, 1));
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80], StartedOnUtc = null });

        (await TodayLimit(svc)).Should().Be(20);
    }

    [Fact]
    public async Task StartedOn_in_DB_overrides_options()
    {
        // Banco diz que o aquecimento reiniciou hoje → dia 0, mesmo com StartedOnUtc antigo.
        var today = new DateOnly(2026, 1, 10);
        SetToday(today);
        var state = SystemStateAggregate.CreateInitial();
        state.RestartWarmup(today);
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(state);
        var svc = Build(new WarmupOptions { Curve = [20, 40, 80], StartedOnUtc = new DateOnly(2026, 1, 1) });

        (await TodayLimit(svc)).Should().Be(20);
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
        _counts.GetAsync(today, Arg.Any<CancellationToken>())
            .Returns(BuildCountWith(today, 10));
        var svc = Build(new WarmupOptions { Curve = [10] });

        (await svc.CanSendAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task EffectiveLimit_includes_today_bonus()
    {
        var today = new DateOnly(2026, 1, 1);
        SetToday(today);
        var state = SystemStateAggregate.CreateInitial();
        state.ReleaseWarmupBonus(today, 20);
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(state);
        var svc = Build(new WarmupOptions { Curve = [10], StartedOnUtc = today });

        var snap = await svc.GetSnapshotAsync(CancellationToken.None);
        snap.TodayLimit.Should().Be(10);       // teto da curva inalterado
        snap.EffectiveLimit.Should().Be(30);   // 10 + 20 liberado
    }

    [Fact]
    public async Task CanSend_true_after_bonus_even_when_at_base_cap()
    {
        var today = new DateOnly(2026, 1, 1);
        SetToday(today);
        _counts.GetAsync(today, Arg.Any<CancellationToken>()).Returns(BuildCountWith(today, 10));
        var state = SystemStateAggregate.CreateInitial();
        state.ReleaseWarmupBonus(today, 5);
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(state);
        var svc = Build(new WarmupOptions { Curve = [10], StartedOnUtc = today });

        (await svc.CanSendAsync(CancellationToken.None)).Should().BeTrue(); // 10 < 15
    }

    [Fact]
    public async Task Bonus_from_a_previous_day_does_not_apply_today()
    {
        var today = new DateOnly(2026, 1, 2);
        SetToday(today);
        _counts.GetAsync(today, Arg.Any<CancellationToken>()).Returns(BuildCountWith(today, 10));
        var state = SystemStateAggregate.CreateInitial();
        state.ReleaseWarmupBonus(new DateOnly(2026, 1, 1), 50); // liberação de ontem
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(state);
        var svc = Build(new WarmupOptions { Curve = [10], StartedOnUtc = today });

        var snap = await svc.GetSnapshotAsync(CancellationToken.None);
        snap.EffectiveLimit.Should().Be(10); // bônus de ontem expirou
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
