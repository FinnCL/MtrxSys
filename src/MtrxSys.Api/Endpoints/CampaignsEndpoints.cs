using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;

namespace MtrxSys.Api.Endpoints;

public static class CampaignsEndpoints
{
    public static IEndpointRouteBuilder MapCampaignsEndpoints(this IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/templates");

        templates.MapGet("/", async (IMessageTemplateRepository repo, CancellationToken ct) =>
        {
            var all = await repo.ListAllAsync(ct);
            return Results.Ok(all.Select(ToTemplateDto));
        });

        templates.MapPost("/", async (
            CreateTemplateRequest req,
            IMessageTemplateRepository repo,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ContentSpintax))
            {
                return Results.Problem("contentSpintax is required", statusCode: 400);
            }
            if (!Enum.TryParse<MessageSlot>(req.Slot, ignoreCase: true, out var slot))
            {
                slot = MessageSlot.Greeting;
            }

            byte[]? imageData = null;
            string? imageMimeType = null;
            if (!string.IsNullOrWhiteSpace(req.ImageBase64))
            {
                if (!AllowedImageMimeTypes.Contains(req.ImageMimeType ?? string.Empty))
                {
                    return Results.Problem("imagem deve ser PNG, JPEG ou WebP", statusCode: 400);
                }
                try
                {
                    imageData = Convert.FromBase64String(req.ImageBase64);
                }
                catch (FormatException)
                {
                    return Results.Problem("imageBase64 inválido", statusCode: 400);
                }
                if (imageData.Length == 0)
                {
                    return Results.Problem("imagem vazia", statusCode: 400);
                }
                if (imageData.Length > MaxImageBytes)
                {
                    return Results.Problem($"imagem excede o limite de {MaxImageBytes / (1024 * 1024)} MB", statusCode: 400);
                }
                imageMimeType = req.ImageMimeType;
            }

            var template = MessageTemplate.Create(
                Guid.NewGuid(), slot, req.ContentSpintax, active: true,
                imageData: imageData, imageMimeType: imageMimeType);
            await repo.AddAsync(template, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Created($"/api/templates/{template.Id}", ToTemplateDto(template));
        });

        templates.MapGet("/{id:guid}/image", async (
            Guid id,
            IMessageTemplateRepository repo,
            CancellationToken ct) =>
        {
            var t = await repo.GetByIdAsync(id, ct);
            if (t is null || !t.HasImage)
            {
                return Results.NotFound();
            }
            return Results.File(t.ImageData!, t.ImageMimeType ?? "application/octet-stream");
        });

