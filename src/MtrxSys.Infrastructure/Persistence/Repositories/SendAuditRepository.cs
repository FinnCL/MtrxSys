using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class SendAuditRepository(MtrxDbContext db) : ISendAuditRepository
{
    public async Task AddAsync(SendAuditEntry entry, CancellationToken ct) =>
        await db.SendAuditLog.AddAsync(entry, ct);
}
