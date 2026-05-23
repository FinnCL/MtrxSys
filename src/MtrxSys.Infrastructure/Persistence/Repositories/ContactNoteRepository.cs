using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ContactNoteRepository(MtrxDbContext db) : IContactNoteRepository
{
    public async Task<IReadOnlyList<ContactNote>> ListByContactAsync(Guid contactId, CancellationToken ct) =>
        await db.ContactNotes
            .Where(n => n.ContactId == contactId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(ContactNote note, CancellationToken ct) =>
        await db.ContactNotes.AddAsync(note, ct);

    public Task<ContactNote?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.ContactNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
}
