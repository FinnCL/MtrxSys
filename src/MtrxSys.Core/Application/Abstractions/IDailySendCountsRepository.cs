using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDailySendCountsRepository
{
    Task<DailySendCount?> GetAsync(DateOnly dateUtc, CancellationToken ct);
    Task<int> IncrementAsync(DateOnly dateUtc, int warmupDayIndex, CancellationToken ct);
    /// <summary>
    /// Quantos dias no intervalo [since, today) tiveram pelo menos 1 envio. Base do DayIndex
    /// do aquecimento — a curva só avança por DIA REALMENTE USADO (chip parado fica no nível),
    /// e o `since` (marco de RestartWarmup) garante que um chip novo re-pareado volte ao Dia 0
    /// em vez de herdar o histórico do ambiente. `since = MinValue` conta todo o histórico.
    /// </summary>
    Task<int> CountActiveDaysBeforeAsync(DateOnly since, DateOnly today, CancellationToken ct);
}
