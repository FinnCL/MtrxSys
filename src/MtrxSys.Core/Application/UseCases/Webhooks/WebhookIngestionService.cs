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
    IWahaClient waha,
    IUnitOfWork uow,
    IClock clock,
    BrazilPhoneValidator phones,
    ISharedPhoneLedger ledger,
    ISendAuditRepository audit,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<WebhookIngestionService> log) : IWebhookIngestionService
{
    public async Task IngestAsync(WahaWebhookEvent evt, CancellationToken ct)
    {
        // Sensor de ENTREGA: o message.ack atualiza o estado das NOSSAS mensagens (detecta shadow-
        // restriction — sai mas não entrega). Roteado ANTES do filtro de inbound; não gera conversa.
        if (WahaEvents.IsAck(evt.Event))
        {
            await HandleAckAsync(evt, ct);
            return;
        }

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
        // Ignora eventos sem conteúdo real: corpo vazio E sem mídia. O WhatsApp emite mensagens
        // de protocolo/sistema (comuns via @lid) assim — elas NÃO são resposta do contato, então
        // não podem marcar "Respondeu" nem virar bolha vazia no chat. Texto ou mídia (resposta só
        // com imagem/áudio) passam normalmente.
        if (string.IsNullOrWhiteSpace(p.Body) && p.HasMedia != true && p.Media is null)
        {
            log.LogDebug("Webhook sem conteúdo (protocolo/sistema), ignorando. id={Id}", p.Id);
            return;
        }
        // Ignora a conta de sistema do WhatsApp (0@c.us) e status@broadcast: são avisos/
        // notificações (ex.: "WhatsApp Business"), não conversa de contato.
        if (WahaChatIdentifier.IsSystem(p.From))
        {
            log.LogDebug("Webhook de conta de sistema ({From}), ignorando", p.From);
            return;
        }
        // De-dupe pelo "core" do id (token final), que é igual no envio (@c.us) e no eco
        // (@lid). Assim o eco de uma mensagem que o disparo já gravou é reconhecido como
        // duplicado — antes os ids serializados divergiam e duplicava no chat.
        var coreId = WahaChatIdentifier.ExtractMessageCore(p.Id);
        var existing = await messages.GetByWaMessageIdAsync(coreId, ct);
        if (existing is not null)
        {
            log.LogDebug("Duplicate message {WaId} (core {Core}), skipping", p.Id, coreId);
            return;
        }

        var chatId = p.From!;
        var kind = WahaChatIdentifier.Classify(chatId);

        // Mensagem que NÓS enviamos num chat INDIVIDUAL (eco do disparo, ou resposta manual pelo
        // celular): o From é o nosso próprio número e, no eco, costuma chegar como @lid (oculto).
        // Se o resolvêssemos, cairíamos no nosso número e gravaríamos numa "conversa com o próprio
        // número". A conversa de saída é sempre o destinatário (To) — trocamos independente do
        // formato (antes só tratávamos @c.us). Sem To não dá pra atribuir, e o disparo já grava a
        // saída na conversa certa, então ignoramos o eco.
        // Em GRUPO não se aplica: lá o From já é o id do grupo (não o nosso número), então mantém.
        if (p.FromMe == true && kind != WahaChatIdentifier.Kind.Group)
        {
            if (string.IsNullOrEmpty(p.To))
            {
                log.LogDebug("Eco de saída sem destinatário (id={Id}); ignorando pra não gravar conversa com o próprio número", p.Id);
                return;
            }
            chatId = p.To;
            kind = WahaChatIdentifier.Classify(chatId);
        }

        var now = clock.UtcNow;
        // WAHA às vezes embute a imagem como base64 no próprio corpo (ex.: avisos com foto).
        // Não guardamos dezenas de KB de base64 no chat nem na prévia — vira "[imagem]".
        var bodyText = CleanBody(p.Body);
        Guid? contactId = null;
        var newlyOptedOut = false;
        // Resolve o telefone real do remetente: direto (@c.us) ou traduzindo o LID (@lid,
        // número oculto pelo WhatsApp) via WAHA. Sem isso, respostas que chegam por LID
        // (cada vez mais comuns) não casariam com o contato — e o opt-out não funcionaria.
        string? e164 = null;
        if (kind == WahaChatIdentifier.Kind.Individual)
        {
            e164 = WahaChatIdentifier.TryExtractPhoneE164(chatId);
        }
        else if (kind == WahaChatIdentifier.Kind.LinkedId)
        {
            e164 = await waha.ResolveLidToPhoneE164Async(dispatchOpts.Value.SessionId, chatId, ct);
        }
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
                newlyOptedOut = await ApplyInboundClassificationAsync(contact, bodyText, now, ct);
                // Opt-out global: propaga pro registro compartilhado (fail-open, idempotente).
                // É o lado seguro do erro — se algo falhar, no máximo a pessoa deixa de receber.
                if (newlyOptedOut)
                {
                    await ledger.MarkOptOutAsync(contact.Phone.E164, ct);
                }
            }
        }

        var conversation = await conversations.GetByWaChatIdAsync(chatId, ct);
        if (conversation is null && contactId is not null && kind != WahaChatIdentifier.Kind.Group)
        {
            // Reaproveita a conversa que o disparo (ou um inbound anterior) já criou pra este
            // contato, mesmo que sob outro identificador (@c.us vs @lid). Sem isso, o eco de um
            // disparo chegando por @lid criaria uma 2ª conversa do mesmo contato.
            conversation = await conversations.GetByContactIdAsync(contactId.Value, ct);
        }
        var conversationTitle = p.NotifyName; // título = nome público, quando houver
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
        var authorPhone = WahaChatIdentifier.ResolveAuthorPhone(kind, chatId, direction, p.Participant);

        var message = ChatMessage.Create(
            id: Guid.NewGuid(),
            conversationId: conversation.Id,
            waMessageId: coreId,
            direction: direction,
            authorPhone: authorPhone,
            body: bodyText,
            timestamp: timestamp,
            mediaUrl: p.Media?.Url);
        await messages.AddAsync(message, ct);

        conversation.TouchLastMessage(timestamp, bodyText);

        await uow.SaveChangesAsync(ct);

        // Confirmação de saída — enviada SÓ aqui (após o commit). Entregas duplicadas do
        // webhook falham no SaveChanges (waMessageId único) e não chegam aqui, então a
        // confirmação sai uma única vez. É uma resposta (baixo risco de ban).
        if (newlyOptedOut)
        {
#pragma warning disable CA1031
            try
            {
                await waha.SendTextAsync(dispatchOpts.Value.SessionId, chatId, OptOutConfirmationMessage, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Falha ao enviar confirmação de opt-out para {ChatId}", chatId);
            }
#pragma warning restore CA1031
        }
    }

    // Atualiza o estado de ENTREGA de uma mensagem NOSSA a partir de um message.ack. Só fromMe da nossa
    // sessão; casa pelo id core com a auditoria do disparo. Best-effort e idempotente (MarkAck é
    // monotônico): ack de mensagem que não é de disparo (resposta manual, ou já purgada) não acha e sai.
    private async Task HandleAckAsync(WahaWebhookEvent evt, CancellationToken ct)
    {
        // CADA saída daqui LOGA. Não é zelo: este método alimenta o guard de saúde de entrega, que é
        // a ÚNICA defesa contra shadow-restriction (o 463 em que o WhatsApp aceita e não entrega —
        // não dá erro, o breaker não vê). Se o ack some, o guard fica cego e ninguém percebe: a
        // coluna `ack` fica 0 e parece "nada foi entregue".
        //
        // Aconteceu de verdade em 2026-07-15: 17 envios, de vários chips e dias, TODOS com ack=0 —
        // enquanto o operador via as mensagens chegando no aparelho. Com as saídas mudas, não deu
        // pra saber se o evento não chegava, se o payload não casava ou se a auditoria não achava.
        // A investigação levou uma hora e não concluiu. Nunca mais em silêncio.
        if (!string.Equals(evt.Session, dispatchOpts.Value.SessionId, StringComparison.Ordinal))
        {
            log.LogDebug("ACK ignorado: sessão {Session} não é a do disparo ({Ours}).",
                evt.Session, dispatchOpts.Value.SessionId);
            return;
        }
        var p = evt.Payload;
        if (p?.Ack is null || p.FromMe != true || string.IsNullOrWhiteSpace(p.Id))
        {
            // Nível WARNING de propósito: um payload de ack que não bate com o que esperamos é
            // exatamente o cenário de sensor cego. Melhor barulho no log que teto de entrega falso.
            log.LogWarning(
                "ACK DESCARTADO (payload inesperado): ack={Ack} fromMe={FromMe} id={Id}. "
                + "O sensor de entrega depende deste evento — se isto repetir, o guard de "
                + "shadow-restriction está cego.",
                p?.Ack, p?.FromMe, p?.Id);
            return;
        }
        var coreId = WahaChatIdentifier.ExtractMessageCore(p.Id);

        // Status de entrega NO CHAT — pra TODA mensagem nossa (disparo OU envio manual). É o que torna
        // a FALHA (ack=-1: WhatsApp rejeitou o envio de companion restrito) visível na tela em vez de
        // a mensagem parecer enviada. A mensagem foi gravada com a chave CORE (ResolveOutboundCoreId),
        // então casa com o core do ack.
        var chatMsg = await messages.GetByWaMessageIdAsync(coreId, ct);
        chatMsg?.MarkAck(p.Ack.Value);

        // Auditoria do disparo (sensor de shadow-restriction). Só existe pra mensagem de DISPARO.
        var entry = await audit.GetByWaMessageIdAsync(coreId, ct);
        entry?.MarkAck(p.Ack.Value, clock.UtcNow);

        if (chatMsg is null && entry is null)
        {
            // Nem no chat nem na auditoria: eco de mensagem já purgada, ou id que não casou. Inofensivo
            // pontualmente; só vira sintoma (sensor cego) se acontecer com TODO envio.
            log.LogDebug("ACK {Ack} sem mensagem/auditoria correspondente (core {Core}).", p.Ack, coreId);
            return;
        }
        await uow.SaveChangesAsync(ct);
        log.LogInformation("ACK {Ack} ({AckName}) para {Core}", p.Ack, p.AckName, coreId);
    }

    private const string OptOutConfirmationMessage =
        "Pronto! Você não vai mais receber nossas mensagens. Obrigado.";

    // Move o contato no funil com base na resposta recebida:
    // - pediu pra sair ("SAIR" etc.) → opt-out + "Descartado" (Lost);
    // - respondeu qualquer outra coisa e ainda estava em "Novo" (Lead) → "Respondeu" (Qualified).
    // Não rebaixa quem já avançou (ex.: Cliente continua Cliente).
    // Retorna true se ESTE processamento acabou de marcar o opt-out (transição nova) —
    // usado pra enviar a confirmação uma única vez.
    private async Task<bool> ApplyInboundClassificationAsync(Contact contact, string? body, DateTimeOffset now, CancellationToken ct)
    {
        if (OptOutDetector.IsOptOut(body))
        {
            var newlyOptedOut = contact.OptOutAt is null;
            if (newlyOptedOut)
            {
                contact.OptOut(now);
                await contacts.UpdateAsync(contact, ct);
                log.LogInformation("Contato {ContactId} pediu opt-out via resposta", contact.Id);
            }
            await MoveStageAsync(contact, ContactStage.Lost, now, ct);
            return newlyOptedOut;
        }

        if (contact.Stage == ContactStage.Lead)
        {
            await MoveStageAsync(contact, ContactStage.Qualified, now, ct);
        }
        return false;
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


    // Troca corpo que é base64 de imagem (WAHA às vezes embute a foto no texto) por um rótulo,
    // pra não poluir o chat/prévia nem guardar dezenas de KB no campo de texto.
    private static string CleanBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }
        return body.Length > 512 && LooksLikeBase64Image(body) ? "[imagem]" : body;
    }

    private static bool LooksLikeBase64Image(string s) =>
        s.StartsWith("/9j/", StringComparison.Ordinal)              // JPEG
        || s.StartsWith("iVBORw0KGgo", StringComparison.Ordinal)    // PNG
        || s.StartsWith("R0lGOD", StringComparison.Ordinal)         // GIF
        || s.StartsWith("UklGR", StringComparison.Ordinal)          // WEBP
        || s.StartsWith("data:image", StringComparison.Ordinal);
}
