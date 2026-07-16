using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Conversations;

/// <summary>
/// Inicia uma conversa NOVA a partir do sistema: manda a 1ª mensagem pelo WAHA e materializa
/// Contact + Conversation + ChatMessage (Outbound). É o caminho de aquecimento manual "pelo Chat" —
/// diferente do endpoint de resposta (que exige conversa já existente) e do disparo (automático).
///
/// Como é um 1º contato, ele traz as travas que o envio-resposta não precisa: número existe no
/// WhatsApp (anti-463), sessão WORKING, e opt-out. A mensagem outbound entra em chat_messages, então
/// o HumanPhaseGate a CONTA (dias de outbound / conversa com resposta) — que é o ponto do aquecimento
/// ficar visível pro sistema. NÃO passa por teto de aquecimento nem gate (é manual, baixo volume),
/// coerente com o endpoint de resposta e o HumanPhaseAutoSender.
/// </summary>
public sealed class StartConversationUseCase(
    IWahaClient waha,
    IContactRepository contacts,
    IConversationRepository conversations,
    IChatMessageRepository messages,
    ISharedPhoneLedger ledger,
    SessionReadinessTracker readiness,
    IOptions<DispatchOptions> dispatchOpts,
    IClock clock,
    IUnitOfWork uow,
    ILogger<StartConversationUseCase> log)
{
    public async Task<StartConversationResult> RunAsync(
        string? phone, Guid? contactId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return StartConversationResult.Fail(StartConversationOutcome.EmptyText, "Escreva a mensagem.");
        }

        // Resolve o número DIGITADO (ou o do contato, se veio por id). NormalizeTypedE164 (e não
        // Validate) preserva número legado BR sem o 9º dígito, que é o caso normal — Validate rejeitaria.
        Contact? contact = null;
        string typedE164;
        if (contactId is { } cid)
        {
            contact = await contacts.GetByIdAsync(cid, ct);
            if (contact is null)
            {
                return StartConversationResult.Fail(
                    StartConversationOutcome.InvalidPhone, "Contato não encontrado.");
            }
            typedE164 = contact.Phone.E164;
        }
        else
        {
            var normalized = BrazilPhoneValidator.NormalizeTypedE164(phone);
            if (normalized is null)
            {
                return StartConversationResult.Fail(
                    StartConversationOutcome.InvalidPhone, "Número inválido. Use algo como +55 71 99999-8888.");
            }
            typedE164 = normalized;
        }

        var sessionId = dispatchOpts.Value.SessionId;

        // Sessão degradada é o gatilho nº1 de ban — só envia com WORKING.
        var status = await waha.GetSessionStatusAsync(sessionId, ct);
        if (status is not WahaSessionStatus.Working)
        {
            return StartConversationResult.Fail(
                StartConversationOutcome.SessionNotWorking,
                $"WhatsApp não está conectado (status={status}); tente quando reconectar.");
        }

        // Reassentamento: não envia nos primeiros minutos após a sessão (re)conectar. Enviar logo após
        // parear um companion recém-linkado é o que fez o WhatsApp REMOVER o device + aplicar reachout
        // timelock (prod 2026-07-16: "oi" 22s após parear → 7 dias de restrição). Mesma janela do
        // disparo (SettleAfterReconnectSeconds). workingFor null = a saúde ainda não confirmou WORKING
        // contínuo (ex.: logo após restart) → trata como não-assentado (fail-safe).
        var settleWindow = TimeSpan.FromSeconds(Math.Max(0, dispatchOpts.Value.SettleAfterReconnectSeconds));
        if (settleWindow > TimeSpan.Zero)
        {
            var workingFor = readiness.WorkingFor(clock.UtcNow);
            if (workingFor is null || workingFor < settleWindow)
            {
                return StartConversationResult.Fail(
                    StartConversationOutcome.SessionNotSettled,
                    $"A sessão reconectou há pouco — espere ~{Math.Ceiling(settleWindow.TotalMinutes):0} min "
                    + "antes de enviar (evita restrição de companion recém-linkado).");
            }
        }

        // Número precisa existir no WhatsApp (anti-463). FAIL-CLOSED no indisponível: num 1º contato de
        // chip frio não arriscamos enviar às cegas (o disparo, que roda em lote, apenas adia).
        var numberCheck = await TryCheckNumberAsync(sessionId, typedE164, ct);
        if (numberCheck is null)
        {
            return StartConversationResult.Fail(
                StartConversationOutcome.NumberCheckUnavailable,
                "Não deu pra verificar o número no WhatsApp agora. Tente de novo.");
        }
        if (!numberCheck.Exists)
        {
            return StartConversationResult.Fail(
                StartConversationOutcome.NumberNotOnWhatsApp, "Este número não tem WhatsApp.");
        }

        // FORMA CANÔNICA. O check-exists devolve o chatId REAL (resolve o 9º dígito do legado), e é
        // essa forma que os webhooks de RESPOSTA vão trazer. Então é ela que casa Contact e Conversation
        // — keyar pelo número digitado (que diverge no legado) fragmentaria a conversa (inbound numa,
        // outbound noutra) e o HumanPhaseGate NUNCA qualificaria a pessoa. O `sendTarget` já era o
        // chatId; agora o CONTATO e o waChatId também nascem dele.
        var sendTarget = numberCheck.ChatId ?? typedE164;
        var e164 = (numberCheck.ChatId is { } canon
            ? WahaChatIdentifier.TryExtractPhoneE164(canon)
            : null) ?? typedE164;

        // Resolve o contato pela forma canônica (casa com o que a resposta vai trazer, e com o contato
        // que o disparo/import criou pela forma do WAHA). Se veio por id, já está em mãos.
        contact ??= await contacts.GetByPhoneAsync(e164, ct);

        // Opt-out local é sagrado — inclusive no 1º contato manual.
        if (contact?.OptOutAt is not null)
        {
            return StartConversationResult.Fail(
                StartConversationOutcome.OptedOut, "Este contato pediu para SAIR — envio bloqueado.");
        }

        // Opt-out CROSS-AMBIENTE: quem deu SAIR em qualquer chip fica fora (o registro compartilhado
        // vale pra todos). Só em Enforce; fail-closed no indisponível, igual ao disparo. Só OptedOut
        // bloqueia — "já enviado" (Sent) é dedup de campanha, não impede conversa manual com conhecido.
        if (ledger.IsEnforcing)
        {
            var ledgerStatus = await ledger.GetStatusAsync(e164, ct);
            if (ledgerStatus == SharedLedgerStatus.OptedOut)
            {
                return StartConversationResult.Fail(
                    StartConversationOutcome.OptedOut,
                    "Este contato pediu para SAIR (em outro chip) — envio bloqueado.");
            }
            if (ledgerStatus == SharedLedgerStatus.Unavailable)
            {
                return StartConversationResult.Fail(
                    StartConversationOutcome.NumberCheckUnavailable,
                    "Não deu pra confirmar o opt-out compartilhado agora. Tente de novo.");
            }
        }

        var waMessageId = await waha.SendTextAsync(sessionId, sendTarget, text, ct);
        if (string.IsNullOrEmpty(waMessageId))
        {
            return StartConversationResult.Fail(
                StartConversationOutcome.SendFailed, "O WhatsApp não confirmou o envio. Tente de novo.");
        }

        var now = clock.UtcNow;

        // Cria o contato se ainda não existe (nasce Lead, opt-in agora), na forma canônica.
        if (contact is null)
        {
            contact = Contact.Create(
                id: Guid.NewGuid(),
                phone: PhoneNumber.FromValidatedE164(e164),
                name: null, groupTag: null, theme: null, optInAt: now);
            await contacts.AddAsync(contact, ct);
        }

        // Cai na MESMA conversa das respostas (por contato) pra não duplicar @c.us vs @lid — igual ao
        // disparo. Se não há, cria com o chatId CANÔNICO (o mesmo que o webhook de resposta vai trazer).
        var conversation = await conversations.GetByContactIdAsync(contact.Id, ct);
        if (conversation is null)
        {
            var waChatId = numberCheck.ChatId
                ?? (WahaChatIdentifier.ExtractDigits(e164) + WahaChatIdentifier.IndividualSuffix);
            // Pode já existir uma conversa ÓRFÃ (inbound que chegou antes do contato existir) sob esse
            // mesmo chatId — e há índice ÚNICO em wa_chat_id, então inserir estouraria (500, com a
            // mensagem já enviada). Reaproveita e vincula, como o webhook faz (GetByWaChatId primeiro).
            conversation = await conversations.GetByWaChatIdAsync(waChatId, ct);
            if (conversation is null)
            {
                conversation = Conversation.Create(
                    id: Guid.NewGuid(),
                    waChatId: waChatId,
                    contactId: contact.Id,
                    title: string.IsNullOrWhiteSpace(contact.Name) ? e164 : contact.Name,
                    isGroup: false,
                    createdAt: now);
                await conversations.AddAsync(conversation, ct);
            }
            else if (conversation.ContactId is null)
            {
                conversation.LinkContact(contact.Id);
            }
        }

        // Grava a mensagem outbound. Dedup pela chave CORE (a mesma que o webhook usa pro eco fromMe) —
        // igual ao disparo e ao aquecimento, via a convenção única em ResolveOutboundCoreId.
        var coreId = WahaChatIdentifier.ResolveOutboundCoreId(waMessageId, "chat");
        var existing = await messages.GetByWaMessageIdAsync(coreId, ct);
        var message = existing;
        if (message is null)
        {
            message = ChatMessage.Create(
                id: Guid.NewGuid(),
                conversationId: conversation.Id,
                waMessageId: coreId,
                direction: MessageDirection.Outbound,
                authorPhone: null,
                body: text,
                timestamp: now);
            await messages.AddAsync(message, ct);
            conversation.TouchLastMessage(now, text);
            await conversations.UpdateAsync(conversation, ct);
        }
        await uow.SaveChangesAsync(ct);

        return StartConversationResult.Ok(conversation, message);
    }

    // Melhor-esforço: exceção na checagem vira null (= indisponível), tratado como fail-closed acima.
    private async Task<WahaNumberCheck?> TryCheckNumberAsync(string sessionId, string e164, CancellationToken ct)
    {
        try
        {
            return await waha.CheckNumberExistsAsync(sessionId, e164, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao verificar número no WhatsApp ({Phone}); tratando como indisponível.", e164);
            return null;
        }
#pragma warning restore CA1031
    }
}

public enum StartConversationOutcome
{
    Ok,
    EmptyText,
    InvalidPhone,
    OptedOut,
    SessionNotWorking,
    SessionNotSettled,
    NumberNotOnWhatsApp,
    NumberCheckUnavailable,
    SendFailed,
}

public sealed record StartConversationResult(
    StartConversationOutcome Outcome, string? Error, Conversation? Conversation, ChatMessage? Message)
{
    public bool Success => Outcome == StartConversationOutcome.Ok;

    public static StartConversationResult Ok(Conversation conversation, ChatMessage message)
        => new(StartConversationOutcome.Ok, null, conversation, message);

    public static StartConversationResult Fail(StartConversationOutcome outcome, string error)
        => new(outcome, error, null, null);
}
