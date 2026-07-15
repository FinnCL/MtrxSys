using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Fachada do cliente WAHA: implementa <see cref="IWahaClient"/> delegando pros clientes por
/// responsabilidade (sessão, mensagens, grupos). Mantém a interface única que os ~20 consumidores
/// (e os testes E2E) já usam — a divisão é interna. Registrado via AddHttpClient&lt;IWahaClient,
/// WahaClient&gt;, então recebe o HttpClient configurado e monta os colaboradores em volta dele.</summary>
internal sealed class WahaClient : IWahaClient
{
    private readonly WahaSessionClient _session;
    private readonly WahaMessagingClient _messaging;
    private readonly WahaGroupsClient _groups;
    private readonly WahaContactsClient _contacts;

    // cache é opcional (default null) pra preservar o ctor de 2 args que os testes E2E usam; no app o
    // DI injeta o IMemoryCache singleton (ActivatorUtilities resolve params opcionais registrados).
    public WahaClient(HttpClient http, IOptions<WahaOptions> opts, IMemoryCache? cache = null)
    {
        var shared = new WahaHttp(http, opts);
        _session = new WahaSessionClient(shared);
        _messaging = new WahaMessagingClient(shared);
        _groups = new WahaGroupsClient(shared, _session, cache); // grupos usa a sessão pra saber o próprio número
        _contacts = new WahaContactsClient(shared);
    }

    // Sessão
    public Task<WahaSessionStatus> GetSessionStatusAsync(string sessionId, CancellationToken ct) =>
        _session.GetSessionStatusAsync(sessionId, ct);
    public Task<string?> GetOwnPhoneE164Async(string sessionId, CancellationToken ct) =>
        _session.GetOwnPhoneE164Async(sessionId, ct);
    public Task<WahaSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken ct) =>
        _session.GetSessionSnapshotAsync(sessionId, ct);
    public Task<bool> IsProxyReadyAsync(string sessionId, CancellationToken ct) =>
        _session.IsProxyReadyAsync(sessionId, ct);
    public Task<string?> ResolveLidToPhoneE164Async(string sessionId, string lid, CancellationToken ct) =>
        _session.ResolveLidToPhoneE164Async(sessionId, lid, ct);
    public Task EnsureSessionStartedAsync(string sessionId, CancellationToken ct) =>
        _session.EnsureSessionStartedAsync(sessionId, ct);
    public Task RestartSessionAsync(string sessionId, CancellationToken ct) =>
        _session.RestartSessionAsync(sessionId, ct);
    public Task LogoutSessionAsync(string sessionId, CancellationToken ct) =>
        _session.LogoutSessionAsync(sessionId, ct);
    public Task DeleteSessionAsync(string sessionId, CancellationToken ct) =>
        _session.DeleteSessionAsync(sessionId, ct);
    public Task<byte[]> GetQrPngAsync(string sessionId, CancellationToken ct) =>
        _session.GetQrPngAsync(sessionId, ct);
    public Task<string> GetQrRawAsync(string sessionId, CancellationToken ct) =>
        _session.GetQrRawAsync(sessionId, ct);
    public Task<string> RequestPairingCodeAsync(string sessionId, string phoneNumber, CancellationToken ct) =>
        _session.RequestPairingCodeAsync(sessionId, phoneNumber, ct);
    public Task<bool> EnsureWebhookConfiguredAsync(
        string sessionId, string webhookUrl, IReadOnlyList<string> events, string? webhookToken, CancellationToken ct) =>
        _session.EnsureWebhookConfiguredAsync(sessionId, webhookUrl, events, webhookToken, ct);

    // Mensagens
    public Task<IReadOnlyList<WahaChat>> ListChatsOverviewAsync(string sessionId, int limit, CancellationToken ct) =>
        _messaging.ListChatsOverviewAsync(sessionId, limit, ct);
    public Task<IReadOnlyList<WahaMessage>> GetChatMessagesAsync(string sessionId, string chatId, int limit, CancellationToken ct) =>
        _messaging.GetChatMessagesAsync(sessionId, chatId, limit, ct);
    public Task<string> SendTextAsync(string sessionId, string phoneOrChatId, string text, CancellationToken ct) =>
        _messaging.SendTextAsync(sessionId, phoneOrChatId, text, ct);
    public Task<WahaNumberCheck?> CheckNumberExistsAsync(string sessionId, string phoneE164, CancellationToken ct) =>
        _messaging.CheckNumberExistsAsync(sessionId, phoneE164, ct);
    public Task<string> SendImageAsync(string sessionId, string phoneOrChatId, byte[] imageData, string mimeType, string caption, CancellationToken ct) =>
        _messaging.SendImageAsync(sessionId, phoneOrChatId, imageData, mimeType, caption, ct);
    public Task StartTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct) =>
        _messaging.StartTypingAsync(sessionId, phoneOrChatId, ct);
    public Task StopTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct) =>
        _messaging.StopTypingAsync(sessionId, phoneOrChatId, ct);

    // Contatos
    public Task<IReadOnlyList<WahaContact>> ListContactsAsync(string sessionId, CancellationToken ct) =>
        _contacts.ListContactsAsync(sessionId, ct);

    // Grupos
    public Task<WahaGroup> CreateGroupAsync(
        string sessionId, string name, IReadOnlyCollection<string> participantsE164, CancellationToken ct) =>
        _groups.CreateGroupAsync(sessionId, name, participantsE164, ct);
    public Task<IReadOnlyList<WahaGroup>> ListGroupsAsync(string sessionId, CancellationToken ct) =>
        _groups.ListGroupsAsync(sessionId, ct);
    public Task<IReadOnlyList<WahaParticipant>> ListGroupParticipantsAsync(string sessionId, string groupId, CancellationToken ct) =>
        _groups.ListGroupParticipantsAsync(sessionId, groupId, ct);
    public Task LeaveGroupAsync(string sessionId, string groupId, CancellationToken ct) =>
        _groups.LeaveGroupAsync(sessionId, groupId, ct);
    public Task<WahaGroup> JoinGroupByInviteAsync(string sessionId, string inviteCodeOrUrl, CancellationToken ct) =>
        _groups.JoinGroupByInviteAsync(sessionId, inviteCodeOrUrl, ct);
}
