using MtrxSys.Core.Domain.Warmup;

namespace MtrxSys.Core.Application.Abstractions;

public interface IWarmupCircleRepository
{
    Task<IReadOnlyList<WarmupCircleMember>> ListAsync(CancellationToken ct);

    Task<WarmupCircleMember?> GetByPhoneAsync(string phoneE164, CancellationToken ct);

    Task AddAsync(WarmupCircleMember member, CancellationToken ct);

    /// <summary>Remove pelo id. Retorna false se já não existia (idempotente).</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken ct);
}
