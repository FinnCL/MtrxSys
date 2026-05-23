using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public interface IContactTagRepository
{
    Task<IReadOnlyList<ContactTag>> ListAllAsync(CancellationToken ct);
    Task<ContactTag?> GetByNameAsync(string name, CancellationToken ct);
    Task AddAsync(ContactTag tag, CancellationToken ct);
    Task<IReadOnlyList<string>> ListTagsForContactAsync(Guid contactId, CancellationToken ct);
    Task AssignAsync(ContactTagAssignment assignment, CancellationToken ct);
    Task UnassignAsync(Guid contactId, string tagName, CancellationToken ct);
}
