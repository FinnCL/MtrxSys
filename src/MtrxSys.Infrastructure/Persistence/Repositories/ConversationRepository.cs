using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ConversationRepository(MtrxDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Conversation?> GetByWaChatIdAsync(string waChatId, CancellationToken ct) =>
        db.Conversations.FirstOrDefaultAsync(c => c.WaChatId == waChatId, ct);

    public Task<Conversation?> GetByContactIdAsync(Guid contactId, CancellationToken ct) =>
        db.Conversations
            .Where(c => c.ContactId == contactId && !c.IsGroup)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Conversation>> ListByStatusAsync(
        string? status, string? search, int limit, int offset, CancellationToken ct) =>
        await BuildQuery(status, search)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ThenByDescending(c => c.Id) // desempate estável: sem isso, itens com o mesmo horário podem pular/repetir entre páginas
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<ConversationStatusCounts> CountByStatusAsync(string? search, CancellationToken ct) =>
        new(
            AwaitingReply: await BuildQuery("awaitingReply", search).CountAsync(ct),
            Responded: await BuildQuery("responded", search).CountAsync(ct),
            OptedOut: await BuildQuery("optedOut", search).CountAsync(ct),
            All: await BuildQuery(null, search).CountAsync(ct));

    // Filtro base: LEFT JOIN conversa→contato, aplicando os mesmos predicados de status que o
    // Chat usava no cliente (agora no banco) + busca por nome/telefone/título via ILIKE (Postgres).
    private IQueryable<Conversation> BuildQuery(string? status, string? search)
    {
        var q = from c in db.Conversations
                join ct0 in db.Contacts on c.ContactId equals (Guid?)ct0.Id into gj
                from contact in gj.DefaultIfEmpty()
                select new { c, contact };

        q = status switch
        {
            "awaitingReply" => q.Where(x => !x.c.IsGroup && x.contact != null && x.contact.OptOutAt == null && x.contact.Stage == ContactStage.Lead),
            "responded" => q.Where(x => !x.c.IsGroup && x.contact != null && x.contact.OptOutAt == null && x.contact.Stage != ContactStage.Lead),
            "optedOut" => q.Where(x => x.contact != null && x.contact.OptOutAt != null),
            _ => q,
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Escapa curingas do LIKE (\ % _) pra busca literal; "\" é o escape char.
            var escaped = search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var term = $"%{escaped}%";
            q = q.Where(x =>
                (x.c.Title != null && EF.Functions.ILike(x.c.Title, term, "\\")) ||
                (x.contact != null && x.contact.Name != null && EF.Functions.ILike(x.contact.Name, term, "\\")) ||
                (x.contact != null && EF.Functions.ILike(x.contact.Phone.E164, term, "\\")));
        }

        return q.Select(x => x.c);
    }

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
