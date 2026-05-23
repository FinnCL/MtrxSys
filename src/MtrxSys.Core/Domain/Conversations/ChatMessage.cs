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
}
