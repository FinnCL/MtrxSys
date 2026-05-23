using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Api.Endpoints;

public static class ConversationsEndpoints
{
    public static IEndpointRouteBuilder MapConversationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapGet("/", async (
            int? limit,
            int? offset,
            IConversationRepository repo,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            var items = await repo.ListAsync(take, skip, ct);
            return Results.Ok(items.Select(ToDto));
        });

        group.MapGet("/{id:guid}/messages", async (
            Guid id,
            int? limit,
            int? offset,
            IConversationRepository conv,
            IChatMessageRepository msgs,
            CancellationToken ct) =>
        {
            var conversation = await conv.GetByIdAsync(id, ct);
            if (conversation is null)
            {
                return Results.NotFound();
            }
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            var items = await msgs.ListByConversationAsync(id, take, skip, ct);
            return Results.Ok(new
            {
                conversation = ToDto(conversation),
                messages = items.Select(ToDto),
            });
        });

        group.MapPost("/{id:guid}/messages", async (
            Guid id,
            SendMessageRequest req,
            IConversationRepository conv,
            IChatMessageRepository msgs,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text))
            {
                return Results.Problem("text is required", statusCode: 400);
            }

            var conversation = await conv.GetByIdAsync(id, ct);
            if (conversation is null)
            {
                return Results.NotFound();
            }

            var sessionId = dispatch.Value.SessionId;
            var waMessageId = await waha.SendTextAsync(sessionId, conversation.WaChatId, req.Text, ct);
            if (string.IsNullOrEmpty(waMessageId))
            {
                return Results.Problem("waha did not return a message id", statusCode: 502);
            }

            var existing = await msgs.GetByWaMessageIdAsync(waMessageId, ct);
            if (existing is null)
            {
                var now = clock.UtcNow;
                var message = ChatMessage.Create(
                    id: Guid.NewGuid(),
                    conversationId: conversation.Id,
                    waMessageId: waMessageId,
                    direction: MessageDirection.Outbound,
                    authorPhone: null,
                    body: req.Text,
                    timestamp: now);
                await msgs.AddAsync(message, ct);
                conversation.TouchLastMessage(now, req.Text);
                await conv.UpdateAsync(conversation, ct);
                await uow.SaveChangesAsync(ct);
                return Results.Ok(ToDto(message));
            }
            return Results.Ok(ToDto(existing));
        });

        return app;
    }

    private static ConversationDto ToDto(Conversation c) => new(
        c.Id,
        c.WaChatId,
        c.ContactId,
        c.Title,
        c.IsGroup,
        c.LastMessageAt,
        c.LastMessagePreview,
        c.CreatedAt);

    private static ChatMessageDto ToDto(ChatMessage m) => new(
        m.Id,
        m.ConversationId,
        m.WaMessageId,
        m.Direction.ToString(),
        m.AuthorPhone,
        m.Body,
        m.Timestamp,
        m.MediaUrl);

    public sealed record SendMessageRequest(string Text);

    public sealed record ConversationDto(
        Guid Id,
        string WaChatId,
        Guid? ContactId,
        string? Title,
        bool IsGroup,
        DateTimeOffset? LastMessageAt,
        string? LastMessagePreview,
        DateTimeOffset CreatedAt);

    public sealed record ChatMessageDto(
        Guid Id,
        Guid ConversationId,
        string WaMessageId,
        string Direction,
        string? AuthorPhone,
        string Body,
        DateTimeOffset Timestamp,
        string? MediaUrl);
}
