using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Messaging;
using MtrxSys.Core.Safety;

namespace MtrxSys.Dispatcher;

public sealed class DispatchEngine(
    IDispatchJobRepository jobs,
    IContactRepository contacts,
    IMessageTemplateRepository templates,
    IUnitOfWork uow,
    IWahaClient waha,
    IClock clock,
    MessageComposer composer,
    DelayPolicy delay,
    TypingSimulator typing,
    CircuitBreaker breaker,
    WarmupManager warmup,
    ISendAuditRepository audit,
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IDispatchMetrics metrics,
    ISystemStateRepository systemState,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<DispatchEngine> log)
{
    public async Task<DispatchCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var processed = 0;
        var sent = 0;
        var failed = 0;
        var skipped = 0;
        var templateCache = new Dictionary<Guid, MtrxSys.Core.Domain.Messages.MessageTemplate>();

        while (!ct.IsCancellationRequested)
        {
            // Freio de mão: operador pausou os envios pelo botão "Parar envios".
            if ((await systemState.GetAsync(ct)).IsManuallyPaused)
            {
                log.LogInformation("Envios pausados manualmente; ciclo parado.");
                break;
            }

            if (await breaker.IsOpenAsync(ct))
            {
                metrics.RecordCircuitOpen();
                log.LogInformation("Circuit breaker open; stopping cycle.");
                break;
            }

            if (!await warmup.CanSendAsync(ct))
            {
                var snap = await warmup.GetSnapshotAsync(ct);
                metrics.RecordWarmupBlocked();
                log.LogInformation(
                    "Warmup daily limit reached ({Sent}/{Limit}, day {Day}); stopping cycle.",
                    snap.SentToday, snap.TodayLimit, snap.DayIndex);
                break;
            }

            var job = await jobs.DequeueNextPendingAsync(clock.UtcNow, ct);
            if (job is null)
            {
                break;
            }

            processed++;
            var contact = await contacts.GetByIdAsync(job.ContactId, ct);
            if (contact is null)
            {
                job.MarkSkipped("contact not found");
                await uow.SaveChangesAsync(ct);
                skipped++;
                continue;
            }
            if (contact.OptOutAt is not null)
            {
                job.MarkSkipped("opted out");
                await uow.SaveChangesAsync(ct);
                skipped++;
                continue;
            }
            // Descartado depois de enfileirado: não envia. O soft delete só marca deleted_at
            // (não apaga jobs como o delete antigo), então um job Pending criado antes do
            // descarte chegaria aqui — sem esta guarda, mandaria pra quem foi descartado.
            if (contact.DeletedAt is not null)
            {
                job.MarkSkipped("descartado");
                await uow.SaveChangesAsync(ct);
                skipped++;
                continue;
            }

            var sessionId = dispatchOpts.Value.SessionId;
            try
            {
                if (!templateCache.TryGetValue(job.TemplateId, out var template))
                {
                    template = await templates.GetByIdAsync(job.TemplateId, ct)
                        ?? throw new InvalidOperationException($"Template {job.TemplateId} not found");
                    templateCache[job.TemplateId] = template;
                }
                var text = composer.Compose(template, contact);
                var delayBefore = delay.NextDelay();
                var typingMs = await typing.SimulateAsync(sessionId, contact.Phone.E164, text, ct);
                // Anexo de imagem DESABILITADO: todo disparo sai como texto, mesmo que o template
                // tenha imagem. Evita rejeição do WAHA (422 por mimetype/dados) e mantém o envio
                // simples e estável. (O texto composto preserva spintax, placeholders e o "SAIR".)
                var waMessageId = await waha.SendTextAsync(sessionId, contact.Phone.E164, text, ct);

                var now = clock.UtcNow;
                job.MarkSent(waMessageId, now);
                contact.RegisterSend(now);
                await contacts.UpdateAsync(contact, ct);
                await warmup.IncrementAsync(ct);
                await breaker.RecordSuccessAsync(ct);
                await audit.AddAsync(
                    SendAuditEntry.Create(
                        id: Guid.NewGuid(),
                        dispatchJobId: job.Id,
                        phoneE164: contact.Phone.E164,
                        renderedText: text,
                        typingMs: typingMs,
                        delayMs: (int)delayBefore.TotalMilliseconds,
                        occurredAt: now),
                    ct);
                // Commita o registro do envio PRIMEIRO: a mensagem já saiu no WhatsApp
                // (irreversível), então marcar enviado/auditoria/breaker não pode depender
                // de nada opcional que venha depois.
                await uow.SaveChangesAsync(ct);

                metrics.RecordSendSuccess((int)delayBefore.TotalMilliseconds, typingMs);
                sent++;
                log.LogInformation("Sent {JobId} to {Phone}", job.Id, contact.Phone.E164);

                // Grava a mensagem no chat do sistema (antes dependia só do "eco" instável do
                // webhook). É MELHOR-ESFORÇO: o envio já está garantido, então uma falha aqui
                // (ex.: concorrência na conversa, ou o eco gravou primeiro) só é logada — nunca
                // marca o job como falho nem abre o circuit breaker.
                await TryRecordOutboundMessageAsync(contact, text, waMessageId, now, ct);

                await Task.Delay(delayBefore, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "Dispatch failed for job {JobId}", job.Id);
                job.MarkFailed(ex.Message, clock.UtcNow);
                await breaker.RecordFailureAsync(ex.Message, ct);
                await uow.SaveChangesAsync(ct);
                metrics.RecordSendFailure(ex.Message);
                failed++;
            }
#pragma warning restore CA1031
        }

        return new DispatchCycleResult(processed, sent, failed, skipped);
    }

    // Envolve a gravação no chat: nunca propaga exceção. Se falhar (concorrência na conversa,
    // erro de DB, ou o eco do webhook gravou primeiro), descarta as alterações pendentes — pra
    // não contaminar o DbContext compartilhado do ciclo — e segue. O envio já está commitado.
    private async Task TryRecordOutboundMessageAsync(
        Contact contact, string text, string waMessageId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await RecordOutboundMessageAsync(contact, text, waMessageId, now, ct);
            await uow.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // melhor-esforço: o envio já ocorreu; não pode derrubar o ciclo
        catch (Exception ex)
        {
            uow.DiscardChanges();
            log.LogWarning(ex, "Envio OK mas não registrei a mensagem no chat (contato {ContactId}); "
                + "o eco do webhook ainda pode cobrir.", contact.Id);
        }
#pragma warning restore CA1031
    }

    // Persiste a mensagem enviada na conversa do contato. Resolve a conversa pelo ContactId
    // (não pelo chatId), assim o disparo cai na MESMA conversa das respostas — que podem
    // chegar por @lid — em vez de criar uma conversa @c.us paralela. Cria a conversa se ainda
    // não existir. O de-dupe por "core" do id evita duplicar quando o eco do WAHA chegar.
    private async Task RecordOutboundMessageAsync(
        Contact contact, string text, string waMessageId, DateTimeOffset now, CancellationToken ct)
    {
        // Mesmo "core" de id usado pelo webhook (token final), pra de-dupe determinístico.
        var coreId = WahaChatIdentifier.ExtractMessageCore(waMessageId);
        if (string.IsNullOrEmpty(coreId))
        {
            coreId = $"dispatch_{Guid.NewGuid():N}"; // WAHA não devolveu id; gera um estável.
        }

        // O eco do webhook venceu a corrida e já gravou esta mensagem? Então não duplica.
        if (await messages.GetByWaMessageIdAsync(coreId, ct) is not null)
        {
            return;
        }

        var conversation = await conversations.GetByContactIdAsync(contact.Id, ct);
        if (conversation is null)
        {
            var chatId = WahaChatIdentifier.ExtractDigits(contact.Phone.E164) + WahaChatIdentifier.IndividualSuffix;
            conversation = Conversation.Create(
                id: Guid.NewGuid(),
                waChatId: chatId,
                contactId: contact.Id,
                title: string.IsNullOrWhiteSpace(contact.Name) ? contact.Phone.E164 : contact.Name,
                isGroup: false,
                createdAt: now);
            await conversations.AddAsync(conversation, ct);
        }

        await messages.AddAsync(
            ChatMessage.Create(
                id: Guid.NewGuid(),
                conversationId: conversation.Id,
                waMessageId: coreId,
                direction: MessageDirection.Outbound,
                authorPhone: null,
                body: text,
                timestamp: now),
            ct);
        conversation.TouchLastMessage(now, text);
    }
}

public sealed record DispatchCycleResult(int Processed, int Sent, int Failed, int Skipped);
