using FluentAssertions;
using MtrxSys.Core.Safety;
using Xunit;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class SessionReadinessTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sem_working_observado_e_null()
    {
        new SessionReadinessTracker().WorkingFor(T0).Should().BeNull();
    }

    [Fact]
    public void MarkWorking_preserva_o_primeiro_since()
    {
        var t = new SessionReadinessTracker();
        t.MarkWorking(T0);
        t.MarkWorking(T0.AddMinutes(5)); // NÃO sobrescreve
        t.WorkingFor(T0.AddMinutes(5)).Should().Be(TimeSpan.FromMinutes(5), "conta desde o 1º WORKING");
    }

    [Fact]
    public void MarkNotWorking_zera_e_o_proximo_working_recomeca()
    {
        var t = new SessionReadinessTracker();
        t.MarkWorking(T0);
        t.MarkNotWorking();
        t.WorkingFor(T0.AddMinutes(1)).Should().BeNull();
        t.MarkWorking(T0.AddMinutes(2));
        t.WorkingFor(T0.AddMinutes(3)).Should().Be(TimeSpan.FromMinutes(1), "recomeça do 2º WORKING (reconexão)");
    }

    [Fact]
    public void Baseline_pos_restart_com_since_no_passado_ja_conta_como_assentado()
    {
        // Simula o baseline do SessionHealthWatch pós-restart da api: sessão já WORKING → MarkWorking
        // com 'since' no passado (backdate) → já assentado, NÃO re-arma o settle a cada deploy.
        var t = new SessionReadinessTracker();
        t.MarkWorking(T0 - TimeSpan.FromDays(1));
        t.WorkingFor(T0).Should().BeGreaterThan(TimeSpan.FromHours(1),
            "restart da api com a sessão já viva não deve impor o settle");
    }
}
