namespace MtrxSys.Core.Application.Abstractions;

public interface IWahaClient
{
    Task<WahaSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken ct);
    /// <summary>Telefone E.164 do número conectado na sessão ("me"), ou null se indisponível.</summary>
    Task<string?> GetOwnPhoneE164Async(string sessionId, CancellationToken ct);
    /// <summary>Status + identidade (número/nome) da sessão numa ÚNICA leitura (evita 2 GETs).</summary>
    Task<WahaSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken ct);
    /// <summary>Resolve um LID (@lid, número oculto) para o telefone E.164 real, ou null se não der.</summary>
    Task<string?> ResolveLidToPhoneE164Async(string sessionId, string lid, CancellationToken ct);
    Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct);
    /// <summary>Reinicia a sessão (stop+start). Necessário pra recuperar do estado FAILED, onde um simples start é rejeitado.</summary>
    Task RestartSessionAsync(string sessionId, CancellationToken ct);
    /// <summary>Desconecta o número da sessão (logout no WhatsApp). Depois é preciso parear de novo via QR.</summary>
    Task LogoutSessionAsync(string sessionId, CancellationToken ct);
    /// <summary>Apaga a sessão por completo (config + credenciais em disco). Diferente do logout, não
    /// deixa resíduo pro WAHA restaurar — garante QR novo no próximo start. Idempotente (404 = ok).</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct);
    Task<byte[]> GetQrPngAsync(string sessionId, CancellationToken ct);
    Task<string> GetQrRawAsync(string sessionId, CancellationToken ct);
    /// <summary>Pede um código de pareamento por número (NOWEB) — alternativa ao QR, imune ao timing
    /// de rotação do QR. O usuário digita o código no WhatsApp em "Conectar com número de telefone".
    /// phoneNumber deve ser só dígitos com DDI (ex.: 5571999998888). Retorna o código (ex.: "ABCD-EFGH").</summary>
    Task<string> RequestPairingCodeAsync(string sessionId, string phoneNumber, CancellationToken ct);

    Task<IReadOnlyList<WahaChat>> ListChatsOverviewAsync(string sessionId, int limit, CancellationToken ct);
    Task<IReadOnlyList<WahaMessage>> GetChatMessagesAsync(string sessionId, string chatId, int limit, CancellationToken ct);

    Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<WahaParticipant>> ListGroupParticipantsAsync(string sessionId, string groupId, CancellationToken ct);
    /// <summary>Faz o número conectado SAIR do grupo. Só 404 (já não é membro ou grupo inexistente)
    /// conta como sucesso; 422/409/5xx sobem como erro, pra não dar falso "saiu" deixando o usuário
    /// ainda no grupo.</summary>
    Task LeaveGroupAsync(string sessionId, string groupId, CancellationToken ct);
    Task<WahaGroup> JoinGroupByInviteAsync(string sessionId, string inviteCodeOrUrl, CancellationToken ct);

    Task<string> SendTextAsync(string sessionId, string phoneOrChatId, string text, CancellationToken ct);
    /// <summary>Envia uma imagem (bytes + mimetype) com legenda opcional. Retorna o id da mensagem no WhatsApp.</summary>
    Task<string> SendImageAsync(string sessionId, string phoneOrChatId, byte[] imageData, string mimeType, string caption, CancellationToken ct);
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

public sealed record WahaIdentity(string PhoneE164, string? Name);

public sealed record WahaSessionSnapshot(WahaSessionStatus Status, WahaIdentity? Identity);

public sealed record WahaGroup(string Id, string Name, int? ParticipantsCount);

public sealed record WahaParticipant(string Id, string PhoneE164, string? Name, bool IsAdmin);

public sealed record WahaChat(string Id, string Name, string? LastMessagePreview, DateTimeOffset? LastMessageAt, bool IsGroup);

public sealed record WahaMessage(string Id, string ChatId, bool FromMe, string Author, string Body, DateTimeOffset Timestamp);
