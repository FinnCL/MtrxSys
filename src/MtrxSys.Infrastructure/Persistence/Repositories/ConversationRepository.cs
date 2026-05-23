using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ConversationRepository(MtrxDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Conversation?> GetByWaChatIdAsync(string waChatId, CancellationToken ct) =>
        db.Conversations.FirstOrDefaultAsync(c => c.WaChatId == waChatId, ct);

    public async Task<IReadOnlyList<Conversation>> ListAsync(int limit, int offset, CancellationToken ct) =>
        await db.Conversations
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(Conversation conversation, CancellationToken ct) =>
        await db.Conversations.AddAsync(conversation, ct);

    public Task UpdateAsync(Conversation conversation, CancellationToken ct)
    {
        if (db.Entry(conversation).State == EntityState.Detached)
        {
            db.Conversations.Update(conversation);
        }
        return Task.CompletedTask;
    }
}
