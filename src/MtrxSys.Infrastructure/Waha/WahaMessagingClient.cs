using System.Net.Http.Json;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Mensageria WAHA: visão geral de chats, histórico de um chat, envio de texto/imagem e
/// indicadores de digitação. Uma responsabilidade só do antigo WahaClient.</summary>
internal sealed class WahaMessagingClient(WahaHttp http)
{
    public async Task<IReadOnlyList<WahaChat>> ListChatsOverviewAsync(string sessionId, int limit, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/chats/overview?limit={limit}");
        using var resp = await http.SendAsync(req, ct);
        // Histórico indisponível (ex.: NOWEB sem o "Store" → 400) = "sem chats", não erro fatal. O
        // sync vira no-op (nada a importar) em vez de estourar 500 no /api/waha/sync.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        var body = await resp.Content.ReadFromJsonAsync<List<ChatOverviewDto>>(WahaHttp.Json, ct) ?? [];
        return body.Select(c => new WahaChat(
            Id: c.Id ?? string.Empty,
            Name: c.Name ?? c.Id ?? "(sem nome)",
            LastMessagePreview: c.LastMessage?.Body,
            LastMessageAt: c.LastMessage?.Timestamp is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
            IsGroup: WahaParsing.IsGroupChat(c.Id))).ToList();
    }

    public async Task<IReadOnlyList<WahaMessage>> GetChatMessagesAsync(string sessionId, string chatId, int limit, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Get, $"api/{WahaHttp.Esc(sessionId)}/chats/{WahaHttp.Esc(chatId)}/messages?limit={limit}&downloadMedia=false");
        using var resp = await http.SendAsync(req, ct);
        // Mesma resiliência do overview: histórico indisponível (NOWEB sem store) → sem mensagens.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        var body = await resp.Content.ReadFromJsonAsync<List<MessageDto>>(WahaHttp.Json, ct) ?? [];
        return body
            .OrderBy(m => m.Timestamp ?? 0)
            .Select(m => new WahaMessage(
                Id: m.Id ?? string.Empty,
                ChatId: chatId,
                FromMe: m.FromMe ?? false,
                Author: m.Author ?? m.From ?? "",
                Body: m.Body ?? string.Empty,
                Timestamp: DateTimeOffset.FromUnixTimeSeconds(m.Timestamp ?? 0)))
            .ToList();
    }

    public async Task<string> SendTextAsync(string sessionId, string phoneOrChatId, string text, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, "api/sendText");
        req.Content = JsonContent.Create(new
        {
            chatId = WahaParsing.ToChatId(phoneOrChatId),
            text,
            session = sessionId,
        }, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await WahaParsing.ReadSentMessageIdAsync(resp, ct);
    }

    public async Task<string> SendImageAsync(
        string sessionId, string phoneOrChatId, byte[] imageData, string mimeType, string caption, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, "api/sendImage");
        req.Content = JsonContent.Create(new
        {
            chatId = WahaParsing.ToChatId(phoneOrChatId),
            caption,
            file = new
            {
                data = Convert.ToBase64String(imageData),
                mimetype = mimeType,
                filename = "image" + WahaParsing.ExtensionFor(mimeType),
            },
            session = sessionId,
        }, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await WahaParsing.ReadSentMessageIdAsync(resp, ct);
    }

    public Task StartTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct) =>
        PostPresenceAsync(sessionId, phoneOrChatId, "api/startTyping", ct);

    public Task StopTypingAsync(string sessionId, string phoneOrChatId, CancellationToken ct) =>
        PostPresenceAsync(sessionId, phoneOrChatId, "api/stopTyping", ct);

    private async Task PostPresenceAsync(string sessionId, string phoneOrChatId, string path, CancellationToken ct)
    {
        using var req = http.NewRequest(HttpMethod.Post, path);
        req.Content = JsonContent.Create(new
        {
            chatId = WahaParsing.ToChatId(phoneOrChatId),
            session = sessionId,
        }, options: WahaHttp.Json);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