        templates.MapDelete("/{id:guid}", async (
            Guid id,
            IMessageTemplateRepository repo,
            IDispatchJobRepository jobs,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var t = await repo.GetByIdAsync(id, ct);
            if (t is null)
            {
                return Results.NotFound();
            }
            t.Deactivate();
            // Junto com o soft delete, sumir com qualquer envio dele que ainda está na fila.
            // Sem isso, o dispatcher continuaria mandando a mensagem "deletada" — o GetByIdAsync
            // retorna template inativo igual, e o job já tem o template_id grudado de quando foi
            // enfileirado. Jobs Sent ficam intactos (histórico de auditoria).
            await jobs.ClearPendingByTemplateAsync(id, ct);
            await uow.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        var dispatch = app.MapGroup("/api/dispatch");

        dispatch.MapPost("/", async (
            DispatchRequest req,
            IContactRepository contacts,
            IDispatchJobRepository jobs,
            IMessageTemplateRepository templates,
            IRandomSource rng,
            IUnitOfWork uow,
            IClock clock,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatchOpts,
            ISystemStateRepository state,
            ISharedPhoneLedger ledger,
            ILoggerFactory logFactory,
            CancellationToken ct) =>
        {
            // Backstop anti-ban: reconcilia o aquecimento com o chip conectado ANTES de
            // enfileirar. Fecha a brecha do reconcile-na-conexão (que pode ler o número antes
            // do WAHA populá-lo). Melhor-esforço: um erro aqui não pode travar o disparo.
            try
            {
                await ReconcileWarmupPhoneAsync(waha, dispatchOpts.Value.SessionId, state, clock, uow, ct);
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                logFactory.CreateLogger("DispatchPrepare")
                    .LogWarning(ex, "Falha ao reconciliar o número do aquecimento antes do disparo; seguindo.");
            }
#pragma warning restore CA1031

            // Aceita uma lista de templates (rodízio) ou um único (compatível com chamadas antigas).
            var requestedIds = req.TemplateIds is { Length: > 0 } many
                ? many
                : req.TemplateId != Guid.Empty ? [req.TemplateId] : Array.Empty<Guid>();
            if (requestedIds.Length == 0)
            {
                return Results.Problem("informe ao menos um template", statusCode: 400);
            }

            var pool = new List<MessageTemplate>();
            foreach (var id in requestedIds.Distinct())
            {
                var t = await templates.GetByIdAsync(id, ct);
                if (t is null)
                {
                    return Results.NotFound(new { error = $"template {id} não encontrado" });
                }
                pool.Add(t);
            }

            ContactStage? stage = null;
            if (!string.IsNullOrWhiteSpace(req.Filter?.Stage))
            {
                if (!Enum.TryParse<ContactStage>(req.Filter.Stage, ignoreCase: true, out var parsed))
                {
                    return Results.Problem($"unknown stage '{req.Filter.Stage}'", statusCode: 400);
                }
                stage = parsed;
            }
            // Nunca dispara pro próprio número conectado (evita auto-envio). Guarda o agregado
            // pra reusar na pausa lá embaixo (sem segundo hit no banco).
            var sysState = await state.GetAsync(ct);
            var ownPhone = sysState.WarmupPhone;
            var filter = new ContactFilter(
                Stage: stage,
                TagName: req.Filter?.TagName,
                GroupTag: req.Filter?.GroupTag,
                ExcludeOptedOut: true,
                EngagedOnly: req.Filter?.EngagedOnly ?? false,
                ExcludePhoneE164: ownPhone,
                ExcludeAlreadyDispatched: true); // não re-enfileira quem já recebeu/está na fila
            var targets = await contacts.ListByFilterAsync(filter, ct);
            // Dedup entre ambientes: em Enforce, tira do público quem já consta no registro
            // compartilhado (já enviado/opt-out em outro chip) — evita enfileirar jobs que o motor
            // pularia e mantém a contagem coerente. Em Observe/Off não altera o público.
            if (ledger.IsEnforcing && targets.Count > 0)
            {
                var suppressed = await ledger.GetSuppressedAsync(targets.Select(c => c.Phone.E164).ToArray(), ct);
                if (suppressed.Count > 0)
                {
                    targets = targets.Where(c => !suppressed.Contains(c.Phone.E164)).ToList();
                }
            }
            var now = clock.UtcNow;
            foreach (var c in targets)
            {
                // Rodízio: cada contato recebe uma mensagem sorteada do pote.
                var tpl = pool[rng.NextInt(0, pool.Count)];
                var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, tpl.Id, now);
                await jobs.AddAsync(job, ct);
            }
            // Prepara a fila JÁ PAUSADA, no servidor e atômico com os jobs: os jobs nascem
            // Pending, mas o motor não envia enquanto IsManuallyPaused. Garante que NADA sai sem
            // o operador clicar "Iniciar envios" (resume) — sem depender de o front pausar antes.
            if (targets.Count > 0)
            {
                sysState.Pause(SystemStateAggregate.ManualPauseReason);
                await state.UpdateAsync(sysState, ct);
            }
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { scheduled = targets.Count, templatesUsed = pool.Count });
        });

        dispatch.MapGet("/stats", async (IDispatchJobRepository repo, CancellationToken ct) =>
        {
            var stats = await repo.GetStatsAsync(ct);
            return Results.Ok(stats);
        });

