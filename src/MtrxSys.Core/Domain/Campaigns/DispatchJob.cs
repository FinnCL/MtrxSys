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

    public void MarkSkipped(string reason)
    {
        Status = DispatchStatus.Skipped;
        ErrorReason = reason;
    }
}
