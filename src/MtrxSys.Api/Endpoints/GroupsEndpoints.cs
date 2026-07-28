using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.SystemState;
using Microsoft.Extensions.Options;

namespace MtrxSys.Api.Endpoints;

public static class GroupsEndpoints
{
    public static IEndpointRouteBuilder MapGroupsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups");

        // FONTE POR MODO: no modo Emulador o aparelho é o dono da conta e já tem os grupos no próprio
        // banco — listar por ali dispensa manter um companion WAHA vinculado ao chip só pra isso. Nos 9
        // stacks WahaOnly nada muda. Mesma decisão do ImportGroupMembersUseCase, pra a tela e o import
        // nunca divergirem sobre QUAIS grupos existem.
        group.MapGet("/", async (
            IWahaClient waha,
            IPhoneOrchestrator phone,
            ISystemStateRepository systemState,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var state = await systemState.GetAsync(ct);
            if (state.DispatchMode == PhoneDispatchMode.Emulator)
            {
                var fromDevice = await phone.ListGroupsAsync(ct);
                return Results.Ok(fromDevice
                    .Select(g => new GroupDto(g.Jid, g.Subject ?? g.Jid, g.ParticipantsCount))
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));
            }

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
            IPhoneOrchestrator phone,
            ISystemStateRepository systemState,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            // Modo Emulador: lê do aparelho, com o telefone real já resolvido a partir do @lid. Quem não
            // tem telefone resolvível é DESCARTADO na origem — então a contagem aqui pode ser menor que a
            // do card do grupo, e isso é proposital (ver PhoneGroup.ParticipantsCount).
            var st = await systemState.GetAsync(ct);
            if (st.DispatchMode == PhoneDispatchMode.Emulator)
            {
                var members = await phone.ListGroupParticipantsAsync(groupId, ct);
                return Results.Ok(members
                    .Select(m => new GroupMemberDto(m.Phone, null, m.IsAdmin))
                    .OrderBy(m => m.Phone, StringComparer.Ordinal)
                    .ToList());
            }

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
            ImportResult result;
            try
            {
                result = await useCase.ExecuteAsync(groupId, req?.GroupTag, ct);
            }
            catch (DbUpdateException)
            {
                // Colisão no índice único por dígitos apesar do dedup: uma corrida (dois imports ao
                // mesmo tempo, ou o Google sync inserindo o mesmo número em paralelo) inseriu o contato
                // entre a leitura do dedup e o SaveChanges. Não é erro do operador nem bug de dado — é
                // concorrência, e a ação certa é só tentar de novo. 409 em vez de 500 (que vazava stack
                // trace e assustava). O 500 de 2026-07-28 era outra causa, JÁ corrigida no dedup; este
                // catch cobre a corrida que o dedup não tem como cobrir sozinho.
                return Results.Problem(
                    "A importação esbarrou num contato criado em paralelo. Tente novamente.",
                    statusCode: 409);
            }
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
