using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public interface IContactRepository
{
    Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Contact?> GetByPhoneAsync(string e164, CancellationToken ct);
    Task AddAsync(Contact contact, CancellationToken ct);
    Task UpdateAsync(Contact contact, CancellationToken ct);
    Task<IReadOnlyList<Contact>> ListByFilterAsync(ContactFilter filter, CancellationToken ct);
    Task<int> CountByFilterAsync(ContactFilter filter, CancellationToken ct);
    Task<IReadOnlyList<ContactGroupTag>> ListGroupTagsAsync(CancellationToken ct);
    /// <summary>Exclui os contatos de um grupo (e suas conversas/mensagens/disparos), sem deixar órfãos.</summary>
    Task<int> DeleteByGroupTagAsync(string groupTag, CancellationToken ct);
}

public sealed record ContactFilter(
    ContactStage? Stage = null,
    string? TagName = null,
    string? GroupTag = null,
    bool ExcludeOptedOut = true,
    bool EngagedOnly = false);

public sealed record ContactGroupTag(string GroupTag, int Count);
