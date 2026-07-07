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

    // Sensor de ENTREGA (anti-shadow-restriction). WaMessageId = id "core" da mensagem enviada, pra
    // casar com o evento message.ack. Ack = maior estado visto (1 SERVER / 2 DEVICE=entregue / 3 READ).
    // DeliveredAt = quando chegou em ack >= 2 (entregue no aparelho do destinatário). Null = não entregou.
    public string WaMessageId { get; private set; } = string.Empty;
    public int Ack { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }

    private SendAuditEntry() { }

    public static SendAuditEntry Create(Guid id, Guid dispatchJobId, string phoneE164,
        string renderedText, int typingMs, int delayMs, DateTimeOffset occurredAt, string waMessageId)
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
            WaMessageId = waMessageId,
        };
    }

    // Atualiza o estado de entrega a partir de um message.ack. Monotônico (nunca regride) e idempotente:
    // marca DeliveredAt na PRIMEIRA vez que atinge "entregue" (ack >= 2). Read/played (3/4) mantêm entregue.
    public void MarkAck(int ack, DateTimeOffset when)
    {
        if (ack > Ack)
        {
            Ack = ack;
        }
        if (ack >= 2 && DeliveredAt is null)
        {
            DeliveredAt = when;
        }
    }
}
