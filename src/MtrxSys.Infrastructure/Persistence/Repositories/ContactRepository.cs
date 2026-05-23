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

    public async Task<IReadOnlyList<Contact>> ListByFilterAsync(ContactFilter filter, CancellationToken ct)
    {
        var q = db.Contacts.AsQueryable();
        if (filter.Stage is { } stage)
        {
            q = q.Where(c => c.Stage == stage);
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
        return await q.OrderBy(c => c.Phone.E164).ToListAsync(ct);
    }
}
