using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Core.Application.Abstractions;

public interface IOwnedGroupRepository
{
    Task<IReadOnlyList<OwnedGroup>> ListAsync(CancellationToken ct);

    /// <summary>Os ids (forma do ListGroups) dos grupos criados por este sistema. É o que a listagem
    /// usa pra marcar `isMine` sem N consultas.</summary>
    Task<IReadOnlySet<string>> ListWaGroupIdsAsync(CancellationToken ct);

    Task<OwnedGroup?> GetByWaGroupIdAsync(string waGroupId, CancellationToken ct);

    Task AddAsync(OwnedGroup group, CancellationToken ct);

    /// <summary>Esquece que o grupo é nosso (o grupo em si continua no WhatsApp). Retorna false se
    /// já não havia registro — idempotente.</summary>
    Task<bool> RemoveAsync(string waGroupId, CancellationToken ct);
}
