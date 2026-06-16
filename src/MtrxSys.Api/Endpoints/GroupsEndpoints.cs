using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;

namespace MtrxSys.Api.Endpoints;

public static class GroupsEndpoints
{
    public static IEndpointRouteBuilder MapGroupsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups");

        group.MapGet("/", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            var groups = await waha.ListGroupsAsync(sessionId, ct);
            var dtos = groups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GroupDto(g.Id, g.Name, g.ParticipantsCount));
            return Results.Ok(dtos);
        });

        group.MapPost("/{groupId}/import", async (
            string groupId,
            ImportGroupRequest? req,
            ImportGroupMembersUseCase useCase,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return Results.Problem("groupId is required", statusCode: 400);
            }
            var result = await useCase.ExecuteAsync(groupId, req?.GroupTag, ct);
            return Results.Ok(new
            {
                total = result.Total,
                imported = result.Imported,
                duplicated = result.Duplicated,
                failed = result.Failed,
                failures = result.Failures,
            });
        });

        // Sai do grupo (número conectado deixa o grupo via WAHA). O client trata 404 (já não é
        // membro) como sucesso; demais erros sobem pro usuário saber que a saída não funcionou.
        group.MapPost("/{groupId}/leave", async (
            string groupId,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return Results.Problem("groupId is required", statusCode: 400);
            }
            await waha.LeaveGroupAsync(dispatch.Value.SessionId, groupId, ct);
            return Results.Ok(new { left = true });
        });

        return app;
    }

    public sealed record ImportGroupRequest(string? GroupTag);

    public sealed record GroupDto(string Id, string Name, int? ParticipantsCount);
}
