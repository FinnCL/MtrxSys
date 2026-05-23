using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.SystemState;

public sealed class SendAuditEntry : Entity<Guid>
{
    public Guid DispatchJobId { get; private set; }
    public string PhoneE164 { get; private set; } = string.Empty;
    public string RenderedText { get; private set; } = string.Empty;
    public int TypingMs { get; private set; }
    public int DelayMs { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    private SendAuditEntry() { }

    public static SendAuditEntry Create(Guid id, Guid dispatchJobId, string phoneE164,
        string renderedText, int typingMs, int delayMs, DateTimeOffset occurredAt)
    {
        return new SendAuditEntry
        {
            Id = id,
            DispatchJobId = dispatchJobId,
            PhoneE164 = phoneE164,
            RenderedText = renderedText,
            TypingMs = typingMs,
            DelayMs = delayMs,
            OccurredAt = occurredAt,
        };
    }
}
