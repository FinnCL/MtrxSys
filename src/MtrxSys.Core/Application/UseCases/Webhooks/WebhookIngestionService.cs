using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.UseCases.Webhooks;

public sealed class WebhookIngestionService(
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IContactRepository contacts,
    IUnitOfWork uow,
    IClock clock,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<WebhookIngestionService> log) : IWebhookIngestionService
{
    public async Task IngestAsync(WahaWebhookEvent evt, CancellationToken ct)
    {
        if (evt.Event is null || !WahaEvents.InboundMessageEvents.Contains(evt.Event))
        {
            log.LogDebug("Ignoring webhook event {Event}", evt.Event);
            return;
        }

        var expectedSession = dispatchOpts.Value.SessionId;
        if (!string.Equals(evt.Session, expectedSession, StringComparison.Ordinal))
        {
            log.LogWarning("Webhook for session {Session}, expected {Expected}; ignoring", evt.Session, expectedSession);
            return;
        }

        if (evt.Payload is null || string.IsNullOrWhiteSpace(evt.Payload.Id) || string.IsNullOrWhiteSpace(evt.Payload.From))
        {
            log.LogWarning("Webhook payload missing id or from");
            return;
        }

        var p = evt.Payload;
        var existing = await messages.GetByWaMessageIdAsync(p.Id!, ct);
        if (existing is not null)
        {
            log.LogDebug("Duplicate message {WaId}, skipping", p.Id);
            return;
        }

        var chatId = p.From!;
        var kind = WahaChatIdentifier.Classify(chatId);

        if (p.FromMe == true && kind == WahaChatIdentifier.Kind.Individual && !string.IsNullOrEmpty(p.To))
        {
            chatId = p.To;
            kind = WahaChatIdentifier.Classify(chatId);
        }

        var now = clock.UtcNow;
        Guid? contactId = null;
        if (kind == WahaChatIdentifier.Kind.Individual)
        {
            var e164 = WahaChatIdentifier.TryExtractPhoneE164(chatId);
            if (e164 is not null)
            {
                var contact = await contacts.GetByPhoneAsync(e164, ct);
                if (contact is null)
                {
                    contact = Contact.Create(
                        id: Guid.NewGuid(),
                        phone: PhoneNumber.FromValidatedE164(e164),
                        name: p.NotifyName,
                        groupTag: null,
                        theme: null,
                        optInAt: now);
                    await contacts.AddAsync(contact, ct);
                }
                contactId = contact.Id;
            }
        }

        var conversation = await conversations.GetByWaChatIdAsync(chatId, ct);
        var conversationTitle = ResolveConversationTitle(kind, p.NotifyName);
        if (conversation is null)
        {
            conversation = Conversation.Create(
                id: Guid.NewGuid(),
                waChatId: chatId,
                contactId: contactId,
                title: conversationTitle,
                isGroup: kind == WahaChatIdentifier.Kind.Group,
                createdAt: now);
            await conversations.AddAsync(conversation, ct);
        }
        else
        {
            if (conversation.ContactId is null && contactId is not null)
            {
                conversation.LinkContact(contactId.Value);
            }
            if (!string.IsNullOrWhiteSpace(conversationTitle) && string.IsNullOrWhiteSpace(conversation.Title))
            {
                conversation.Rename(conversationTitle);
            }
        }

        var timestamp = p.Timestamp.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(p.Timestamp.Value)
            : now;
        var direction = p.FromMe == true ? MessageDirection.Outbound : MessageDirection.Inbound;
        var authorPhone = ResolveAuthorPhone(kind, chatId, direction, p);

        var message = ChatMessage.Create(
            id: Guid.NewGuid(),
            conversationId: conversation.Id,
            waMessageId: p.Id!,
            direction: direction,
            authorPhone: authorPhone,
            body: p.Body ?? string.Empty,
            timestamp: timestamp,
            mediaUrl: p.Media?.Url);
        await messages.AddAsync(message, ct);

        conversation.TouchLastMessage(timestamp, p.Body);

        await uow.SaveChangesAsync(ct);
    }

    private static string? ResolveConversationTitle(WahaChatIdentifier.Kind kind, string? notifyName) =>
        kind switch
        {
            WahaChatIdentifier.Kind.LinkedId when !string.IsNullOrWhiteSpace(notifyName) => notifyName,
            WahaChatIdentifier.Kind.Group when !string.IsNullOrWhiteSpace(notifyName) => notifyName,
            _ => notifyName,
        };

    private static string? ResolveAuthorPhone(
        WahaChatIdentifier.Kind kind,
        string chatId,
        MessageDirection direction,
        WahaMessagePayload p)
    {
        if (kind == WahaChatIdentifier.Kind.Group)
        {
            return string.IsNullOrEmpty(p.Participant)
                ? null
                : WahaChatIdentifier.TryExtractPhoneE164(p.Participant)
                  ?? "+" + WahaChatIdentifier.ExtractDigits(p.Participant);
        }
        if (direction == MessageDirection.Inbound && kind == WahaChatIdentifier.Kind.Individual)
        {
            return WahaChatIdentifier.TryExtractPhoneE164(chatId);
        }
        return null;
    }
}
