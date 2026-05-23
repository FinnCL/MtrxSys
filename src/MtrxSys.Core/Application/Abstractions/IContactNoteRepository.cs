using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public interface IContactNoteRepository
{
    Task<IReadOnlyList<ContactNote>> ListByContactAsync(Guid contactId, CancellationToken ct);
    Task AddAsync(ContactNote note, CancellationToken ct);
    Task<ContactNote?> GetByIdAsync(Guid id, CancellationToken ct);
}
