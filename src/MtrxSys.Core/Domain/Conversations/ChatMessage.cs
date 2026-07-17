using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Conversations;

public sealed class ChatMessage : Entity<Guid>
{
    public Guid ConversationId { get; private set; }
    public string WaMessageId { get; private set; } = null!;
    public MessageDirection Direction { get; private set; }
    public string? AuthorPhone { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset Timestamp { get; private set; }
    public string? MediaUrl { get; private set; }

    /// <summary>Status de entrega (do ack do WhatsApp). None em inbound e enquanto não chega ack.</summary>
    public MessageDeliveryStatus DeliveryStatus { get; private set; }

    private ChatMessage() { }

    public static ChatMessage Create(
        Guid id,
        Guid conversationId,
        string waMessageId,
        MessageDirection direction,
        string? authorPhone,
        string body,
        DateTimeOffset timestamp,
        string? mediaUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waMessageId);
        return new ChatMessage
        {
            Id = id,
            ConversationId = conversationId,
            WaMessageId = waMessageId,
            Direction = direction,
            AuthorPhone = authorPhone,
            Body = body ?? string.Empty,
            Timestamp = timestamp,
            MediaUrl = mediaUrl,
        };
    }

    /// <summary>Atualiza o status de entrega a partir do ack cru do WhatsApp (message.ack): ack&lt;0 é
    /// FALHA (rejeitado), 0/1/2/3+ avançam por Pending/Sent/Delivered/Read. Monotônico — só avança no
    /// caminho de entrega; uma mensagem já entregue não regride por um ack espúrio (ver
    /// MessageDeliveryStatus). Idempotente.</summary>
    public void MarkAck(int wahaAck)
    {
        var next = wahaAck switch
        {
            < 0 => MessageDeliveryStatus.Failed,
            0 => MessageDeliveryStatus.Pending,
            1 => MessageDeliveryStatus.Sent,
            2 => MessageDeliveryStatus.Delivered,
            _ => MessageDeliveryStatus.Read,
        };
        if ((int)next > (int)DeliveryStatus)
        {
            DeliveryStatus = next;
        }
    }
}
