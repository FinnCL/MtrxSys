using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Core.Application.Abstractions;

public interface IDispatchJobRepository
{
    Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct);
    /// <summary>Menor ScheduledAt entre os jobs ainda na fila (Pending/Retrying), ou null se a fila
    /// está vazia. Usado pra enfileirar novos importados NO TOPO (com ScheduledAt anterior a este).</summary>
    Task<DateTimeOffset?> GetEarliestPendingScheduledAtAsync(CancellationToken ct);
    Task AddAsync(DispatchJob job, CancellationToken ct);
    Task UpdateAsync(DispatchJob job, CancellationToken ct);
    Task<DispatchStats> GetStatsAsync(CancellationToken ct);
    Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<DispatchReportItem>> ListReportAsync(DispatchStatus? status, int limit, CancellationToken ct);
    /// <summary>Remove os jobs ainda na fila (Pending e Retrying) — cancela o que foi preparado
    /// (ou reenfileirado após falha) e ainda não saiu.</summary>
    Task<int> ClearPendingAsync(CancellationToken ct);
    /// <summary>
    /// Remove os jobs ainda na fila (Pending e Retrying) que referenciam um template específico.
    /// Usado quando um template é deletado: jobs já enviados (Sent) ficam intactos pro histórico,
    /// mas envios que ainda não saíram precisam sumir junto — caso contrário o dispatcher
    /// continuaria mandando a mensagem "deletada" enquanto a fila esvaziasse.
    /// </summary>
    Task<int> ClearPendingByTemplateAsync(Guid templateId, CancellationToken ct);
    /// <summary>Remove TODOS os jobs (renova a lista/zera o histórico de envios).</summary>
    Task<int> ClearAllAsync(CancellationToken ct);
}

public sealed record DispatchStats(int Pending, int Sent, int Failed, int Skipped, int Retrying);

public sealed record DispatchReportItem(
    string? Phone,
    string? Name,
    string Status,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? SentAt,
    string? ErrorReason,
    int AttemptCount,
    // Chip que importou o contato — pra o relatório marcar "outro chip" (não sai deste chip).
    string? ImportedByPhone = null,
    // Contato já ENGAJOU (respondeu/avançou — Stage != Novo/Descartado)? Pro relatório marcar
    // "Respondeu" na linha — ex.: na fase de aquecimento a fila é só de respondedores.
    bool Engaged = false);
