using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ContactRepository(MtrxDbContext db) : IContactRepository
{
    public Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Contacts.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Contact?> GetByPhoneAsync(string e164, CancellationToken ct) =>
        db.Contacts.FirstOrDefaultAsync(c => c.Phone.E164 == e164, ct);

    public async Task AddAsync(Contact contact, CancellationToken ct) =>
        await db.Contacts.AddAsync(contact, ct);

    public Task UpdateAsync(Contact contact, CancellationToken ct)
    {
        if (db.Entry(contact).State == EntityState.Detached)
        {
            db.Contacts.Update(contact);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Contact>> ListByFilterAsync(ContactFilter filter, CancellationToken ct) =>
        await ApplyFilter(db.Contacts.AsQueryable(), filter).OrderBy(c => c.Phone.E164).ToListAsync(ct);

    // Exclui os contatos de um grupo SEM deixar órfãos: primeiro apaga disparos e conversas
    // (cascateiam as mensagens) ligados a esses contatos, depois os contatos (que cascateiam
    // notas, tags e histórico). Toca outras tabelas de propósito — é uma operação de manutenção.
    // Tudo numa transação: cada ExecuteDelete commitaria sozinho, então sem isso uma falha no
    // meio deixaria estado parcial (ex.: disparos apagados mas contatos não).
    public async Task<int> DeleteByGroupTagAsync(string groupTag, CancellationToken ct)
    {
        var ids = await db.Contacts
            .Where(c => c.GroupTag == groupTag)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (ids.Count == 0)
        {
            return 0;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.DispatchJobs.Where(j => ids.Contains(j.ContactId)).ExecuteDeleteAsync(ct);
        await db.Conversations
            .Where(c => c.ContactId.HasValue && ids.Contains(c.ContactId.Value))
            .ExecuteDeleteAsync(ct);
        await db.Contacts.Where(c => c.GroupTag == groupTag).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return ids.Count;
    }

    public Task<int> CountByFilterAsync(ContactFilter filter, CancellationToken ct) =>
        ApplyFilter(db.Contacts.AsQueryable(), filter).CountAsync(ct);

    private IQueryable<Contact> ApplyFilter(IQueryable<Contact> q, ContactFilter filter)
    {
        if (filter.Stage is { } stage)
        {
            q = q.Where(c => c.Stage == stage);
        }
        // "Engajados" = qualquer um que respondeu/avançou: tudo menos "Novo" (Lead) e "Descartado" (Lost).
        if (filter.EngagedOnly)
        {
            q = q.Where(c => c.Stage != ContactStage.Lead && c.Stage != ContactStage.Lost);
        }
        if (filter.ExcludeOptedOut)
        {
            q = q.Where(c => c.OptOutAt == null);
        }
        if (!string.IsNullOrWhiteSpace(filter.GroupTag))
        {
            q = q.Where(c => c.GroupTag == filter.GroupTag);
        }
        if (!string.IsNullOrWhiteSpace(filter.TagName))
        {
            var key = filter.TagName.Trim().ToLowerInvariant();
            var contactIds = db.ContactTagAssignments
                .Where(a => a.TagName == key)
                .Select(a => a.ContactId);
            q = q.Where(c => contactIds.Contains(c.Id));
        }
        return q;
    }

    public async Task<IReadOnlyList<ContactGroupTag>> ListGroupTagsAsync(CancellationToken ct)
    {
        // EF traduz o GroupBy numa projeção anônima; o mapeamento pro record e a
        // ordenação são feitos em memória (EF não traduz projeção via construtor + OrderBy).
        var raw = await db.Contacts
            .Where(c => c.GroupTag != null)
            .GroupBy(c => c.GroupTag!)
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return raw
            .OrderBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ContactGroupTag(x.Tag, x.Count))
            .ToList();
    }
}
