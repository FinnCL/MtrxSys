using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

public interface IChatMessageRepository
{
    Task<ChatMessage?> GetByWaMessageIdAsync(string waMessageId, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> ListByConversationAsync(Guid conversationId, int limit, int offset, CancellationToken ct);
    Task AddAsync(ChatMessage message, CancellationToken ct);
}
