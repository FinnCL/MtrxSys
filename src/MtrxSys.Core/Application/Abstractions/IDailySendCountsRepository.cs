using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDailySendCountsRepository
{
    Task<DailySendCount?> GetAsync(DateOnly dateUtc, CancellationToken ct);
    Task<int> IncrementAsync(DateOnly dateUtc, int warmupDayIndex, CancellationToken ct);
    /// <summary>
    /// Quantos dias ANTERIORES a hoje tiveram pelo menos 1 envio. Base do calculo do
    /// DayIndex do aquecimento — assim a curva so avanca por DIA REALMENTE USADO, nao
    /// por dias do calendario sem uso. Chip novo que nao foi disparado fica no Dia 1.
    /// </summary>
    Task<int> CountActiveDaysBeforeAsync(DateOnly today, CancellationToken ct);
}