        dispatch.MapGet("/status", async (ISystemStateRepository state, IClock clock, CancellationToken ct) =>
        {
            var s = await state.GetAsync(ct);
            // Expõe também o disjuntor: ele pausa os envios sozinho após 3 falhas seguidas e
            // retoma quando OpenUntil passa. Sem isso a UI mostrava "Enviando" travado sem aviso.
            var circuitOpen = s.Circuit.IsOpenAt(clock.UtcNow);
            return Results.Ok(new
            {
                paused = s.IsManuallyPaused,
                circuitOpen,
                circuitOpenUntil = circuitOpen ? s.Circuit.OpenUntil : null,
            });
        });

        dispatch.MapPost("/pause", async (ISystemStateRepository state, IUnitOfWork uow, CancellationToken ct) =>
        {
            var s = await state.GetAsync(ct);
            s.Pause(SystemStateAggregate.ManualPauseReason);
            await state.UpdateAsync(s, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { paused = true });
        });

        dispatch.MapPost("/resume", async (ISystemStateRepository state, IUnitOfWork uow, CancellationToken ct) =>
        {
            var s = await state.GetAsync(ct);
            s.Resume();
            s.UpdateCircuit(CircuitBreakerState.Closed); // limpa também eventual pausa do circuit breaker
            await state.UpdateAsync(s, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { paused = false });
        });

        dispatch.MapPost("/clear", async (IDispatchJobRepository repo, CancellationToken ct) =>
        {
            var cleared = await repo.ClearPendingAsync(ct);
            return Results.Ok(new { cleared });
        });

        dispatch.MapPost("/reset", async (IDispatchJobRepository repo, IContactRepository contacts, CancellationToken ct) =>
        {
            // Renovar = recomeçar a campanha: zera os jobs E o marcador de envio dos contatos,
            // pra quem só tinha recebido voltar a "Novo" (consistente com ser re-disparável).
            var cleared = await repo.ClearAllAsync(ct);
            await contacts.ClearLastSentAsync(ct);
            return Results.Ok(new { cleared });
        });

        // Carga inicial do registro compartilhado: empurra os telefones já ENVIADOS / em OPT-OUT
        // DESTE ambiente pro registro, pra o histórico já contar na dedup cross-chip. Idempotente
        // (ON CONFLICT) e fail-open. No-op quando o recurso está desligado. Rode uma vez por chip.
        dispatch.MapPost("/ledger-backfill", async (
            IContactRepository contacts,
            ISharedPhoneLedger ledger,
            CancellationToken ct) =>
        {
            if (!ledger.IsEnabled)
            {
                return Results.Ok(new { enabled = false, sent = 0, optedOut = 0 });
            }
            var all = await contacts.ListByFilterAsync(new ContactFilter(ExcludeOptedOut: false), ct);
            var sent = 0;
            var optedOut = 0;
            foreach (var c in all)
            {
                ct.ThrowIfCancellationRequested();
                if (c.OptOutAt is not null)
                {
                    await ledger.MarkOptOutAsync(c.Phone.E164, ct);
                    optedOut++;
                }
                else if (c.LastSentAt is not null)
                {
                    await ledger.MarkSentAsync(c.Phone.E164, ct);
                    sent++;
                }
            }
            return Results.Ok(new { enabled = true, sent, optedOut });
        });

        // Reconcilia opt-outs que o webhook não pegou (ex.: "Sair" que chegou com o chip fora e
        // só entrou pelo sync, que não classifica). Marca opt-out + Lost dos contatos ativos cujo
        // inbound bate o OptOutDetector. Idempotente — rodar de novo não muda quem já é opt-out.
        dispatch.MapPost("/reconcile-optout", async (OptOutReconciler reconciler, CancellationToken ct) =>
        {
            var result = await reconciler.ReconcileAsync(ct);
            return Results.Ok(new { count = result.Count, phones = result.Phones });
        });

