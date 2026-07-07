using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface ISendAuditRepository
{
    Task AddAsync(SendAuditEntry entry, CancellationToken ct);

    // Busca a entrada de auditoria de UM envio nosso pelo id "core" da mensagem — pro handler do
    // message.ack atualizar o estado de entrega. Null = não é mensagem de disparo (ex.: resposta manual).
    Task<SendAuditEntry?> GetByWaMessageIdAsync(string waMessageId, CancellationToken ct);

    // Contagem de envios e entregas (ack >= 2) desde `since` — pra taxa de entrega (sensor de restrição).
    Task<DeliveryStats> GetDeliveryStatsAsync(DateTimeOffset since, CancellationToken ct);
}

// Enviados x entregues numa janela — base da "saúde de entrega" (queda = possível shadow-restriction).
public sealed record DeliveryStats(int Sent, int Delivered);
