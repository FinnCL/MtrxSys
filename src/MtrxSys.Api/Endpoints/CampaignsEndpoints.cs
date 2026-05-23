using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;

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

        var dispatch = app.MapGroup("/api/dispatch");

        dispatch.MapPost("/", async (
            DispatchRequest req,
            IContactRepository contacts,
            IDispatchJobRepository jobs,
            IMessageTemplateRepository templates,
            IUnitOfWork uow,
            IClock clock,
            CancellationToken ct) =>
        {
            if (req.TemplateId == Guid.Empty)
            {
                return Results.Problem("templateId is required", statusCode: 400);
            }
            var template = await templates.GetByIdAsync(req.TemplateId, ct);
            if (template is null)
            {
                return Results.NotFound(new { error = "template not found" });
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
                ExcludeOptedOut: true);
            var targets = await contacts.ListByFilterAsync(filter, ct);
            var now = clock.UtcNow;
            foreach (var c in targets)
            {
                var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, template.Id, now);
                await jobs.AddAsync(job, ct);
            }
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { scheduled = targets.Count });
        });

        dispatch.MapGet("/stats", async (IDispatchJobRepository repo, CancellationToken ct) =>
        {
            var stats = await repo.GetStatsAsync(ct);
            return Results.Ok(stats);
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
    public sealed record DispatchRequest(Guid TemplateId, DispatchFilterRequest? Filter);
    public sealed record DispatchFilterRequest(string? Stage, string? TagName, string? GroupTag);
    public sealed record TemplateDto(Guid Id, string Slot, string ContentSpintax, bool Active);
}