        dispatch.MapGet("/warmup", async (
            WarmupManager warmup, ISystemStateRepository state, CancellationToken ct) =>
        {
            var s = await warmup.GetSnapshotAsync(ct);
            var phone = (await state.GetAsync(ct)).WarmupPhone;
            return Results.Ok(new
            {
                phone,
                startedOn = s.StartedOn,
                // dayIndex é base-0 no domínio; expõe base-1 pra UI ("dia 1 de 7").
                day = s.DayIndex + 1,
                totalDays = s.Curve.Length,
                todayLimit = s.TodayLimit,       // teto da curva
                bonusToday = s.BonusToday,       // extra liberado manualmente hoje
                effectiveLimit = s.EffectiveLimit, // o que realmente vale agora
                unlimitedToday = s.UnlimitedToday,
                atCap = s.AtCap,
                sentToday = s.SentToday,
                remaining = s.Remaining,
                nextLimit = s.NextLimit,
                plateauLimit = s.PlateauLimit,
                curve = s.Curve,
            });
        });

        // Reinicia o aquecimento a partir de hoje (curva volta ao dia 0). Pra chip novo.
        dispatch.MapPost("/warmup/restart", async (
            ISystemStateRepository state, IClock clock, IUnitOfWork uow, CancellationToken ct) =>
        {
            var s = await state.GetAsync(ct);
            s.RestartWarmup(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
            await state.UpdateAsync(s, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { startedOn = s.WarmupStartedOn });
        });

        // Reconcilia o aquecimento com o número conectado no WhatsApp. Se o número mudou
        // (chip novo escaneado pelo QR), reinicia o aquecimento sozinho. Chamado quando a
        // sessão fica "Working". Devolve { changed, phone } pra UI avisar.
        dispatch.MapPost("/warmup/reconcile", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatchOpts,
            ISystemStateRepository state,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var (changed, phone) = await ReconcileWarmupPhoneAsync(
                waha, dispatchOpts.Value.SessionId, state, clock, uow, ct);
            return Results.Ok(new { changed, phone });
        });

        // Libera envios acima do teto do aquecimento SÓ PRA HOJE (decisão do operador no
        // modal). { all: true } solta o teto inteiro; senão { extra: N } adiciona N. Expira
        // à meia-noite. O dispatcher retoma a fila sozinho no próximo ciclo.
        dispatch.MapPost("/warmup/release", async (
            ReleaseWarmupRequest req,
            ISystemStateRepository state, IClock clock, IUnitOfWork uow, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            var s = await state.GetAsync(ct);
            if (req.All)
            {
                s.ReleaseWarmupAll(today);
            }
            else
            {
                if (req.Extra is not > 0)
                {
                    return Results.Problem("informe 'extra' > 0 ou 'all': true", statusCode: 400);
                }
                s.ReleaseWarmupBonus(today, req.Extra.Value);
            }
            await state.UpdateAsync(s, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { bonusToday = s.WarmupBonusToday, unlimited = s.WarmupBonusToday >= SystemStateAggregate.UnlimitedBonus });
        });

        dispatch.MapGet("/audience-count", async (
            bool? engagedOnly,
            string? groupTag,
            IContactRepository contacts,
            ISystemStateRepository state,
            ISharedPhoneLedger ledger,
            CancellationToken ct) =>
        {
            // Mesma exclusão do disparo real (próprio número + já enviados), pra a prévia bater com a fila.
            var ownPhone = (await state.GetAsync(ct)).WarmupPhone;
            var filter = new ContactFilter(
                Stage: null,
                TagName: null,
                GroupTag: string.IsNullOrWhiteSpace(groupTag) ? null : groupTag,
                ExcludeOptedOut: true,
                EngagedOnly: engagedOnly ?? false,
                ExcludePhoneE164: ownPhone,
                ExcludeAlreadyDispatched: true);
            // Em Enforce, a prévia também desconta quem o registro compartilhado vai suprimir —
            // assim a contagem bate com o que o disparo realmente enfileira (mesma lógica do POST).
            if (ledger.IsEnforcing)
            {
                var targets = await contacts.ListByFilterAsync(filter, ct);
                var suppressed = await ledger.GetSuppressedAsync(targets.Select(c => c.Phone.E164).ToArray(), ct);
                return Results.Ok(new { count = targets.Count(c => !suppressed.Contains(c.Phone.E164)) });
            }
            var count = await contacts.CountByFilterAsync(filter, ct);
            return Results.Ok(new { count });
        });

