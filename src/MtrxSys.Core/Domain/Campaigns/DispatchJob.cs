using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Campaigns;

public sealed class DispatchJob : Entity<Guid>
{
    public Guid ContactId { get; private set; }
    public Guid TemplateId { get; private set; }
    public DispatchStatus Status { get; private set; }
    public DateTimeOffset ScheduledAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public string? WahaMessageId { get; private set; }
    public string? ErrorReason { get; private set; }

    // Quantas vezes este disparo já foi efetivamente tentado (incrementa a cada reenvio agendado).
    public int AttemptCount { get; private set; }

    private DispatchJob() { }

    public static DispatchJob Schedule(Guid id, Guid contactId, Guid templateId, DateTimeOffset scheduledAt)
    {
        return new DispatchJob
        {
            Id = id,
            ContactId = contactId,
            TemplateId = templateId,
            Status = DispatchStatus.Pending,
            ScheduledAt = scheduledAt,
        };
    }

    public void MarkSent(string wahaMessageId, DateTimeOffset at)
    {
        Status = DispatchStatus.Sent;
        WahaMessageId = wahaMessageId;
        SentAt = at;
    }

    public void MarkFailed(string reason, DateTimeOffset at)
    {
        Status = DispatchStatus.Failed;
        ErrorReason = reason;
        SentAt = at;
    }

    // Decide, no momento de uma falha, se ainda vale reenviar. AttemptCount é o nº de reenvios
    // já agendados (0 na 1ª falha). Contando a tentativa que acabou de falhar (AttemptCount+1),
    // ainda fica abaixo do teto total? Ex.: maxAttempts=2 → reenvia só quando AttemptCount==0.
    public bool CanRetry(int maxAttempts) => AttemptCount + 1 < maxAttempts;

    // Falha transitória: registra o motivo e reagenda pro fim da fila (nextAt = agora, mais novo
    // que os Pending antigos). Não mexe em SentAt — o disparo ainda não saiu de fato.
    public void ScheduleRetry(DateTimeOffset nextAt, string reason)
    {
        Status = DispatchStatus.Retrying;
        AttemptCount++;
        ScheduledAt = nextAt;
        ErrorReason = reason;
    }

    public void MarkSkipped(string reason)
    {
        Status = DispatchStatus.Skipped;
        ErrorReason = reason;
    }
}
