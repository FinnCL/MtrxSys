using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ChatMessageRepository(MtrxDbContext db) : IChatMessageRepository
{
    public Task<ChatMessage?> GetByWaMessageIdAsync(string waMessageId, CancellationToken ct) =>
        db.ChatMessages.FirstOrDefaultAsync(m => m.WaMessageId == waMessageId, ct);

    // AsNoTracking: os dois chamadores (tela do Chat e reconciliador) só LEEM. Sem isto, listar uma
    // conversa jogava até `limit` mensagens no change tracker — custo de memória e, pior, o
    // SaveChanges seguinte passava a varrer todas elas atrás de alterações que nunca existem.
    public async Task<IReadOnlyList<ChatMessage>> ListByConversationAsync(Guid conversationId, int limit, int offset, CancellationToken ct) =>
        await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<InboundMessageRef>> ListInboundByConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
        {
            return [];
        }
        var ids = conversationIds as IList<Guid> ?? [.. conversationIds];
        return await db.ChatMessages
            .AsNoTracking()
            .Where(m => ids.Contains(m.ConversationId) && m.Direction == MessageDirection.Inbound)
            .Select(m => new InboundMessageRef(m.ConversationId, m.Body, m.Timestamp))
            .ToListAsync(ct);
    }

    public async Task AddAsync(ChatMessage message, CancellationToken ct) =>
        await db.ChatMessages.AddAsync(message, ct);
}
