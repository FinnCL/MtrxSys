using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using Microsoft.Extensions.Options;

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

        // Telefones de quem está DENTRO do grupo. O client já resolve o número real por trás do @lid
        // (ver WahaParsing.PhoneFromParticipant), então isto é só expor o que já existia.
        group.MapGet("/{groupId}/participants", async (
            string groupId,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            // "Ver membros" DEGRADA em vez de estourar 500 no browser: um grupo-fantasma (que o WAHA já
            // não acha via getChatById, mesmo com o @g.us) ou a sessão fora fazem o /participants do WAHA
            // responder erro. Sem isto o 500 aparece no console (e o header CORS some na resposta de erro).
            // O IMPORT (ação, endpoint à parte) SEGUE estourando alto — não pode importar 0 em silêncio.
            try
            {
                var members = await waha.ListGroupParticipantsAsync(dispatch.Value.SessionId, groupId, ct);
                return Results.Ok(members
                    .Select(m => new GroupMemberDto(m.PhoneE164, m.Name, m.IsAdmin))
                    .OrderBy(m => m.Name ?? m.Phone, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch
            {
                return Results.Ok(Array.Empty<GroupMemberDto>());
            }
#pragma warning restore CA1031
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

    // FOTOGRAFA quem está no grupo AGORA, direto do WAHA (a verdade), já na forma em que a isenção
    // vai ser casada. Usado pelo "é meu" e pelo religar da chave — o mesmo ato nos dois, então mora
    // num lugar só.
    //
    // Se o WAHA estiver fora, NÃO isenta: com lista velha isentaria quem já saiu do grupo, e com
    // lista vazia acenderia a chave sem isentar ninguém. As duas mentem, e em silêncio — o operador
    // só descobriria pelo envio que não acontece.
    //
    // A normalização é a MESMA da importação de grupo (ImportGroupMembersUseCase), de propósito: a
    // isenção é casada contra `Contact.Phone.E164`, e o contato desses membros nasce lá, desta mesma
    // string do WAHA por esta mesma função. Mesma entrada + mesma função = concordam por construção.
    // NormalizeTrusted (e não Validate) pelo mesmo motivo de lá: número legado que a lib rejeita
    // (9º dígito ausente em DDD antigo) é preservado como veio em vez de sumir da isenção.
    //
    public sealed record ImportGroupRequest(string? GroupTag);

    public sealed record GroupDto(string Id, string Name, int? ParticipantsCount);

    public sealed record GroupMemberDto(string Phone, string? Name, bool IsAdmin);
}
