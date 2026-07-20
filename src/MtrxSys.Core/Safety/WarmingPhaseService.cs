using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Safety;

/// <summary>Foto da fase "só quem respondeu" AGORA. <see cref="Active"/> = ainda aquecendo;
/// <see cref="ActiveDays"/> = dias COM ENVIO antes de hoje (base-0); <see cref="CurrentDay"/> é a
/// versão humana (base-1) pra mensagem/UI.</summary>
public readonly record struct WarmingPhaseStatus(
    bool Active, int ActiveDays, int WarmingDays, WarmingStage Stage, int PlateauDay)
{
    // "dia N de M" pra humano (base-1). Fora da fase não tem significado — use só quando Active/IsHybrid.
    public int CurrentDay => ActiveDays + 1;
    // Fase híbrida (dia 4 → platô): mistura Círculo re-enviável + frios novos, intercalado.
    public bool IsHybrid => Stage == WarmingStage.Hybrid;
}

/// <summary>FONTE ÚNICA da pergunta "o chip está na fase de aquecimento por respondedores AGORA?".
/// Antes a mesma conta (CountActiveDaysBeforeAsync + <see cref="WarmingPhase.IsActive"/>) estava
/// repetida no disparo, no relatório e no reset diário — três cópias que podiam derivar. Todas passam
/// a chamar aqui. O motor (Dispatcher) segue pelo snapshot do WarmupManager (DayIndex ≡ ActiveDays,
/// equivalência comprovada), pois já o carrega por outros motivos.</summary>
public sealed class WarmingPhaseService(
    IDailySendCountsRepository counts,
    WarmupManager warmup,
    IClock clock,
    IOptions<DispatchOptions> options)
{
    public async Task<WarmingPhaseStatus> EvaluateAsync(SystemStateAggregate state, CancellationToken ct)
    {
        var warmingDays = options.Value.WarmingResponderOnlyDays;
        var plateau = warmup.PlateauDayIndex; // último degrau da curva = fim do aquecimento
        // Trava desligada (0) ou chip sem marco → fora do aquecimento (disparo normal = Mature).
        if (warmingDays <= 0 || state.WarmupStartedOn is not { } since)
        {
            return new WarmingPhaseStatus(false, 0, warmingDays, WarmingStage.Mature, plateau + 1);
        }
        var activeDays = await counts.CountActiveDaysBeforeAsync(
            since, IClock.ToBrasiliaDate(clock.UtcNow), ct);
        var stage = WarmingPhase.Classify(
            since, activeDays, warmingDays, plateau, options.Value.HybridWarmingEnabled);
        // Active == ResponderOnly (fonte única do gate 409 / filtro do relatório — inalterado).
        return new WarmingPhaseStatus(
            WarmingPhase.IsActive(since, activeDays, warmingDays), activeDays, warmingDays, stage, plateau + 1);
    }
}
