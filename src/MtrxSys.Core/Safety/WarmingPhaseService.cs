using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Safety;

/// <summary>Foto da fase "só quem respondeu" AGORA. <see cref="Active"/> = ainda aquecendo;
/// <see cref="ActiveDays"/> = dias COM ENVIO antes de hoje (base-0); <see cref="CurrentDay"/> é a
/// versão humana (base-1) pra mensagem/UI.</summary>
public readonly record struct WarmingPhaseStatus(bool Active, int ActiveDays, int WarmingDays)
{
    // "dia N de M" pra humano (base-1). Fora da fase não tem significado — use só quando Active.
    public int CurrentDay => ActiveDays + 1;
}

/// <summary>FONTE ÚNICA da pergunta "o chip está na fase de aquecimento por respondedores AGORA?".
/// Antes a mesma conta (CountActiveDaysBeforeAsync + <see cref="WarmingPhase.IsActive"/>) estava
/// repetida no disparo, no relatório e no reset diário — três cópias que podiam derivar. Todas passam
/// a chamar aqui. O motor (Dispatcher) segue pelo snapshot do WarmupManager (DayIndex ≡ ActiveDays,
/// equivalência comprovada), pois já o carrega por outros motivos.</summary>
public sealed class WarmingPhaseService(
    IDailySendCountsRepository counts,
    IClock clock,
    IOptions<DispatchOptions> options)
{
    public async Task<WarmingPhaseStatus> EvaluateAsync(SystemStateAggregate state, CancellationToken ct)
    {
        var warmingDays = options.Value.WarmingResponderOnlyDays;
        // Trava desligada (0) ou chip sem marco → nunca em fase (disparo normal).
        if (warmingDays <= 0 || state.WarmupStartedOn is not { } since)
        {
            return new WarmingPhaseStatus(false, 0, warmingDays);
        }
        var activeDays = await counts.CountActiveDaysBeforeAsync(
            since, IClock.ToBrasiliaDate(clock.UtcNow), ct);
        return new WarmingPhaseStatus(
            WarmingPhase.IsActive(since, activeDays, warmingDays), activeDays, warmingDays);
    }
}
