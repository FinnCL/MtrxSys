using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Conversations;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Api.Endpoints;

public static class ConversationsEndpoints
{
    public static IEndpointRouteBuilder MapConversationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations");

        group.MapGet("/", async (
            string? status,
            string? search,
            int? limit,
            int? offset,
            IConversationRepository repo,
            CancellationToken ct) =>
        {
            // Paginação no servidor: cada chamada traz só uma página (~50) já filtrada por
            // status/busca. Escala sem teto — não importa quantas conversas existam no total.
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            var items = await repo.ListByStatusAsync(status, search, take, skip, ct);
            return Results.Ok(items.Select(ToDto));
        });

        // Religa conversas órfãs (sem contato) ao contato — conserta as que nasceram durante
        // instabilidade da sessão (LID resolvia null). Idempotente; chamado na conexão e manual.
        group.MapPost("/relink", async (RelinkOrphanConversationsUseCase relink, CancellationToken ct) =>
        {
            var linked = await relink.RunAsync(ct);
            return Results.Ok(new { linked });
        });

        group.MapGet("/counts", async (
            string? search,
            IConversationRepository repo,
            CancellationToken ct) =>
        {
            var counts = await repo.CountByStatusAsync(search, ct);
            return Results.Ok(counts);
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
            IContactRepository contacts,
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

            // SAIR é sagrado — TAMBÉM na resposta manual. Sem isto, este endpoint era a brecha que
            // deixava enviar pra quem deu opt-out (o resto do sistema bloqueia). Grupo/sem-contato não
            // tem opt-out individual, então só checa quando há contato vinculado.
            if (conversation.ContactId is { } contactId)
            {
                var contact = await contacts.GetByIdAsync(contactId, ct);
                if (contact?.OptOutAt is not null)
                {
                    return Results.Problem("Este contato pediu para SAIR — envio bloqueado.", statusCode: 409);
                }
            }

            var sessionId = dispatch.Value.SessionId;
            // Não envia em sessão degradada (gatilho nº1 de ban): só com a sessão WORKING. Antes este
            // endpoint disparava em qualquer estado (logo após parear, em SCAN_QR, na janela de settle).
            var status = await waha.GetSessionStatusAsync(sessionId, ct);
            if (status is not WahaSessionStatus.Working)
            {
                return Results.Problem(
                    $"WhatsApp não está conectado (status={status}); tente de novo quando reconectar.",
                    statusCode: 409);
            }
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
