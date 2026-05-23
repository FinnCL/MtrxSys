using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Api.Endpoints;

public static class TagsEndpoints
{
    public static IEndpointRouteBuilder MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tags");

        group.MapGet("/", async (IContactTagRepository tags, CancellationToken ct) =>
        {
            var all = await tags.ListAllAsync(ct);
            return Results.Ok(all.Select(t => new TagDto(t.Name, t.Color, t.CreatedAt)));
        });

        group.MapPost("/", async (
            CreateTagRequest req,
            IContactTagRepository tags,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.Problem("name is required", statusCode: 400);
            }
            var key = req.Name.Trim().ToLowerInvariant();
            var existing = await tags.GetByNameAsync(key, ct);
            if (existing is not null)
            {
                return Results.Ok(new TagDto(existing.Name, existing.Color, existing.CreatedAt));
            }
            var tag = ContactTag.Create(key, req.Color, clock.UtcNow);
            await tags.AddAsync(tag, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Created($"/api/tags/{tag.Name}", new TagDto(tag.Name, tag.Color, tag.CreatedAt));
        });

        return app;
    }

    public sealed record CreateTagRequest(string Name, string? Color);

    public sealed record TagDto(string Name, string? Color, DateTimeOffset CreatedAt);
}
