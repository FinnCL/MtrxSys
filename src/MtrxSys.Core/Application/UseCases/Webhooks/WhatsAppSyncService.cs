using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.UseCases.Webhooks;

public sealed class WhatsAppSyncService(
    IWahaClient waha,
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IContactRepository contacts,
    IUnitOfWork uow,
    IClock clock,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<WhatsAppSyncService> log)
{
    public async Task<SyncResult> SyncAsync(int messagesPerChat, CancellationToken ct)
    {
        var sessionId = dispatchOpts.Value.SessionId;
        var chats = await waha.ListChatsOverviewAsync(sessionId, limit: 500, ct);
        log.LogInformation("Sync: WAHA returned {Count} chats", chats.Count);

        var chatsTouched = 0;
        var messagesImported = 0;
        var contactsCreated = 0;
        var failures = new List<string>();

        foreach (var chat in chats)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await SyncOneAsync(chat, messagesPerChat, ct);
                chatsTouched++;
                messagesImported += result.MessagesImported;
                if (result.ContactCreated)
                {
                    contactsCreated++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                failures.Add($"{chat.Id}: {ex.Message}");
                log.LogWarning(ex, "Sync: failed for chat {ChatId}", chat.Id);
            }
#pragma warning restore CA1031
        }

        await uow.SaveChangesAsync(ct);
        return new SyncResult(chatsTouched, messagesImported, contactsCreated, failures);
    }

    private async Task<ChatSyncResult> SyncOneAsync(WahaChat chat, int messagesPerChat, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var kind = WahaChatIdentifier.Classify(chat.Id);
        var isGroup = kind == WahaChatIdentifier.Kind.Group || chat.IsGroup;

        Guid? contactId = null;
        var contactCreated = false;
        if (kind == WahaChatIdentifier.Kind.Individual)
        {
            var e164 = WahaChatIdentifier.TryExtractPhoneE164(chat.Id);
            if (e164 is not null)
            {
                var contact = await contacts.GetByPhoneAsync(e164, ct);
                if (contact is null)
                {
                    contact = Contact.Create(
                        id: Guid.NewGuid(),
                        phone: PhoneNumber.FromValidatedE164(e164),
                        name: chat.Name,
                        groupTag: null,
                        theme: null,
                        optInAt: now);
                    await contacts.AddAsync(contact, ct);
                    contactCreated = true;
                }
                contactId = contact.Id;
            }
        }

        var conversation = await conversations.GetByWaChatIdAsync(chat.Id, ct);
        if (conversation is null)
        {
            conversation = Conversation.Create(
                id: Guid.NewGuid(),
                waChatId: chat.Id,
                contactId: contactId,
                title: chat.Name,
                isGroup: isGroup,
                createdAt: now);
            await conversations.AddAsync(conversation, ct);
        }
        else
        {
            if (conversation.ContactId is null && contactId is not null)
            {
                conversation.LinkContact(contactId.Value);
            }
            if (!string.IsNullOrWhiteSpace(chat.Name) && !string.Equals(conversation.Title, chat.Name, StringComparison.Ordinal))
            {
                conversation.Rename(chat.Name);
            }
        }

        var sessionId = dispatchOpts.Value.SessionId;
        var msgs = await waha.GetChatMessagesAsync(sessionId, chat.Id, messagesPerChat, ct);

        var imported = 0;
        DateTimeOffset? latestAt = null;
        string? latestPreview = null;

        foreach (var m in msgs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(m.Id))
            {
                continue;
            }

            if (latestAt is null || m.Timestamp > latestAt)
            {
                latestAt = m.Timestamp;
                latestPreview = m.Body;
            }

            var existing = await messages.GetByWaMessageIdAsync(m.Id, ct);
            if (existing is not null)
            {
                continue;
            }

            var direction = m.FromMe ? MessageDirection.Outbound : MessageDirection.Inbound;
            var authorPhone = ResolveAuthorPhone(kind, chat.Id, direction, m.Author);

            var entry = ChatMessage.Create(
                id: Guid.NewGuid(),
                conversationId: conversation.Id,
                waMessageId: m.Id,
                direction: direction,
                authorPhone: authorPhone,
                body: m.Body ?? string.Empty,
                timestamp: m.Timestamp);
            await messages.AddAsync(entry, ct);
            imported++;
        }

        if (latestAt is not null)
        {
            conversation.TouchLastMessage(latestAt.Value, latestPreview);
        }
        else if (chat.LastMessageAt is not null)
        {
            conversation.TouchLastMessage(chat.LastMessageAt.Value, chat.LastMessagePreview);
        }

        return new ChatSyncResult(imported, contactCreated);
    }

    private static string? ResolveAuthorPhone(
        WahaChatIdentifier.Kind kind,
        string chatId,
        MessageDirection direction,
        string? participant)
    {
        if (kind == WahaChatIdentifier.Kind.Group)
        {
            return string.IsNullOrEmpty(participant)
                ? null
                : WahaChatIdentifier.TryExtractPhoneE164(participant)
                  ?? "+" + WahaChatIdentifier.ExtractDigits(participant);
        }
        if (direction == MessageDirection.Inbound && kind == WahaChatIdentifier.Kind.Individual)
        {
            return WahaChatIdentifier.TryExtractPhoneE164(chatId);
        }
        return null;
    }

    private readonly record struct ChatSyncResult(int MessagesImported, bool ContactCreated);
}

public sealed record SyncResult(
    int ChatsTouched,
    int MessagesImported,
    int ContactsCreated,
    IReadOnlyList<string> Failures);
