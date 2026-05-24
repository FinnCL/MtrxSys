using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Webhooks;

public sealed class WebhookIngestionService(
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IContactRepository contacts,
    IContactStageChangeRepository stageChanges,
    IUnitOfWork uow,
    IClock clock,
    BrazilPhoneValidator phones,
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
                var phone = phones.NormalizeTrusted(e164);
                // O nome público (NotifyName) só vale quando é a pessoa que mandou (inbound).
                var inboundName = p.FromMe == true ? null : p.NotifyName;
                var contact = await contacts.GetByPhoneAsync(phone.E164, ct);
                if (contact is null)
                {
                    contact = Contact.Create(
                        id: Guid.NewGuid(),
                        phone: phone,
                        name: inboundName,
                        groupTag: null,
                        theme: null,
                        optInAt: now);
                    await contacts.AddAsync(contact, ct);
                }
                else
                {
                    // Backfill: se o contato veio sem nome (ex.: importado de grupo) e agora
                    // respondeu, preenche com o nome público dele.
                    contact.FillNameIfEmpty(inboundName);
                    await contacts.UpdateAsync(contact, ct);
                }
                contactId = contact.Id;

                // Classificação automática quando o CONTATO respondeu (mensagem recebida).
                if (p.FromMe != true)
                {
                    await ApplyInboundClassificationAsync(contact, p.Body, now, ct);
                }
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

    // Move o contato no funil com base na resposta recebida:
    // - pediu pra sair ("SAIR" etc.) → opt-out + "Descartado" (Lost);
    // - respondeu qualquer outra coisa e ainda estava em "Novo" (Lead) → "Respondeu" (Qualified).
    // Não rebaixa quem já avançou (ex.: Cliente continua Cliente).
    private async Task ApplyInboundClassificationAsync(Contact contact, string? body, DateTimeOffset now, CancellationToken ct)
    {
        if (OptOutDetector.IsOptOut(body))
        {
            if (contact.OptOutAt is null)
            {
                contact.OptOut(now);
                await contacts.UpdateAsync(contact, ct);
                log.LogInformation("Contato {ContactId} pediu opt-out via resposta", contact.Id);
            }
            await MoveStageAsync(contact, ContactStage.Lost, now, ct);
            return;
        }

        if (contact.Stage == ContactStage.Lead)
        {
            await MoveStageAsync(contact, ContactStage.Qualified, now, ct);
        }
    }

    private async Task MoveStageAsync(Contact contact, ContactStage to, DateTimeOffset now, CancellationToken ct)
    {
        var previous = contact.ChangeStage(to, now);
        if (previous is null)
        {
            return;
        }
        await contacts.UpdateAsync(contact, ct);
        await stageChanges.AddAsync(
            ContactStageChange.Create(
                id: Guid.NewGuid(),
                contactId: contact.Id,
                fromStage: previous,
                toStage: to,
                changedAt: now,
                changedByUserId: Guid.Empty),
            ct);
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
