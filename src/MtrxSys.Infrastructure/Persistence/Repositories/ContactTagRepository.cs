using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ContactTagRepository(MtrxDbContext db) : IContactTagRepository
{
    public async Task<IReadOnlyList<ContactTag>> ListAllAsync(CancellationToken ct) =>
        await db.ContactTags.OrderBy(t => t.Name).ToListAsync(ct);

    public Task<ContactTag?> GetByNameAsync(string name, CancellationToken ct)
    {
        var key = name.Trim().ToLowerInvariant();
        return db.ContactTags.FirstOrDefaultAsync(t => t.Name == key, ct);
    }

    public async Task AddAsync(ContactTag tag, CancellationToken ct) =>
        await db.ContactTags.AddAsync(tag, ct);

    public async Task<IReadOnlyList<string>> ListTagsForContactAsync(Guid contactId, CancellationToken ct) =>
        await db.ContactTagAssignments
            .Where(a => a.ContactId == contactId)
            .Select(a => a.TagName)
            .ToListAsync(ct);

    public async Task AssignAsync(ContactTagAssignment assignment, CancellationToken ct) =>
        await db.ContactTagAssignments.AddAsync(assignment, ct);

    public async Task UnassignAsync(Guid contactId, string tagName, CancellationToken ct)
    {
        var key = tagName.Trim().ToLowerInvariant();
        var existing = await db.ContactTagAssignments
            .FirstOrDefaultAsync(a => a.ContactId == contactId && a.TagName == key, ct);
        if (existing is not null)
        {
            db.ContactTagAssignments.Remove(existing);
        }
    }
}
