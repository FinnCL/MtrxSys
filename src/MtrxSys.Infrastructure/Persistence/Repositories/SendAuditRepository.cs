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
        // Enviados x entregues numa ÚNICA query (COUNT + SUM condicional) — um round-trip só.
        // AsNoTracking = leitura barata; o índice em occurred_at cobre o filtro da janela.
        // IGNORA o Círculo de Aquecimento (contatos do próprio operador): eles SEMPRE leem, então
        // inflariam a taxa entregue/enviado e cegariam o guard de shadow-restriction pros frios REAIS.
        // Anti-join contra warmup_circle (mesmo banco) — mede só envio a terceiro real.
        var row = await db.SendAuditLog.AsNoTracking()
            .Where(e => e.OccurredAt >= since)
            .Where(e => !db.WarmupCircle.Any(w => w.PhoneE164 == e.PhoneE164))
            .GroupBy(_ => 1)
            .Select(g => new { Sent = g.Count(), Delivered = g.Sum(e => e.DeliveredAt != null ? 1 : 0) })
            .FirstOrDefaultAsync(ct);
        return row is null ? new DeliveryStats(0, 0) : new DeliveryStats(row.Sent, row.Delivered);
    }
}
