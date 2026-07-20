using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Webhooks;

public sealed class WhatsAppSyncService(
    IWahaClient waha,
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IContactRepository contacts,
    IUnitOfWork uow,
    IClock clock,
    BrazilPhoneValidator phones,
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
        // Dedup de wa_message_id DENTRO da rodada: o GetByWaMessageIdAsync só enxerga o banco
        // commitado, não os Adds pendentes — sem isto, o mesmo id vindo 2x do WAHA numa rodada
        // viraria insert duplicado e violaria o índice único.
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chat in chats)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await SyncOneAsync(chat, messagesPerChat, seenMessageIds, ct);
                // Salva POR CHAT (em vez de um save único no fim): isola falhas — uma duplicata/conflito
                // não derruba o lote inteiro — e encolhe a janela da corrida com o webhook em tempo real.
                await uow.SaveChangesAsync(ct);
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
                // Descarta o pendente deste chat pra NÃO contaminar o save do próximo (ex.: conflito
                // raro de chave com o webhook). A próxima sync repesca o que faltou (auto-cura).
                uow.DiscardChanges();
                failures.Add($"{chat.Id}: {ex.Message}");
                log.LogWarning(ex, "Sync: failed for chat {ChatId}", chat.Id);
            }
#pragma warning restore CA1031
        }

        return new SyncResult(chatsTouched, messagesImported, contactsCreated, failures);
    }

    private async Task<ChatSyncResult> SyncOneAsync(
        WahaChat chat, int messagesPerChat, HashSet<string> seenMessageIds, CancellationToken ct)
    {
        // Pula a conta de sistema do WhatsApp (0@c.us / status@broadcast) — avisos como
        // "WhatsApp Business", não conversa de contato. Coerente com o webhook.
        if (WahaChatIdentifier.IsSystem(chat.Id))
        {
            return new ChatSyncResult(0, false);
        }

        var now = clock.UtcNow;
        var kind = WahaChatIdentifier.Classify(chat.Id);
        var isGroup = kind == WahaChatIdentifier.Kind.Group || chat.IsGroup;

        // WAHA devolve o próprio id do chat ("557185543603@c.us") como "nome" quando o número não está
        // salvo na agenda e não tem nome público. Não é nome — sanitiza pra null (senão o id vaza pra UI
        // como se fosse nome), pela fonte única WahaChatIdentifier (dona dos sufixos de JID).
        var cleanName = string.IsNullOrWhiteSpace(chat.Name) || WahaChatIdentifier.LooksLikeChatId(chat.Name)
            ? null
            : chat.Name;

        Guid? contactId = null;
        var contactCreated = false;
        if (kind == WahaChatIdentifier.Kind.Individual)
        {
            var e164 = WahaChatIdentifier.TryExtractPhoneE164(chat.Id);
            if (e164 is not null)
            {
                var phone = phones.NormalizeTrusted(e164);
                var contact = await contacts.GetByPhoneAsync(phone.E164, ct);
                if (contact is null)
                {
                    contact = Contact.Create(
                        id: Guid.NewGuid(),
                        phone: phone,
                        name: cleanName,
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
        var isNewConversation = conversation is null;
        var lastKnownAt = conversation?.LastMessageAt;
        if (conversation is null)
        {
            conversation = Conversation.Create(
                id: Guid.NewGuid(),
                waChatId: chat.Id,
                contactId: contactId,
                title: cleanName,
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
            if (!string.IsNullOrWhiteSpace(cleanName) && !string.Equals(conversation.Title, cleanName, StringComparison.Ordinal))
            {
                conversation.Rename(cleanName);
            }
        }

        var imported = 0;
        DateTimeOffset? latestAt = null;
        string? latestPreview = null;

        // OTIMIZAÇÃO: só puxa mensagens quando há atividade nova — chat novo, ou a prévia do
        // overview aponta uma mensagem mais recente que a última que já temos. Quando nada mudou,
        // o webhook já cobriu em tempo real; re-puxar N msgs de TODO chat a cada ciclo era o que
        // saturava a API (ERR_EMPTY_RESPONSE). TouchLastMessage abaixo cuida da prévia.
        var hasNewActivity = isNewConversation
            || (chat.LastMessageAt is { } overviewAt && (lastKnownAt is null || overviewAt > lastKnownAt));

        if (hasNewActivity)
        {
            var msgs = await waha.GetChatMessagesAsync(dispatchOpts.Value.SessionId, chat.Id, messagesPerChat, ct);
            foreach (var m in msgs)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(m.Id))
                {
                    continue;
                }
                // Pula mensagens sem texto. O sync não captura mídia, então corpo vazio aqui =
                // bolha inútil (mensagem de protocolo/sistema do WhatsApp, comum via @lid). Mantém
                // o chat limpo e coerente com o webhook, que também ignora eventos sem conteúdo.
                if (string.IsNullOrWhiteSpace(m.Body))
                {
                    continue;
                }

                if (latestAt is null || m.Timestamp > latestAt)
                {
                    latestAt = m.Timestamp;
                    latestPreview = m.Body;
                }

                // Já contabilizado NESTA rodada (WAHA repetiu o id) → pula antes de tentar inserir.
                if (!seenMessageIds.Add(m.Id))
                {
                    continue;
                }
                var existing = await messages.GetByWaMessageIdAsync(m.Id, ct);
                if (existing is not null)
                {
                    continue;
                }

                var direction = m.FromMe ? MessageDirection.Outbound : MessageDirection.Inbound;
                var authorPhone = WahaChatIdentifier.ResolveAuthorPhone(kind, chat.Id, direction, m.Author);

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

    private readonly record struct ChatSyncResult(int MessagesImported, bool ContactCreated);
}

public sealed record SyncResult(
    int ChatsTouched,
    int MessagesImported,
    int ContactsCreated,
    IReadOnlyList<string> Failures);
