using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDispatchJobRepository
{
    Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct);
    Task AddAsync(DispatchJob job, CancellationToken ct);
    Task UpdateAsync(DispatchJob job, CancellationToken ct);
    Task<DispatchStats> GetStatsAsync(CancellationToken ct);
    Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<DispatchReportItem>> ListReportAsync(DispatchStatus? status, int limit, CancellationToken ct);
    /// <summary>Remove os jobs "Na fila" (Pending) — cancela o que foi preparado e ainda não saiu.</summary>
    Task<int> ClearPendingAsync(CancellationToken ct);
    /// <summary>
    /// Remove os jobs "Na fila" (Pending) que referenciam um template específico. Usado quando
    /// um template é deletado: jobs já enviados (Sent) ficam intactos pro histórico, mas envios
    /// que ainda não saíram precisam sumir junto — caso contrário o dispatcher continuaria
    /// mandando a mensagem "deletada" enquanto a fila esvaziasse.
    /// </summary>
    Task<int> ClearPendingByTemplateAsync(Guid templateId, CancellationToken ct);
    /// <summary>Remove TODOS os jobs (renova a lista/zera o histórico de envios).</summary>
    Task<int> ClearAllAsync(CancellationToken ct);
}

public sealed record DispatchStats(int Pending, int Sent, int Failed, int Skipped);

public sealed record DispatchReportItem(
    string? Phone,
    string? Name,
    string Status,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? SentAt,
    string? ErrorReason);
