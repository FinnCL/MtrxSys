using FluentAssertions;
using MtrxSys.Core.Safety;
using Xunit;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class WarmingPhaseTests
{
    private static readonly DateOnly Marco = new(2026, 7, 18);

    [Theory]
    [InlineData(0, 3, true)]   // dia 1 (0 dias ativos antes de hoje) → ainda aquecendo
    [InlineData(1, 3, true)]   // dia 2
    [InlineData(2, 3, true)]   // dia 3
    [InlineData(3, 3, false)]  // 3 dias ativos cumpridos → fora da fase
    [InlineData(10, 3, false)] // chip veterano → fora
    public void Aquece_ate_cumprir_os_dias_ativos(int activeDays, int warmingDays, bool expected)
    {
        WarmingPhase.IsActive(Marco, activeDays, warmingDays).Should().Be(expected);
    }

    [Fact]
    public void Sem_marco_do_chip_nunca_aquece()
    {
        // WarmupStartedOn null = não sabemos que é chip novo → fase não se aplica (fail-open).
        WarmingPhase.IsActive(null, 0, 3).Should().BeFalse();
    }

    [Fact]
    public void WarmingDays_zero_ou_negativo_desliga_a_fase()
    {
        WarmingPhase.IsActive(Marco, 0, 0).Should().BeFalse();
        WarmingPhase.IsActive(Marco, 0, -1).Should().BeFalse();
    }

    [Theory]
    [InlineData(2, WarmingStage.ResponderOnly)]  // dia 3 → só respondeu
    [InlineData(3, WarmingStage.Hybrid)]          // dia 4 → híbrido começa
    [InlineData(27, WarmingStage.Hybrid)]         // penúltimo degrau (180/dia) → ainda híbrido
    [InlineData(28, WarmingStage.Mature)]         // platô (200/dia) → maduro
    [InlineData(40, WarmingStage.Mature)]         // veterano
    public void Classify_segue_os_estagios_por_dias_ativos(int activeDays, WarmingStage expected)
    {
        WarmingPhase.Classify(Marco, activeDays, responderDays: 3, plateauDayIndex: 28, hybridEnabled: true)
            .Should().Be(expected);
    }

    [Fact]
    public void Classify_com_hibrido_desligado_pula_direto_pra_maduro()
    {
        WarmingPhase.Classify(Marco, 3, 3, 28, hybridEnabled: false).Should().Be(WarmingStage.Mature);
        // Antes dos 3 dias ainda é só-respondeu, independente do híbrido.
        WarmingPhase.Classify(Marco, 1, 3, 28, hybridEnabled: false).Should().Be(WarmingStage.ResponderOnly);
    }

    [Fact]
    public void Classify_sem_marco_ou_trava_off_e_maduro()
    {
        WarmingPhase.Classify(null, 1, 3, 28, true).Should().Be(WarmingStage.Mature);
        WarmingPhase.Classify(Marco, 1, 0, 28, true).Should().Be(WarmingStage.Mature);
    }
}
