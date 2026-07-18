using MtrxSys.Core.Domain.Funnel;

namespace MtrxSys.Core.Application.Abstractions;

public interface IFunnelInviteRepository
{
    Task AddAsync(FunnelInvite invite, CancellationToken ct);
    /// <summary>O convite ABERTO mais recente do contato (engaged_at IS NULL). Usado pelo webhook no
    /// 1º inbound pra saber se deve disparar a auto-resposta. null = sem convite aberto.</summary>
    Task<FunnelInvite?> GetOpenByContactIdAsync(Guid contactId, CancellationToken ct);
    /// <summary>Convites ABERTOS (engaged_at IS NULL) dos contatos dados, num único SELECT. Usado na
    /// geração de links pra reusar o convite aberto do contato em vez de criar um duplicado.</summary>
    Task<IReadOnlyList<FunnelInvite>> ListOpenByContactIdsAsync(IReadOnlyCollection<Guid> contactIds, CancellationToken ct);
    Task UpdateAsync(FunnelInvite invite, CancellationToken ct);
    /// <summary>Convites recentes (pro painel), do mais novo pro mais antigo, com teto.</summary>
    Task<IReadOnlyList<FunnelInvite>> ListRecentAsync(int limit, CancellationToken ct);
}