        dispatch.MapGet("/report", async (
            string? status,
            int? limit,
            IDispatchJobRepository repo,
            CancellationToken ct) =>
        {
            DispatchStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<DispatchStatus>(status, ignoreCase: true, out var s))
                {
                    return Results.Problem($"unknown status '{status}'", statusCode: 400);
                }
                parsed = s;
            }
            var take = Math.Clamp(limit ?? 1000, 1, 5000);
            var items = await repo.ListReportAsync(parsed, take, ct);
            return Results.Ok(items.Select(i => new
            {
                phone = i.Phone,
                name = i.Name,
                status = i.Status,
                scheduledAt = i.ScheduledAt,
                sentAt = i.SentAt,
                errorReason = i.ErrorReason,
                attemptCount = i.AttemptCount,
            }));
        });

        dispatch.MapGet("/jobs", async (int? limit, IDispatchJobRepository repo, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var list = await repo.ListRecentAsync(take, ct);
            return Results.Ok(list.Select(j => new
            {
                id = j.Id,
                contactId = j.ContactId,
                templateId = j.TemplateId,
                status = j.Status.ToString(),
                scheduledAt = j.ScheduledAt,
                sentAt = j.SentAt,
                errorReason = j.ErrorReason,
                attemptCount = j.AttemptCount,
            }));
        });

        return app;
    }

    // Reconcilia o aquecimento com o número conectado e persiste. Retorna se detectou troca
    // de chip (e reiniciou). Usado na conexão (frontend) e como backstop antes de disparar —
    // garante que o aquecimento bate com o chip atual mesmo se a 1ª leitura do "me" falhou.
    private static async Task<(bool Changed, string? Phone)> ReconcileWarmupPhoneAsync(
        IWahaClient waha, string sessionId, ISystemStateRepository state,
        IClock clock, IUnitOfWork uow, CancellationToken ct)
    {
        var phone = await waha.GetOwnPhoneE164Async(sessionId, ct);
        var s = await state.GetAsync(ct);
        var changed = s.ReconcileWarmupPhone(phone, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        // Persiste sempre: o primeiro registro do número também muta (e retorna false).
        // Sem mudança real, o SaveChanges é no-op (não emite SQL).
        await state.UpdateAsync(s, ct);
        await uow.SaveChangesAsync(ct);
        return (changed, s.WarmupPhone);
    }

    // Guardas da imagem: tipos permitidos e teto de tamanho (localhost, imagem de promo).
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> AllowedImageMimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp" };

    private static TemplateDto ToTemplateDto(MessageTemplate t) =>
        new(t.Id, t.Slot.ToString(), t.ContentSpintax, t.Active, t.HasImage);

    public sealed record CreateTemplateRequest(string ContentSpintax, string? Slot, string? ImageBase64, string? ImageMimeType);
    public sealed record DispatchRequest(Guid TemplateId, Guid[]? TemplateIds, DispatchFilterRequest? Filter);
    public sealed record ReleaseWarmupRequest(int? Extra, bool All);
    public sealed record DispatchFilterRequest(string? Stage, string? TagName, string? GroupTag, bool? EngagedOnly);
    public sealed record TemplateDto(Guid Id, string Slot, string ContentSpintax, bool Active, bool HasImage);
}
