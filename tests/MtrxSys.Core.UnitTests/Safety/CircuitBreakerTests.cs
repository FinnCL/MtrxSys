using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class CircuitBreakerTests
{
    private readonly ISystemStateRepository _state = Substitute.For<ISystemStateRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private CircuitBreaker Build(int threshold = 3, int openMinutes = 60)
    {
        _clock.UtcNow.Returns(_now);
        return new CircuitBreaker(
            _state,
            _clock,
            Options.Create(new CircuitBreakerOptions { FailureThreshold = threshold, OpenDurationMinutes = openMinutes }));
    }

    [Fact]
    public async Task IsOpen_false_when_circuit_closed()
    {
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(SystemStateAggregate.CreateInitial());
        var svc = Build();

        (await svc.IsOpenAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsOpen_true_when_OpenUntil_in_future()
    {
        var agg = SystemStateAggregate.CreateInitial();
        agg.UpdateCircuit(new CircuitBreakerState(3, _now.AddMinutes(10)));
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(agg);
        var svc = Build();

        (await svc.IsOpenAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsOpen_false_when_OpenUntil_already_past()
    {
        var agg = SystemStateAggregate.CreateInitial();
        agg.UpdateCircuit(new CircuitBreakerState(3, _now.AddMinutes(-1)));
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(agg);
        var svc = Build();

        (await svc.IsOpenAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordFailure_opens_circuit_at_threshold()
    {
        var agg = SystemStateAggregate.CreateInitial();
        agg.UpdateCircuit(new CircuitBreakerState(2, null));
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(agg);
        var svc = Build(threshold: 3, openMinutes: 60);

        await svc.RecordFailureAsync("boom", CancellationToken.None);

        agg.Circuit.ConsecutiveFailures.Should().Be(3);
        agg.Circuit.OpenUntil.Should().NotBeNull();
        agg.Circuit.OpenUntil!.Value.Should().BeCloseTo(_now.AddMinutes(60), TimeSpan.FromSeconds(1));
        agg.PausedReason.Should().Be("boom");
    }

    [Fact]
    public async Task RecordFailure_below_threshold_does_not_open()
    {
        var agg = SystemStateAggregate.CreateInitial();
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(agg);
        var svc = Build(threshold: 3);

        await svc.RecordFailureAsync("flaky", CancellationToken.None);

        agg.Circuit.ConsecutiveFailures.Should().Be(1);
        agg.Circuit.OpenUntil.Should().BeNull();
    }

    [Fact]
    public async Task RecordSuccess_resets_consecutive_failures()
    {
        var agg = SystemStateAggregate.CreateInitial();
        agg.UpdateCircuit(new CircuitBreakerState(2, null));
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(agg);
        var svc = Build();

        await svc.RecordSuccessAsync(CancellationToken.None);

        agg.Circuit.ConsecutiveFailures.Should().Be(0);
        agg.Circuit.OpenUntil.Should().BeNull();
    }
}
