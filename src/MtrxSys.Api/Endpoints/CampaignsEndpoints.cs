using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;
using MtrxSys.Core.Domain.SystemState;

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
            var template = MessageTemplate.Create(Guid.NewGuid(), slot, req.ContentSpintax, active: true);
            await repo.AddAsync(template, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Created($"/api/templates/{template.Id}", ToTemplateDto(template));
        });

        templates.MapDelete("/{id:guid}", async (
            Guid id,
            IMessageTemplateRepository repo,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var t = await repo.GetByIdAsync(id, ct);
            if (t is null)
            {
                return Results.NotFound();
            }
            t.Deactivate();
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
            CancellationToken ct) =>
        {
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
            var filter = new ContactFilter(
                Stage: stage,
                TagName: req.Filter?.TagName,
                GroupTag: req.Filter?.GroupTag,
                ExcludeOptedOut: true,
                EngagedOnly: req.Filter?.EngagedOnly ?? false);
            var targets = await contacts.ListByFilterAsync(filter, ct);
            var now = clock.UtcNow;
            foreach (var c in targets)
            {
                // Rodízio: cada contato recebe uma mensagem sorteada do pote.
                var tpl = pool[rng.NextInt(0, pool.Count)];
                var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, tpl.Id, now);
                await jobs.AddAsync(job, ct);
            }
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { scheduled = targets.Count, templatesUsed = pool.Count });
        });

        dispatch.MapGet("/stats", async (IDispatchJobRepository repo, CancellationToken ct) =>
        {
            var stats = await repo.GetStatsAsync(ct);
            return Results.Ok(stats);
        });

        dispatch.MapGet("/status", async (ISystemStateRepository state, CancellationToken ct) =>
        {
            var s = await state.GetAsync(ct);
            return Results.Ok(new { paused = s.IsManuallyPaused });
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

        dispatch.MapGet("/audience-count", async (
            bool? engagedOnly,
            string? groupTag,
            IContactRepository contacts,
            CancellationToken ct) =>
        {
            var filter = new ContactFilter(
                Stage: null,
                TagName: null,
                GroupTag: string.IsNullOrWhiteSpace(groupTag) ? null : groupTag,
                ExcludeOptedOut: true,
                EngagedOnly: engagedOnly ?? false);
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
            }));
        });

        return app;
    }

    private static TemplateDto ToTemplateDto(MessageTemplate t) =>
        new(t.Id, t.Slot.ToString(), t.ContentSpintax, t.Active);

    public sealed record CreateTemplateRequest(string ContentSpintax, string? Slot);
    public sealed record DispatchRequest(Guid TemplateId, Guid[]? TemplateIds, DispatchFilterRequest? Filter);
    public sealed record DispatchFilterRequest(string? Stage, string? TagName, string? GroupTag, bool? EngagedOnly);
    public sealed record TemplateDto(Guid Id, string Slot, string ContentSpintax, bool Active);
}
