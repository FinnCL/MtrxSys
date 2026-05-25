using FluentAssertions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.UnitTests.SystemState;

public sealed class WarmupReconcileTests
{
    private static readonly DateOnly Today = new(2026, 5, 25);

    [Fact]
    public void First_phone_is_recorded_without_restarting()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.RestartWarmup(new DateOnly(2026, 5, 20)); // já em andamento (dia X)

        var changed = s.ReconcileWarmupPhone("+5511999999999", Today);

        changed.Should().BeFalse();
        s.WarmupPhone.Should().Be("+5511999999999");
        s.WarmupStartedOn.Should().Be(new DateOnly(2026, 5, 20)); // preservado
    }

    [Fact]
    public void Same_phone_does_not_restart()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.RestartWarmup(new DateOnly(2026, 5, 20));            // base do aquecimento
        s.ReconcileWarmupPhone("+5511999999999", Today);      // 1ª vez: só registra o número

        var changed = s.ReconcileWarmupPhone("+5511999999999", Today);

        changed.Should().BeFalse();
        s.WarmupStartedOn.Should().Be(new DateOnly(2026, 5, 20));
    }

    [Fact]
    public void Different_phone_restarts_warmup_and_tracks_new_number()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.ReconcileWarmupPhone("+5511111111111", new DateOnly(2026, 5, 20));
        s.ReleaseWarmupBonus(new DateOnly(2026, 5, 20), 50); // bônus do chip antigo

        var changed = s.ReconcileWarmupPhone("+5522222222222", Today);

        changed.Should().BeTrue();
        s.WarmupPhone.Should().Be("+5522222222222");
        s.WarmupStartedOn.Should().Be(Today);        // curva volta ao dia 0
        s.BonusFor(Today).Should().Be(0);            // liberação do chip antigo limpa
    }

    [Fact]
    public void Empty_read_is_ignored()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.RestartWarmup(new DateOnly(2026, 5, 20));
        s.ReconcileWarmupPhone("+5511999999999", Today);

        var changed = s.ReconcileWarmupPhone(null, Today);

        changed.Should().BeFalse();
        s.WarmupPhone.Should().Be("+5511999999999"); // mantém o atual
        s.WarmupStartedOn.Should().Be(new DateOnly(2026, 5, 20));
    }
}
