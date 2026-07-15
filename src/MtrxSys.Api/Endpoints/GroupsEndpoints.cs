using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Validation;

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
            var exempt = await owned.ListExemptWaGroupIdsAsync(ct);
            var dtos = groups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GroupDto(
                    g.Id, g.Name, g.ParticipantsCount, mine.Contains(g.Id), exempt.Contains(g.Id)));
            return Results.Ok(dtos);
        });

        // DECLARA que um grupo existente é do operador. É o caminho PRINCIPAL de posse, não um atalho
        // do "criar pelo sistema": o grupo de aquecimento nasce no APARELHO FÍSICO, na mão, porque
        // num chip novo criar grupo por API é assinatura de bot (ver OwnedGroup).
        //
        // Idempotente: declarar de novo devolve o mesmo estado em vez de estourar no índice único —
        // um clique repetido não é erro, é a mesma afirmação.
        //
        // JÁ LIGA A ISENÇÃO. Dizer "este grupo é meu" e depois ter que dizer "posso falar de novo com
        // eles" era pedir a mesma afirmação duas vezes — o grupo é de conhecidos, é pra isso que ele
        // existe. A chave continua na tela e pode ser desligada; ela só nasce ligada.
        group.MapPost("/{groupId}/claim", async (
            string groupId,
            IWahaClient waha,
            IOwnedGroupRepository owned,
            IOptions<DispatchOptions> dispatch,
            BrazilPhoneValidator phones,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (await owned.GetByWaGroupIdAsync(groupId, ct) is not null)
            {
                return Results.Ok(new { claimed = true, alreadyClaimed = true, exempt = true });
            }

            // O grupo TEM que existir na listagem do WhatsApp. Sem esta checagem daria pra declarar
            // um id inventado (ou já saído do grupo), e a linha órfã em owned_groups nunca casaria
            // com a listagem: a posse não apareceria, e o operador não saberia por quê. Além disso, a
            // listagem é a mesma fonte do `isMine` — validar contra ela garante o MESMO formato de id
            // dos dois lados (o número antes do '@', ver WahaParsing).
            var groups = await waha.ListGroupsAsync(dispatch.Value.SessionId, ct);
            var found = groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
            if (found is null)
            {
                return Results.NotFound(new
                {
                    error = "Este grupo não aparece na lista do WhatsApp conectado. "
                        + "Confira se o chip está pareado e se ele ainda participa do grupo.",
                });
            }

            // Posse e isenção numa transação só. Se ler os membros falhar, NADA é gravado: um grupo
            // "meu" sem isenção seria um estado que a tela promete e o disparo não cumpre — e o
            // operador só descobriria quando o envio pulasse a pessoa, sem erro nenhum.
            var snapshot = await ReadMemberPhonesAsync(waha, phones, dispatch.Value.SessionId, groupId, ct);
            if (snapshot.Problem is { } problem)
            {
                return problem;
            }

            var target = OwnedGroup.Create(Guid.NewGuid(), found.Id, found.Name, clock.UtcNow);
            target.EnableDispatchExemption(snapshot.Phones, Guid.NewGuid);
            await owned.AddAsync(target, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { claimed = true, alreadyClaimed = false, exempt = true });
        });

        // Desfaz a declaração. O grupo continua intacto no WhatsApp — some só a marca de que é seu.
        // A isenção cai junto (a fotografia dos membros é apagada em cascata), de propósito: um grupo
        // que não é seu não pode ter isenção, e deixá-la ligada seria uma dispensa órfã que ninguém
        // mais vê na tela pra desligar.
        group.MapDelete("/{groupId}/claim", async (
            string groupId,
            IOwnedGroupRepository owned,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var removed = await owned.RemoveAsync(groupId, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { claimed = false, wasClaimed = removed });
        });

        // Liga/desliga a dispensa da trava de "já enviei pra esse" pros membros DESTE grupo.
        // Só alcança grupo declarado seu — grupo sem registro em owned_groups nem tem o que ligar.
        group.MapPatch("/{groupId}/exemption", async (
            string groupId,
            SetExemptionRequest req,
            IWahaClient waha,
            IOwnedGroupRepository owned,
            IOptions<DispatchOptions> dispatch,
            BrazilPhoneValidator phones,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var target = await owned.GetForUpdateAsync(groupId, ct);
            if (target is null)
            {
                // 404 e não 400: a isenção é uma propriedade de grupo SEU. Num grupo sem posse
                // declarada ela não existe pra ser ligada — não é valor inválido, é ausência.
                return Results.NotFound(new
                {
                    error = "Este grupo não está marcado como seu. Clique em \"Este grupo é meu\" antes.",
                });
            }

            if (!req.Enabled)
            {
                target.DisableDispatchExemption();
                await uow.SaveChangesAsync(ct);
                return Results.Ok(new { enabled = false, members = 0 });
            }

            var snapshot = await ReadMemberPhonesAsync(waha, phones, dispatch.Value.SessionId, groupId, ct);
            if (snapshot.Problem is { } problem)
            {
                return problem;
            }
            target.EnableDispatchExemption(snapshot.Phones, Guid.NewGuid);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { enabled = true, members = target.Members.Count });
        });

        // Cria um grupo COM o sistema — é isto que torna "esse grupo é meu" um FATO e não um palpite:
        // o WAHA não expõe quem criou (ver OwnedGroup). Quem cria, sabe.
        group.MapPost("/", async (
            CreateGroupRequest req,
            IWahaClient waha,
            IOwnedGroupRepository owned,
            IOptions<DispatchOptions> dispatch,
            BrazilPhoneValidator phoneNumbers,
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
            var target = OwnedGroup.Create(Guid.NewGuid(), created.Id, created.Name, clock.UtcNow);

            // Isenta igual ao "é meu" — criar aqui também é declarar posse, e os dois caminhos têm
            // que terminar no mesmo estado.
            //
            // Lê os membros do WAHA em vez de usar os telefones DIGITADOS, mesmo já os tendo em mãos:
            // o contato desses membros vai nascer da string que o WAHA devolve, e é contra ela que a
            // isenção casa. Quem digita "+5571 99107-2835" enquanto o WhatsApp guarda
            // "+557191072835" criaria uma fotografia que não casa com contato nenhum — chave acesa,
            // disparo pulando a pessoa, sem erro.
            //
            // Falhar aqui NÃO desfaz nada: o grupo já existe no WhatsApp (irreversível) e a posse é o
            // que importa registrar. Sem a leitura, o grupo nasce SEM isenção e a chave aparece
            // desligada na tela — estado honesto e que o operador conserta num clique. Melhor que
            // perder a posse por um hiccup de rede.
            var snapshot = await ReadMemberPhonesAsync(waha, phoneNumbers, dispatch.Value.SessionId, created.Id, ct);
            var exempt = snapshot.Problem is null;
            if (exempt)
            {
                target.EnableDispatchExemption(snapshot.Phones, Guid.NewGuid);
            }

            // O grupo JÁ existe no WhatsApp neste ponto. Se gravar o registro falhar, o operador fica
            // com um grupo real que o sistema não reconhece como seu — some da seção e a isenção não
            // o alcança. Por isso o registro é parte da resposta, não melhor-esforço: se der erro,
            // ele VÊ, e pode registrar de novo (o POST é idempotente pelo unique do wa_group_id).
            await owned.AddAsync(target, ct);
            await uow.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/groups/{created.Id}",
                new GroupDto(
                    created.Id, created.Name, created.ParticipantsCount,
                    IsMine: true, ExemptFromDispatchLimits: exempt));
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
    private static async Task<(IReadOnlyList<string> Phones, IResult? Problem)> ReadMemberPhonesAsync(
        IWahaClient waha, BrazilPhoneValidator phones, string sessionId, string groupId, CancellationToken ct)
    {
        IReadOnlyList<WahaParticipant> members;
        try
        {
            members = await waha.ListGroupParticipantsAsync(sessionId, groupId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ([], Results.Problem(
                "Não deu pra ler os membros do grupo no WhatsApp agora, então nada foi salvo "
                + "(marcar sem saber quem está no grupo liberaria envio repetido pra pessoa errada). "
                + "Tente de novo.",
                statusCode: StatusCodes.Status502BadGateway));
        }
        if (members.Count == 0)
        {
            return ([], Results.Problem(
                "O WhatsApp não devolveu nenhum membro deste grupo, então não há quem isentar.",
                statusCode: StatusCodes.Status409Conflict));
        }
        return ([.. members.Select(m => phones.NormalizeTrusted(m.PhoneE164).E164)], null);
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

    public sealed record SetExemptionRequest(bool Enabled);

    // IsMine = criado por ESTE sistema (consta em owned_groups). Não é palpite: o WAHA não diz quem
    // criou, então a verdade é o registro do ato.
    // ExemptFromDispatchLimits = os membros deste grupo dispensam a trava de "já enviei pra esse".
    // Só pode ser true quando IsMine é true.
    public sealed record GroupDto(
        string Id, string Name, int? ParticipantsCount, bool IsMine, bool ExemptFromDispatchLimits);

    public sealed record GroupMemberDto(string Phone, string? Name, bool IsAdmin);
}
