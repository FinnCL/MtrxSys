using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ContactStageChangeRepository(MtrxDbContext db) : IContactStageChangeRepository
{
    public async Task<IReadOnlyList<ContactStageChange>> ListByContactAsync(Guid contactId, CancellationToken ct) =>
        await db.ContactStageChanges
            .Where(c => c.ContactId == contactId)
            .OrderByDescending(c => c.ChangedAt)
            .ToListAsync(ct);

    public async Task AddAsync(ContactStageChange change, CancellationToken ct) =>
        await db.ContactStageChanges.AddAsync(change, ct);
}
