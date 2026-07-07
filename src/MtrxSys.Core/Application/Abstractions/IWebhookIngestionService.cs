namespace MtrxSys.Core.Application.Abstractions;

public interface IWebhookIngestionService
{
    Task IngestAsync(WahaWebhookEvent evt, CancellationToken ct);
}

public sealed record WahaWebhookEvent(string? Event, string? Session, WahaMessagePayload? Payload);

public sealed record WahaMessagePayload(
    string? Id,
    long? Timestamp,
    string? From,
    string? To,
    bool? FromMe,
    string? Body,
    bool? HasMedia,
    WahaMediaInfo? Media,
    string? Participant,
    string? NotifyName = null,
    // Só no evento message.ack: estado de entrega da NOSSA mensagem (WAHA/whatsmeow):
    // -1 ERROR, 0 PENDING, 1 SERVER (saiu), 2 DEVICE (entregue), 3 READ, 4 PLAYED. Entregue = ack >= 2.
    int? Ack = null,
    string? AckName = null);

public sealed record WahaMediaInfo(string? Url, string? Mimetype, string? Filename);
