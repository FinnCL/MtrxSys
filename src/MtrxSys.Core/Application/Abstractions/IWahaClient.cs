namespace MtrxSys.Core.Application.Abstractions;

public interface IWahaClient
{
    Task<WahaSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken ct);
    Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct);
    Task<byte[]> GetQrPngAsync(string sessionId, CancellationToken ct);
    Task<string> GetQrRawAsync(string sessionId, CancellationToken ct);

    Task<IReadOnlyList<WahaChat>> ListChatsOverviewAsync(string sessionId, int limit, CancellationToken ct);
    Task<IReadOnlyList<WahaMessage>> GetChatMessagesAsync(string sessionId, string chatId, int limit, CancellationToken ct);

    Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<WahaParticipant>> ListGroupParticipantsAsync(string sessionId, string groupId, CancellationToken ct);
    Task<WahaGroup> JoinGroupByInviteAsync(string sessionId, string inviteCodeOrUrl, CancellationToken ct);

    Task<string> SendTextAsync(string sessionId, string phoneOrChatId, string text, CancellationToken ct);
    Task StartTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct);
    Task StopTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct);

    Task<bool> EnsureWebhookConfiguredAsync(string sessionId, string webhookUrl, IReadOnlyList<string> events, CancellationToken ct);
}

public enum WahaSessionStatus
{
    Unknown = 0,
    Stopped,
    Starting,
    ScanQrCode,
    Working,
    Failed,
}

public sealed record WahaGroup(string Id, string Name, int? ParticipantsCount);

public sealed record WahaParticipant(string Id, string PhoneE164, string? Name, bool IsAdmin);

public sealed record WahaChat(string Id, string Name, string? LastMessagePreview, DateTimeOffset? LastMessageAt, bool IsGroup);

public sealed record WahaMessage(string Id, string ChatId, bool FromMe, string Author, string Body, DateTimeOffset Timestamp);
