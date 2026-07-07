using System.Net.Http;
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
    IPhoneOrchestrator phone,
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
    ISharedPhoneLedger ledger,
    DispatchSettleTracker settle,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<DispatchEngine> log)
{
    public async Task<DispatchCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var processed = 0;
        var sent = 0;
        var failed = 0;
        var skipped = 0;
        var retried = 0;
        var templateCache = new Dictionary<Guid, MtrxSys.Core.Domain.Messages.MessageTemplate>();
        var sessionId = dispatchOpts.Value.SessionId;

        // Reconcilia o número conectado ANTES de disparar: se o chip foi re-pareado com um número
        // diferente (troca de SIM, re-pareamento fora do fluxo connect/start), reinicia o aquecimento
        // pra o número NOVO nascer FRIO. Sem isto, o motor drenaria a fila herdando o platô do chip
        // antigo → ban. Best-effort e uma vez por ciclo (não por job).
        await TryReconcileWarmupPhoneAsync(sessionId, ct);

        // Modo de disparo (persistido): só salvamos o contato na agenda do EMULADOR no modo Emulator.
        // Em WahaOnly (aparelho físico) o emulador NÃO é o aparelho em uso — salvar lá seria ~5 docker
        // exec INÚTEIS por envio (falham/são no-op, com latência e spam de log). Lido 1x por ciclo.
        var saveContactsToEmulator =
            (await systemState.GetAsync(ct)).DispatchMode == PhoneDispatchMode.Emulator;

        while (!ct.IsCancellationRequested)
        {
            // Freio de mão: operador pausou os envios pelo botão "Parar envios". Leitura fresca
            // (sem cache de tracking) — senão o ciclo não enxergaria a pausa gravada no meio dele
            // e drenaria a fila inteira mesmo após o clique.
            if (await systemState.IsManuallyPausedAsync(ct))
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

            // Só agora (há job pra enviar) vale checar a sessão — evita bater no WAHA em ciclos
            // ociosos. Sessão fora? Para; o job fica Pending e é retomado quando a sessão voltar.
            // LISTA-BRANCA: só envia se WORKING (ver abaixo). Erro transitório de LEITURA de status é
            // engolido (assume Working) pra não travar por blip de infra — a falha do envio é o backstop.
            if (dispatchOpts.Value.PauseWhenSessionDown)
            {
                WahaSessionStatus sessionStatus;
                try
                {
                    sessionStatus = await waha.GetSessionStatusAsync(sessionId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch
                {
                    sessionStatus = WahaSessionStatus.Working; // status indisponível: não trava o envio
                }
#pragma warning restore CA1031
                // LISTA-BRANCA (anti-ban): só envia se a sessão está WORKING. QUALQUER outro estado
                // (SCAN_QR_CODE / Starting / Stopped / Failed / Unknown) PARA o ciclo — o job fica
                // Pending e retoma quando o chip voltar a WORKING. Antes era lista-negra (só Stopped/
                // Failed), que deixava passar SCAN_QR_CODE (re-pareando) e estados novos → tentava
                // enviar numa sessão degradada. (Leitura de status indisponível cai no default WORKING
                // acima, de propósito, pra não travar por blip de infra — a falha do envio é o backstop.)
                if (sessionStatus is not WahaSessionStatus.Working)
                {
                    settle.Reset(); // caiu: o próximo WORKING recomeça a contagem do reassentamento.
                    log.LogInformation("Sessão WAHA {Status} (não WORKING); ciclo parado (job segue Pending).", sessionStatus);
                    break;
                }
                // REASSENTAR APÓS RECONECTAR: se a sessão voltou a WORKING há pouco (ou o dispatcher
                // reiniciou), espera a janela antes de enviar — evita reconectar-e-metralhar (anti-ban).
                var settleWindow = TimeSpan.FromSeconds(Math.Max(0, dispatchOpts.Value.SettleAfterReconnectSeconds));
                if (settleWindow > TimeSpan.Zero && settle.IsSettling(clock.UtcNow, settleWindow))
                {
                    log.LogInformation("Chip reassentando após reconectar; ciclo aguarda (job segue Pending).");
                    break;
                }
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
            if (ledger.IsEnabled)
            {
                // Uma única consulta cobre dedup (Sent) e opt-out (OptedOut). Em Enforce o opt-out é
                // FAIL-CLOSED: registro inacessível (Unavailable) PAUSA o ciclo (o job fica Pending e
                // é retomado quando o registro voltar) em vez de arriscar mandar pra quem deu SAIR —
                // o dedup só é postergado, não furado. Em Observe só loga o que faria.
                var ledgerStatus = await ledger.GetStatusAsync(contact.Phone.E164, ct);
                if (ledger.IsEnforcing)
                {
                    if (ledgerStatus == SharedLedgerStatus.Unavailable)
                    {
                        log.LogWarning(
                            "Registro compartilhado indisponível; ciclo pausado (fail-closed p/ opt-out). "
                            + "Job {JobId} volta para Pending.", job.Id);
                        break;
                    }
                    if (ledgerStatus is SharedLedgerStatus.OptedOut or SharedLedgerStatus.Sent)
                    {
                        var reason = ledgerStatus == SharedLedgerStatus.OptedOut
                            ? "opt-out em outro ambiente"
                            : "já enviado em outro ambiente";
                        job.MarkSkipped(reason);
                        await uow.SaveChangesAsync(ct);
                        skipped++;
                        log.LogInformation(
                            "Job {JobId} pulado ({Reason}): {Phone} consta no registro compartilhado.",
                            job.Id, reason, contact.Phone.E164);
                        continue;
                    }
                }
                else if (ledgerStatus is SharedLedgerStatus.OptedOut or SharedLedgerStatus.Sent)
                {
                    log.LogInformation(
                        "[ledger observe] Job {JobId} ({Phone}) SERIA pulado (consta no registro compartilhado).",
                        job.Id, contact.Phone.E164);
                }
            }

            try
            {
                if (!templateCache.TryGetValue(job.TemplateId, out var template))
                {
                    template = await templates.GetByIdAsync(job.TemplateId, ct)
                        ?? throw new InvalidOperationException($"Template {job.TemplateId} not found");
                    templateCache[job.TemplateId] = template;
                }
                var text = composer.Compose(template, contact);

                // Confere se o número EXISTE no WhatsApp — CEDO, antes de gastar typing/delay. Disparar
                // pra número inexistente falha (erro 463) E é gatilho de ban — foi o que restringiu a
                // conta no teste. Inexistente → pula (não arrisca o chip). Checagem indisponível (null)
                // → segue (não perde contato por hiccup). O chatId devolvido é o CANÔNICO do WhatsApp
                // (resolve o 9º dígito BR) — usado no typing E no envio, pra bater no chat certo.
                var numberCheck = await TryCheckNumberAsync(sessionId, contact, ct);
                if (numberCheck is { Exists: false })
                {
                    job.MarkSkipped("número não existe no WhatsApp");
                    await uow.SaveChangesAsync(ct);
                    skipped++;
                    log.LogInformation("Job {JobId} pulado: {Phone} não existe no WhatsApp.", job.Id, contact.Phone.E164);
                    // Espaça os check-exists: pular vários RÁPIDO = validação em massa = sinal de bot
                    // (e rate-limit da consulta). Cooldown curto entre checks de pulados protege o chip.
                    await Task.Delay(delay.NextCheckCooldown(), ct);
                    continue;
                }
                var sendTarget = numberCheck?.ChatId ?? contact.Phone.E164;

                var delayBefore = delay.NextDelay();
                var typingMs = await typing.SimulateAsync(sessionId, sendTarget, text, ct);

                // 2º freio de mão: se o operador clicou "Parar envios" enquanto a gente simulava
                // o typing (2-5s), o envio ainda não saiu. Checa de novo aqui pra abortar ANTES
                // do irreversível. O job não é marcado nem como Sent nem como Failed — permanece
                // Pending e é pego de novo na próxima retomada. Erro de DB no check = não pausa
                // (preserva o trade-off "preferimos enviar a falhar por hiccup transitório").
                bool pausedMidIteration;
                try
                {
                    pausedMidIteration = await systemState.IsManuallyPausedAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch
                {
                    pausedMidIteration = false;
                }
#pragma warning restore CA1031

                if (pausedMidIteration)
                {
                    log.LogInformation(
                        "Pausa detectada após typing; abortando envio do job {JobId} (volta para Pending).",
                        job.Id);
                    break;
                }

                // Grava o contato na agenda do EMULADOR ANTES do envio (perfil menos-robô, ajuda
                // anti-ban) — SÓ no modo Emulator (no físico o emulador não é o aparelho em uso).
                // Best-effort e idempotente: uma falha aqui é só logada e NÃO impede o envio.
                if (saveContactsToEmulator)
                {
                    await TrySaveContactAsync(contact, ct);
                }

                // Anexo de imagem DESABILITADO: todo disparo sai como texto, mesmo que o template
                // tenha imagem. Evita rejeição do WAHA (422 por mimetype/dados) e mantém o envio
                // simples e estável. (O texto composto preserva spintax, placeholders e o "SAIR".)
                var waMessageId = await waha.SendTextAsync(sessionId, sendTarget, text, ct);

                var now = clock.UtcNow;
                job.MarkSent(waMessageId, now);
                contact.RegisterSend(now);
                await contacts.UpdateAsync(contact, ct);
                await audit.AddAsync(
                    SendAuditEntry.Create(
                        id: Guid.NewGuid(),
                        dispatchJobId: job.Id,
                        phoneE164: contact.Phone.E164,
                        renderedText: text,
                        typingMs: typingMs,
                        delayMs: (int)delayBefore.TotalMilliseconds,
                        occurredAt: now,
                        // Id "core" (mesma normalização do webhook) pro sensor de entrega casar o message.ack.
                        waMessageId: WahaChatIdentifier.ExtractMessageCore(waMessageId)),
                    ct);
                // Commita SÓ o registro do envio (job=Sent + contato + auditoria). A mensagem já
                // saiu no WhatsApp (irreversível), então este commit NÃO pode tocar system_state:
                // o reset do breaker escrevia a linha singleton (token xmin) aqui, e um conflito de
                // concorrência (pausa/bônus pela API, ou o webhook) revertia TAMBÉM o MarkSent —
                // o job voltava a Pending e a MESMA mensagem era reenviada (duplicata/risco de ban).
                await uow.SaveChangesAsync(ct);

                // Daqui pra baixo é PÓS-ENVIO e best-effort: o job já está Sent e commitado; nada
                // abaixo pode revertê-lo nem reenviar — falhas são só logadas.

                // Teto diário: conta só envio já COMMITADO (antes incrementava antes do commit, e um
                // rollback do commit deixava o contador divergente do que de fato persistiu).
                await IncrementWarmupSafeAsync(ct);

                // Reset do breaker FORA da transação do envio (ver acima). Um conflito aqui é inócuo:
                // logado e o breaker zera no próximo sucesso.
                await ResetBreakerSafeAsync(ct);

                // Livro-razão compartilhado (dedup cross-ambiente): fail-open, só após o commit pra
                // não registrar "enviado" globalmente algo que não persistiu localmente.
                await ledger.MarkSentAsync(contact.Phone.E164, ct);

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
                var now = clock.UtcNow;
                metrics.RecordSendFailure(ex.Message);

                // WAHA INALCANÇÁVEL — conexão recusada/DNS/reset, SEM resposta HTTP: a mensagem
                // garantidamente NÃO saiu. É a sessão/rede que caiu, não o número → não consome
                // tentativa, não marca Failed, não abre o breaker; o job fica PENDING e o ciclo PARA
                // (como o PauseWhenSessionDown), retomando quando o WAHA voltar. Sem isto, um blip que
                // derrubasse status+envio juntos marcava Failed (terminal) = mensagem PERDIDA.
                // TIMEOUT fica FORA daqui de propósito: um timeout pós-request PODE ter enviado, então
                // vai pro retry COM TETO abaixo — senão um WAHA lento-mas-vivo geraria reenvio ilimitado
                // (duplicata/risco de ban).
                if (IsSessionDownFailure(ex))
                {
                    log.LogWarning(ex,
                        "WAHA inalcançável ao enviar o job {JobId}; ciclo parado (job segue Pending).", job.Id);
                    break;
                }

                // SESSÃO SAIU DE WORKING no meio do envio (WAHA respondeu, mas a sessão deslogou/caiu
                // pra SCAN_QR_CODE): a falha NÃO é do número — é da sessão. PARA o ciclo (job segue
                // Pending) em vez de marcar Failed, consumir tentativa e SEGUIR enviando numa sessão
                // degradada (risco de ban). Só cai no tratamento de falha-do-número se a sessão SEGUE
                // WORKING. Se não der pra ler o status (blip), NÃO afirma que caiu — o gate de lista-
                // branca no topo do próximo ciclo é o backstop.
                if (await IsSessionNotWorkingAsync(sessionId, ct))
                {
                    log.LogWarning(ex,
                        "Envio do job {JobId} falhou e a sessão NÃO está WORKING; ciclo parado (job segue Pending).", job.Id);
                    break;
                }

                // Erro permanente (4xx do WAHA: número inválido etc.) não melhora com reenvio.
                // Falha transitória (timeout/5xx/conexão) reenvia ATÉ o teto de tentativas.
                if (!IsPermanentFailure(ex) && job.CanRetry(dispatchOpts.Value.MaxSendAttempts))
                {
                    // Volta pro FIM da fila (ScheduledAt = agora) pra um novo envio. NÃO conta pro
                    // circuit breaker: um contato que reenfileira não pode pausar o sistema todo.
                    job.ScheduleRetry(now, ex.Message);
                    await uow.SaveChangesAsync(ct);
                    retried++;
                    log.LogInformation(
                        "Envio do job {JobId} falhou ({Reason}); reenfileirado (tentativa {Attempt} de {Max}).",
                        job.Id, ex.Message, job.AttemptCount + 1, dispatchOpts.Value.MaxSendAttempts);
                    // Respiro curto SÓ no reenvio: o job volta com ScheduledAt = agora e seria
                    // re-dequeuado na hora se a fila estiver vazia. Evita martelar o WAHA em loop.
                    await Task.Delay(FailureCooldown, ct);
                }
                else
                {
                    // Definitivo: esgotou as tentativas ou é erro permanente. Aí sim conta pro
                    // breaker (chip genuinamente quebrado acaba pausando após falhas seguidas).
                    log.LogWarning(ex, "Dispatch failed for job {JobId}", job.Id);
                    job.MarkFailed(ex.Message, now);
                    await breaker.RecordFailureAsync(ex.Message, ct);
                    await uow.SaveChangesAsync(ct);
                    failed++;
                }
            }
#pragma warning restore CA1031
        }

        return new DispatchCycleResult(processed, sent, failed, skipped, retried);
    }

    // Reconcilia o número conectado com o WarmupPhone: número diferente → RestartWarmup (chip novo
    // nasce frio). Best-effort: leitura vazia/instável do WAHA é ignorada (não reseta à toa) e uma
    // falha não trava o ciclo — o gate de aquecimento segue com o estado atual.
    private async Task TryReconcileWarmupPhoneAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var phone = await waha.GetOwnPhoneE164Async(sessionId, ct);
            if (string.IsNullOrWhiteSpace(phone))
            {
                return;
            }
            var state = await systemState.GetAsync(ct);
            var changed = state.ReconcileWarmupPhone(phone, IClock.ToBrasiliaDate(clock.UtcNow));
            await systemState.UpdateAsync(state, ct); // persiste também o 1º registro do número
            await uow.SaveChangesAsync(ct);
            if (changed)
            {
                log.LogInformation(
                    "Chip trocado (número conectado {Phone}); aquecimento reiniciado — nasce frio.", phone);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // best-effort: reconciliar não pode derrubar o ciclo de disparo
        catch (Exception ex)
        {
            log.LogWarning(ex, "Não reconciliei o número do aquecimento; sigo com o estado atual.");
        }
#pragma warning restore CA1031
    }

    // Checa se o número existe no WhatsApp, best-effort: qualquer erro na checagem devolve null (= "não
    // deu pra checar") pra o disparo seguir — não descartamos um contato por causa de um hiccup da checagem.
    private async Task<WahaNumberCheck?> TryCheckNumberAsync(string sessionId, Contact contact, CancellationToken ct)
    {
        try
        {
            return await waha.CheckNumberExistsAsync(sessionId, contact.Phone.E164, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // best-effort: falha na checagem não pode travar nem pular o envio
        catch (Exception ex)
        {
            log.LogWarning(ex, "Checagem de número falhou para {Phone}; sigo com o envio.", contact.Phone.E164);
            return null;
        }
#pragma warning restore CA1031
    }

    // Grava o contato na agenda do emulador antes de enviar. NUNCA propaga: se o docker/adb falhar
    // (socket ausente, emulador fora, engine sem suporte), loga e segue — o contato salvo é perfil
    // anti-robô, não pré-requisito do envio. Idempotente no orquestrador (não duplica número já salvo).
    private async Task TrySaveContactAsync(Contact contact, CancellationToken ct)
    {
        try
        {
            await phone.SaveContactAsync(contact.Phone.E164, contact.Name, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // best-effort: salvar contato não pode derrubar o envio
        catch (Exception ex)
        {
            log.LogWarning(ex, "Não gravei o contato {Phone} na agenda; sigo com o envio.", contact.Phone.E164);
        }
#pragma warning restore CA1031
    }

    // Respiro após uma falha, pra não martelar o WAHA em loop quando a fila só tem o job que falha.
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);

    // Falha definitiva = a que não melhora reenviando: respostas 4xx do WAHA (request inválido,
    // número ruim), EXCETO 408 (timeout) e 429 (rate limit), que são transitórios. Timeout do
    // Polly, 5xx e erros de conexão caem no padrão "transitório" (reenvia).
    private static bool IsPermanentFailure(Exception ex)
    {
        if (ex is HttpRequestException http && http.StatusCode is { } code)
        {
            var n = (int)code;
            if (n is 408 or 429)
            {
                return false;
            }
            return n is >= 400 and < 500;
        }
        return false;
    }

    // WAHA inalcançável ANTES de qualquer resposta: conexão recusada / DNS / reset — nenhuma request
    // HTTP completou, então a mensagem NÃO saiu (seguro manter Pending sem consumir tentativa nem
    // marcar Failed). NÃO inclui timeout de propósito: um timeout pós-request pode ter enviado, então
    // ele segue o caminho de retry COM TETO (evita reenvio ilimitado/duplicata). Um 4xx/5xx tem
    // StatusCode preenchido (o WAHA respondeu) e também não cai aqui.
    private static bool IsSessionDownFailure(Exception ex)
        => ex is HttpRequestException { StatusCode: null };

    // Re-checa se a sessão SAIU de WORKING depois de uma falha de envio (deslogou/caiu pra SCAN_QR_CODE
    // no meio do ciclo). true = confirmadamente NÃO-WORKING → o chamador PARA o ciclo (não trata como
    // falha do número). Se o status não puder ser lido (blip), retorna false DE PROPÓSITO: não afirma
    // queda por uma leitura instável — o gate de lista-branca no topo do próximo ciclo é o backstop.
    private async Task<bool> IsSessionNotWorkingAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await waha.GetSessionStatusAsync(sessionId, ct) is not WahaSessionStatus.Working;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // leitura de status instável não pode derrubar o ciclo; backstop é o gate do topo
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    // Incrementa o teto de aquecimento após o commit do envio. Best-effort: o envio já ocorreu, então
    // uma falha aqui (rede/DB do contador) só é logada — nunca reverte nem reenvia. O IncrementAsync é
    // um UPSERT atômico próprio (fora do uow), então não há mudança pendente a descartar.
    private async Task IncrementWarmupSafeAsync(CancellationToken ct)
    {
        try
        {
            await warmup.IncrementAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // best-effort pós-envio: não pode derrubar o ciclo
        catch (Exception ex)
        {
            log.LogWarning(ex, "Envio OK mas não incrementei o teto de aquecimento; o contador pode subir 1 a menos.");
        }
#pragma warning restore CA1031
    }

    // Zera o breaker (consecutive failures) após um envio bem-sucedido, FORA da transação do envio.
    // Best-effort: um conflito de concorrência na linha singleton do system_state é descartado e
    // logado — o breaker zera no próximo sucesso, e o envio já commitado permanece intacto.
    private async Task ResetBreakerSafeAsync(CancellationToken ct)
    {
        try
        {
            await breaker.RecordSuccessAsync(ct);
            await uow.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // best-effort pós-envio: não pode derrubar o ciclo
        catch (Exception ex)
        {
            uow.DiscardChanges();
            log.LogWarning(ex, "Envio OK mas não resetei o circuit breaker; zera no próximo sucesso.");
        }
#pragma warning restore CA1031
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

public sealed record DispatchCycleResult(int Processed, int Sent, int Failed, int Skipped, int Retried);
