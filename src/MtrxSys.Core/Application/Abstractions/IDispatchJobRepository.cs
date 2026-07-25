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
    /// <summary>Contadores da fila/histórico. <paramref name="currentChip"/> (WarmupPhone) permite contar
    /// quantos da FILA são do chip conectado agora (só esses saem — gate anti-463); null = chip desconhecido
    /// → conta todos como "do chip" (gate da UI fica OFF, igual ao motor, que só aplica o gate com chip conhecido).</summary>
    Task<DispatchStats> GetStatsAsync(string? currentChip, CancellationToken ct);
    Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct);
    /// <summary>Relatório de envios (histórico + fila). <paramref name="engagedOnly"/> true (fase de
    /// aquecimento) restringe a SÓ respondedores — o mesmo público que a fila aceita nesses dias, pra a
    /// tabela não exibir não-respondedores (pulados/legado). Fora da fase mostra todos, como antes.</summary>
    Task<IReadOnlyList<DispatchReportItem>> ListReportAsync(DispatchStatus? status, int limit, bool engagedOnly, CancellationToken ct);
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
    /// <summary>Remove os jobs de HISTÓRICO (Enviada/Falhou/Pulada) de UM contato — o que "sujava" o
    /// relatório com o status velho ("Enviada") depois de liberar o contato pra novo disparo. Mantém
    /// Pending/Retrying (fila ativa). É o análogo per-contato do ClearAllAsync ("Renovar lista").</summary>
    Task<int> DeleteHistoryByContactAsync(Guid contactId, CancellationToken ct);
    /// <summary>Remove da FILA (Pending/Retrying) os jobs de quem NÃO engajou (Stage Novo/Descartado).
    /// Usado na fase de aquecimento pra a fila ser 100% de respondedores — limpa jobs legados de um
    /// disparo "Todos" anterior à trava. Não toca em histórico (Enviada/Falhou/Pulada). Retorna quantos.</summary>
    Task<int> DeleteNonEngagedPendingAsync(CancellationToken ct);
    /// <summary>Remove o HISTÓRICO (Enviada/Falhou/Pulada) dos ENGAJADOS. Usado no reset diário do
    /// aquecimento pra o ciclo do dia começar limpo — sem o "Enviada" de ontem duplicando com o novo
    /// "Na fila" de hoje. Não toca na fila (Pending/Retrying). Retorna quantos.</summary>
    Task<int> DeleteEngagedHistoryAsync(CancellationToken ct);
}

// PendingFromCurrentChip: quantos da fila (Pending+Retrying) são do chip conectado agora — só esses saem
// (o gate anti-463 pula "outro chip"). A UI desabilita "Iniciar envios" quando Pending>0 mas este é 0
// (fila 100% de outro chip → disparo sairia 0). Chip desconhecido → = Pending+Retrying (gate OFF).
public sealed record DispatchStats(int Pending, int Sent, int Failed, int Skipped, int Retrying, int PendingFromCurrentChip);

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
