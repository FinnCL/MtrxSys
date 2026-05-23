using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
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
    IDispatchMetrics metrics,
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
            if (await breaker.IsOpenAsync(ct))
            {
                metrics.RecordCircuitOpen();
                log.LogInformation("Circuit breaker open; stopping cycle.");
                break;
            }

            if (!await warmup.CanSendAsync(ct))
            {
                metrics.RecordWarmupBlocked();
                log.LogInformation("Warmup daily limit reached ({Limit}); stopping cycle.", warmup.TodayLimit());
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
                await uow.SaveChangesAsync(ct);

                metrics.RecordSendSuccess((int)delayBefore.TotalMilliseconds, typingMs);
                sent++;
                log.LogInformation("Sent {JobId} to {Phone}", job.Id, contact.Phone.E164);

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
}

public sealed record DispatchCycleResult(int Processed, int Sent, int Failed, int Skipped);
