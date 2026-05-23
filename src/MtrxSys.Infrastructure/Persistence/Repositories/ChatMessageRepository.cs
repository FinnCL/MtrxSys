using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ChatMessageRepository(MtrxDbContext db) : IChatMessageRepository
{
    public Task<ChatMessage?> GetByWaMessageIdAsync(string waMessageId, CancellationToken ct) =>
        db.ChatMessages.FirstOrDefaultAsync(m => m.WaMessageId == waMessageId, ct);

    public async Task<IReadOnlyList<ChatMessage>> ListByConversationAsync(Guid conversationId, int limit, int offset, CancellationToken ct) =>
        await db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(ChatMessage message, CancellationToken ct) =>
        await db.ChatMessages.AddAsync(message, ct);
}
