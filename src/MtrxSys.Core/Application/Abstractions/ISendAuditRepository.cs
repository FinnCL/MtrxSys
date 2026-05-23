using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface ISendAuditRepository
{
    Task AddAsync(SendAuditEntry entry, CancellationToken ct);
}
