using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Api.Endpoints;

public static class GroupsEndpoints
{
    public static IEndpointRouteBuilder MapGroupsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups");

        group.MapGet("/", async (
            IWahaClient waha,
            IOwnedGroupRepository owned,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            var groups = await waha.ListGroupsAsync(sessionId, ct);
            // "É meu?" sai de UMA leitura em lote, não de N consultas — a lista pode ter dezenas de
            // grupos e isto roda a cada abertura da aba.
            var mine = await owned.ListWaGroupIdsAsync(ct);
            var dtos = groups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GroupDto(g.Id, g.Name, g.ParticipantsCount, mine.Contains(g.Id)));
            return Results.Ok(dtos);
        });

        // Cria um grupo COM o sistema — é isto que torna "esse grupo é meu" um FATO e não um palpite:
        // o WAHA não expõe quem criou (ver OwnedGroup). Quem cria, sabe.
        group.MapPost("/", async (
            CreateGroupRequest req,
            IWahaClient waha,
            IOwnedGroupRepository owned,
            IOptions<DispatchOptions> dispatch,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.BadRequest(new { error = "Dê um nome ao grupo." });
            }
            var phones = (req.Phones ?? [])
                .Select(NormalizeE164)
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (phones.Count == 0)
            {
                return Results.BadRequest(new { error = "Informe ao menos um participante (E.164, ex.: +5571999998888)." });
            }

            var created = await waha.CreateGroupAsync(dispatch.Value.SessionId, req.Name.Trim(), phones, ct);
            // O grupo JÁ existe no WhatsApp neste ponto. Se gravar o registro falhar, o operador fica
            // com um grupo real que o sistema não reconhece como seu — some da seção e a isenção não
            // o alcança. Por isso o registro é parte da resposta, não melhor-esforço: se der erro,
            // ele VÊ, e pode registrar de novo (o POST é idempotente pelo unique do wa_group_id).
            await owned.AddAsync(
                OwnedGroup.Create(Guid.NewGuid(), created.Id, created.Name, clock.UtcNow), ct);
            await uow.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/groups/{created.Id}",
                new GroupDto(created.Id, created.Name, created.ParticipantsCount, IsMine: true));
        });

        // Telefones de quem está DENTRO do grupo. O client já resolve o número real por trás do @lid
        // (ver WahaParsing.PhoneFromParticipant), então isto é só expor o que já existia.
        group.MapGet("/{groupId}/participants", async (
            string groupId,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var members = await waha.ListGroupParticipantsAsync(dispatch.Value.SessionId, groupId, ct);
            return Results.Ok(members
                .Select(m => new GroupMemberDto(m.PhoneE164, m.Name, m.IsAdmin))
                .OrderBy(m => m.Name ?? m.Phone, StringComparer.OrdinalIgnoreCase)
                .ToList());
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

    // "+" seguido de 8 a 15 dígitos (faixa do E.164). Mesma normalização mínima do círculo de
    // aquecimento, e pelo mesmo motivo: são pessoas conhecidas suas, e o BrazilPhoneValidator
    // rejeitaria um contato estrangeiro legítimo. Devolve null se não der — o chamador vira 400.
    private static string? NormalizeE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var digits = new string([.. raw.Where(char.IsDigit)]);
        if (digits.Length is < 8 or > 15 || digits.All(c => c == '0'))
        {
            return null;
        }
        return "+" + digits;
    }

    public sealed record ImportGroupRequest(string? GroupTag);

    public sealed record CreateGroupRequest(string? Name, IReadOnlyList<string>? Phones);

    // IsMine = criado por ESTE sistema (consta em owned_groups). Não é palpite: o WAHA não diz quem
    // criou, então a verdade é o registro do ato.
    public sealed record GroupDto(string Id, string Name, int? ParticipantsCount, bool IsMine);

    public sealed record GroupMemberDto(string Phone, string? Name, bool IsAdmin);
}
