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
            IDailySendCountsRepository dailyCounts,
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

            // FASE DE AQUECIMENTO: nos primeiros N dias ATIVOS do chip (dias com envio), o disparo SÓ
            // aceita "Respondeu" (EngagedOnly). Frio recém-pareado é o gatilho nº1 de ban; quem já te
            // escreveu é seguro. Conta a partir do marco do chip (re-parear reinicia). 0 desliga.
            var warmingDays = dispatchOpts.Value.WarmingResponderOnlyDays;
            var inWarming = false;
            if (warmingDays > 0 && sysState.WarmupStartedOn is { } warmupSince)
            {
                var brToday = IClock.ToBrasiliaDate(clock.UtcNow);
                var activeDays = await dailyCounts.CountActiveDaysBeforeAsync(warmupSince, brToday, ct);
                inWarming = WarmingPhase.IsActive(warmupSince, activeDays, warmingDays);
                if (inWarming && !(req.Filter?.EngagedOnly ?? false))
                {
                    return Results.Problem(
                        $"Chip em aquecimento (dia {activeDays + 1} de {warmingDays}): nesta fase só é "
                        + "permitido disparar para quem já respondeu. Selecione o público "
                        + "\"Só quem já respondeu\".",
                        statusCode: 409);
                }
            }

            var filter = new ContactFilter(
                Stage: stage,
                TagName: req.Filter?.TagName,
                GroupTag: req.Filter?.GroupTag,
                ExcludeOptedOut: true,
                EngagedOnly: req.Filter?.EngagedOnly ?? false,
                ExcludePhoneE164: ownPhone,
                ExcludeAlreadyDispatched: true); // não re-enfileira quem já recebeu/está na fila
            // Dedup entre ambientes (Enforce): tira do público quem já consta no registro compartilhado
            // (já enviado/opt-out em OUTRO chip) — evita enfileirar jobs que o motor pularia e mantém a
            // contagem coerente. Fonte única (FilterOutSuppressedAsync); no-op em Observe/Off.
            var targets = await ledger.FilterOutSuppressedAsync(await contacts.ListByFilterAsync(filter, ct), ct);
            var now = clock.UtcNow;
            // NOVOS IMPORTADOS NO TOPO DA FILA: DequeueNextPending ordena por ScheduledAt ASC, então
            // pra os recém-adicionados saírem PRIMEIRO eles precisam de ScheduledAt anterior ao mais
            // antigo já pendente. Cada lote novo fica antes do anterior → "a cada importação, os novos
            // ficam no topo". A ordem de importação é preservada entre eles (idx).
            var earliest = await jobs.GetEarliestPendingScheduledAtAsync(ct);
            var baseTime = earliest is { } e && e < now ? e : now;
            var idx = 0;
            foreach (var c in targets)
            {
                // Rodízio: cada contato recebe uma mensagem sorteada do pote.
                var tpl = pool[rng.NextInt(0, pool.Count)];
                var scheduledAt = baseTime.AddMilliseconds(-(targets.Count - idx));
                var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, tpl.Id, scheduledAt);
                await jobs.AddAsync(job, ct);
                idx++;
            }
            // Prepara a fila JÁ PAUSADA, no servidor e atômico com os jobs: os jobs nascem
            // Pending, mas o motor não envia enquanto IsManuallyPaused. Garante que NADA sai sem
            // o operador clicar "Iniciar envios" (resume) — sem depender de o front pausar antes.
            //
            // MAS NÃO PAUSA FILA QUE JÁ ESTÁ RODANDO. Este mesmo endpoint atende dois botões: o
            // "Adicionar para disparar" (fila vazia → prepara) e o "+ Adicionar N novo(s) à fila",
            // que a tela oferece DURANTE o envio. Pausar no segundo caso derrubava o envio em curso:
            // o operador clicava pra somar contatos e o motor parava calado (o banner seguia dizendo
            // "Enviando"). O próprio comentário do botão promete "não interfere na fila atual".
            //
            // A garantia continua intacta: ela é "nada sai sem o operador mandar", e numa fila que já
            // está rodando ele JÁ mandou. Pausar de novo não protege nada — só sabota.
            //
            // `earliest` (lido acima, sem consulta extra) é null quando não há job pendente.
            if (targets.Count > 0 && !sysState.IsSendingNow(queueHasPendingJobs: earliest is not null))
            {
                sysState.Pause(SystemStateAggregate.ManualPauseReason);
                await state.UpdateAsync(sysState, ct);
            }
            await uow.SaveChangesAsync(ct);
            // Fase de aquecimento: a fila tem que ser 100% respondedores. Apaga jobs LEGADOS de não-
            // respondedores (de um "Todos" anterior à trava) que estivessem "Na fila" — senão eles
            // apareceriam na tabela e o motor teria que pulá-los. Aqui já saem da fila. (Bulk, fora do
            // uow; os jobs recém-criados são de respondedores, não são afetados. Histórico fica.)
            if (inWarming)
            {
                await jobs.DeleteNonEngagedPendingAsync(ct);
            }
            // `paused` é o estado REAL depois desta chamada — a tela precisa dele pra saber se mostra
            // "Iniciar envios" ou "Enviando". Antes ela adivinhava, e adivinhava errado no somar.
            return Results.Ok(new
            {
                scheduled = targets.Count,
                templatesUsed = pool.Count,
                paused = sysState.IsManuallyPaused,
            });
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

        // Saúde de ENTREGA (sensor anti-shadow-restriction): quantos dos envios das últimas 24h
        // chegaram a "entregue" (ack >= 2). Queda forte da taxa = possível restrição silenciosa do chip
        // (mensagem sai mas não entrega) — hoje o circuit breaker só vê falha na chamada de envio.
        dispatch.MapGet("/delivery-health", async (
            ISendAuditRepository audit, IClock clock, CancellationToken ct) =>
        {
            const int windowHours = 24;
            var stats = await audit.GetDeliveryStatsAsync(clock.UtcNow.AddHours(-windowHours), ct);
            double? rate = stats.Sent > 0 ? (double)stats.Delivered / stats.Sent : null;
            return Results.Ok(new { windowHours, sent = stats.Sent, delivered = stats.Delivered, rate });
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
            WarmupManager warmup, ISystemStateRepository state,
            IOptions<DispatchOptions> dispatchOpts, CancellationToken ct) =>
        {
            var s = await warmup.GetSnapshotAsync(ct);
            var sysState = await state.GetAsync(ct);
            var phone = sysState.WarmupPhone;
            // Fase "só quem respondeu": os primeiros N dias ATIVOS (s.DayIndex = dias com envio antes de
            // hoje). Mesma definição da trava do disparo — a UI usa pra travar o seletor em "Respondeu".
            var warmingDays = dispatchOpts.Value.WarmingResponderOnlyDays;
            var responderOnlyPhase = WarmingPhase.IsActive(sysState.WarmupStartedOn, s.DayIndex, warmingDays);
            return Results.Ok(new
            {
                phone,
                startedOn = s.StartedOn,
                responderOnlyPhase,
                responderOnlyDaysLeft = responderOnlyPhase ? warmingDays - s.DayIndex : 0,
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
            s.RestartWarmup(IClock.ToBrasiliaDate(clock.UtcNow));
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
            var today = IClock.ToBrasiliaDate(clock.UtcNow);
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
            // Mesma exclusão do disparo real (próprio número + já enviados), pra a prévia bater com a
            // fila.
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
                var kept = await ledger.FilterOutSuppressedAsync(await contacts.ListByFilterAsync(filter, ct), ct);
                return Results.Ok(new { count = kept.Count });
            }
            var count = await contacts.CountByFilterAsync(filter, ct);
            return Results.Ok(new { count });
        });

        dispatch.MapGet("/report", async (
            string? status,
            int? limit,
            IDispatchJobRepository repo,
            ISystemStateRepository state,
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
            // Chip conectado agora — pra marcar no relatório quais itens são de OUTRO chip (não saem
            // deste chip; o disparo os pula, anti-463). Null (desconhecido) → não marca ninguém.
            var currentChip = (await state.GetAsync(ct)).WarmupPhone;
            return Results.Ok(items.Select(i => new
            {
                phone = i.Phone,
                name = i.Name,
                status = i.Status,
                scheduledAt = i.ScheduledAt,
                sentAt = i.SentAt,
                errorReason = i.ErrorReason,
                attemptCount = i.AttemptCount,
                // ESTRITO: só "do chip atual" quando a marca bate com o chip conectado. Legado (sem
                // marca) OU de outro chip → cinza, sem envio.
                fromCurrentChip = currentChip is null
                    || string.Equals(i.ImportedByPhone, currentChip, StringComparison.Ordinal),
                engaged = i.Engaged,
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
        var changed = s.ReconcileWarmupPhone(phone, IClock.ToBrasiliaDate(clock.UtcNow));
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
