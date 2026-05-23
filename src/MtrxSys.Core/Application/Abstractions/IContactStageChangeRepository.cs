using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public interface IContactStageChangeRepository
{
    Task<IReadOnlyList<ContactStageChange>> ListByContactAsync(Guid contactId, CancellationToken ct);
    Task AddAsync(ContactStageChange change, CancellationToken ct);
}
