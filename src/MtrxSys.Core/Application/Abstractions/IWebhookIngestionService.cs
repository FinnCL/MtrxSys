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
    string? NotifyName = null);

public sealed record WahaMediaInfo(string? Url, string? Mimetype, string? Filename);
