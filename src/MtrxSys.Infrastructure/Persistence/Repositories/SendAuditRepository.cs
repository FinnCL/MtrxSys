using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class SendAuditRepository(MtrxDbContext db) : ISendAuditRepository
{
    public async Task AddAsync(SendAuditEntry entry, CancellationToken ct) =>
        await db.SendAuditLog.AddAsync(entry, ct);

    // Rastreado: a entidade volta no change tracker, então o MarkAck do handler é persistido no
    // SaveChanges seguinte, sem UpdateAsync explícito. Pega a mais recente (o id core pode repetir
    // em teoria; na prática o disparo é sequencial e o id é único por envio).
    public Task<SendAuditEntry?> GetByWaMessageIdAsync(string waMessageId, CancellationToken ct) =>
        db.SendAuditLog
            .Where(e => e.WaMessageId == waMessageId)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(ct);

    public async Task<DeliveryStats> GetDeliveryStatsAsync(DateTimeOffset since, CancellationToken ct)
    {
        // Só conta envios que já tiveram tempo de entregar (a entrega leva segundos): considera a
        // janela inteira; a UI mostra a taxa e o operador interpreta. AsNoTracking = leitura barata.
        var q = db.SendAuditLog.AsNoTracking().Where(e => e.OccurredAt >= since);
        var sent = await q.CountAsync(ct);
        var delivered = await q.CountAsync(e => e.DeliveredAt != null, ct);
        return new DeliveryStats(sent, delivered);
    }
}
