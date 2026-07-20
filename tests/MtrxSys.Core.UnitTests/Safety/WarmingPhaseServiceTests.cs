using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;
using NSubstitute;
using Xunit;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>Trava a fonte única da fase "só respondeu" (WarmingPhaseService) — usada pelo disparo,
/// pelo relatório e pelo reset diário. O primitivo IsActive tem teste próprio; aqui garantimos a
/// orquestração (options + marco + contagem de dias ativos) e os campos humanos do status.</summary>
public sealed class WarmingPhaseServiceTests
{
    private readonly IDailySendCountsRepository _counts = Substitute.For<IDailySendCountsRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private static readonly DateOnly Marco = new(2026, 7, 18);

    public WarmingPhaseServiceTests() =>
        // Meio-dia UTC: data de Brasília coincide com a UTC, sem ambiguidade de fronteira.
        _clock.UtcNow.Returns(new DateTimeOffset(new DateOnly(2026, 7, 20).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));

    private WarmingPhaseService Build(int warmingDays = 3) =>
        new(_counts,
            new WarmupManager(_counts, Substitute.For<ISystemStateRepository>(), _clock, Options.Create(new WarmupOptions())),
            _clock,
            Options.Create(new DispatchOptions { WarmingResponderOnlyDays = warmingDays }));

    private static SystemStateAggregate StateWithMarco()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.RestartWarmup(Marco);
        return s;
    }

    private void ActiveDays(int n) => _counts
        .CountActiveDaysBeforeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
        .Returns(n);

    [Fact]
    public async Task Trava_desligada_nunca_esta_em_fase_e_nem_consulta_o_banco()
    {
        var status = await Build(warmingDays: 0).EvaluateAsync(StateWithMarco(), CancellationToken.None);

        status.Active.Should().BeFalse();
        await _counts.DidNotReceiveWithAnyArgs().CountActiveDaysBeforeAsync(default, default, default);
    }

    [Fact]
    public async Task Chip_sem_marco_nao_esta_em_fase_e_nem_consulta_o_banco()
    {
        var status = await Build().EvaluateAsync(SystemStateAggregate.CreateInitial(), CancellationToken.None);

        status.Active.Should().BeFalse();
        await _counts.DidNotReceiveWithAnyArgs().CountActiveDaysBeforeAsync(default, default, default);
    }

    [Theory]
    [InlineData(0, true, 1)]   // 1º dia de disparo
    [InlineData(2, true, 3)]   // 3º dia
    [InlineData(3, false, 4)]  // cumpriu os 3 dias ativos → abre
    public async Task Segue_os_dias_ATIVOS_e_expoe_o_dia_humano(int activeDays, bool active, int currentDay)
    {
        ActiveDays(activeDays);

        var status = await Build().EvaluateAsync(StateWithMarco(), CancellationToken.None);

        status.Active.Should().Be(active);
        status.ActiveDays.Should().Be(activeDays);
        status.CurrentDay.Should().Be(currentDay);
    }
}
