using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.Warmup;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Domain.Warmup;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;

namespace MtrxSys.Api.Endpoints;

/// <summary>API do motor de aquecimento de conversa: status (config + toggle + contagem por membro) e
/// o toggle Iniciar/Parar (persistido no system_state). Também serve a FASE HUMANA (dias 1-3 do chip
/// novo): progresso medido, agenda do aparelho e o círculo de aquecimento. Auth herdada da
/// FallbackPolicy.</summary>
public static class WarmupEndpoints
{
    public static IEndpointRouteBuilder MapWarmupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/warmup");

        group.MapGet("/status", async (
            IOptions<WarmupEngineOptions> opts, WarmupState state, ISystemStateRepository stateRepo,
            IClock clock, CancellationToken ct) =>
        {
            var o = opts.Value;
            var st = await stateRepo.GetAsync(ct);
            var today = IClock.ToBrasiliaDate(clock.UtcNow);
            var sent = state.SnapshotSentToday(today);
            var members = o.Members
                .Select(m => new WarmupMemberStatus(m.Name, m.PhoneE164, sent.GetValueOrDefault(m.Name)))
                .ToList();
            return Results.Ok(new WarmupStatusDto(
                FeatureEnabled: o.Enabled,
                Running: st.WarmupEngineEnabled,
                MemberCount: o.Members.Count,
                GroupCount: o.GroupInviteLinks.Count,
                StartedOn: state.StartedOn?.ToString("yyyy-MM-dd"),
                Members: members));
        });

        group.MapPost("/start", async (
            IOptions<WarmupEngineOptions> opts, ISystemStateRepository stateRepo, IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var o = opts.Value;
            if (!o.Enabled)
            {
                return Results.BadRequest(new { error = "Aquecimento desabilitado por config (WarmupEngine:Enabled=false)." });
            }
            if (o.Members.Count < 2)
            {
                return Results.BadRequest(new { error = "O pool precisa de pelo menos 2 membros pra mão dupla." });
            }
            var st = await stateRepo.GetAsync(ct);
            st.SetWarmupEngineEnabled(true);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { running = true });
        });

        group.MapPost("/stop", async (ISystemStateRepository stateRepo, IUnitOfWork uow, CancellationToken ct) =>
        {
            var st = await stateRepo.GetAsync(ct);
            st.SetWarmupEngineEnabled(false);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { running = false });
        });

        // ── Fase Humana (dias 1-3 do chip novo) ──────────────────────────────────────────────────

        // Progresso medido. 200 com applies=false quando a fase não vale pra este chip (recurso
        // desligado ou chip anterior ao corte) — a UI usa isso pra não renderizar o card. Não é 404:
        // "não se aplica" é uma resposta legítima, não ausência de recurso.
        group.MapGet("/human-phase", async (
            HumanPhaseGate gate, IWarmupCircleRepository circle, ISystemStateRepository stateRepo,
            CancellationToken ct) =>
        {
            var snap = await gate.GetSnapshotAsync(ct);
            if (snap is null)
            {
                return Results.Ok(new HumanPhaseDto(Applies: false));
            }
            var st = await stateRepo.GetAsync(ct);

            // Casa cada conversa com a pessoa do círculo pelo telefone. Conversa por @lid (número
            // oculto) não casa — segue contando pro gate, só não é atribuída a uma linha do círculo.
            // Uma pessoa pode ter MAIS DE UMA conversa (histórico de outro chip, ou @c.us e @lid).
            // Escolhe a MAIS COMPLETA — a de mais trocas — em vez da primeira que aparecer: com
            // First() a escolha dependia da ordem do GroupBy (indefinida) e o card podia mostrar
            // "0↑ 0↓, não qualificado" enquanto o gate contava a outra conversa como qualificada.
            // Placar que não bate com o que destrava o disparo é pior que placar nenhum.
            var byPhone = snap.Conversations
                .Select(c => (Phone: WahaChatIdentifier.TryExtractPhoneE164(c.WaChatId), Tally: c))
                .Where(x => x.Phone is not null)
                .GroupBy(x => x.Phone!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => Math.Min(x.Tally.Inbound, x.Tally.Outbound))
                          .ThenByDescending(x => x.Tally.Inbound + x.Tally.Outbound)
                          .First().Tally,
                    StringComparer.Ordinal);

            var members = await circle.ListAsync(ct);
            var rows = members
                .Select(m =>
                {
                    var t = byPhone.GetValueOrDefault(m.PhoneE164);
                    return new HumanPhasePersonDto(
                        Phone: m.PhoneE164,
                        Name: m.Name,
                        ConversationId: t?.ConversationId,
                        Inbound: t?.Inbound ?? 0,
                        Outbound: t?.Outbound ?? 0,
                        Qualified: t is not null
                            && t.Inbound >= snap.MinInbound
                            && t.Outbound >= snap.MinOutbound);
                })
                .ToList();

            return Results.Ok(new HumanPhaseDto(
                Applies: true,
                StartedOn: snap.StartedOn.ToString("yyyy-MM-dd"),
                Satisfied: snap.Satisfied,
                ActiveDays: snap.ActiveDays,
                MinDays: snap.MinDays,
                // QualifiedPeople é do SNAPSHOT (conta QUALQUER conversa), não das linhas acima: o
                // gate não exige que a pessoa esteja no círculo — o círculo é lista de checagem.
                // Conversar com alguém fora dela também vale, e o card precisa dizer a verdade
                // sobre o que destrava o disparo.
                QualifiedPeople: snap.QualifiedPeople,
                MinPeople: snap.MinPeople,
                MinInbound: snap.MinInbound,
                MinOutbound: snap.MinOutbound,
                AutoSendEnabled: st.HumanPhaseAutoSendEnabled,
                Circle: rows));
        });

        // Liga/desliga o envio automático pro círculo (pela via do Chat — ver HumanPhaseAutoSender).
        // Só o toggle: o remetente já se cala sozinho quando a fase fecha ou não se aplica.
        group.MapPost("/human-phase/auto/{state:bool}", async (
            bool state, HumanPhaseGate gate, ISystemStateRepository stateRepo,
            IWarmupCircleRepository circle, IUnitOfWork uow, CancellationToken ct) =>
        {
            if (await gate.GetAnchorIfAppliesAsync(ct) is null)
            {
                return Results.BadRequest(new { error = "A fase humana não se aplica a este chip." });
            }
            if (state && (await circle.ListAsync(ct)).Count == 0)
            {
                return Results.BadRequest(new { error = "Monte o círculo antes: sem pessoas não há a quem escrever." });
            }
            var st = await stateRepo.GetAsync(ct);
            st.SetHumanPhaseAutoSendEnabled(state);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { autoSendEnabled = state });
        });

        // Agenda do aparelho. Por padrão só os SALVOS (isMyContact) — é o que a fase pede. `?all=true`
        // devolve tudo, pra diagnosticar caso o engine não preencha a marca (aí a lista viria vazia
        // sem explicação).
        group.MapGet("/phone-contacts", async (
            IWahaClient waha, IOptions<DispatchOptions> dispatchOpts, bool? all, CancellationToken ct) =>
        {
            var contacts = await waha.ListContactsAsync(dispatchOpts.Value.SessionId, ct);
            var filtered = all == true ? contacts : contacts.Where(c => c.IsMyContact).ToList();
            return Results.Ok(filtered
                .Select(c => new PhoneContactDto(c.PhoneE164, c.Name, c.IsMyContact))
                .OrderBy(c => c.Name ?? c.Phone, StringComparer.OrdinalIgnoreCase)
                .ToList());
        });

        // ── Círculo de aquecimento ───────────────────────────────────────────────────────────────
        // NÃO é Contact (ver WarmupCircleMember) e SOBREVIVE à troca de chip: as pessoas são do
        // operador, não do chip.

        group.MapGet("/circle", async (IWarmupCircleRepository circle, CancellationToken ct) =>
        {
            var members = await circle.ListAsync(ct);
            return Results.Ok(members
                .Select(m => new CircleMemberDto(m.Id, m.PhoneE164, m.Name))
                .ToList());
        });

        group.MapPost("/circle", async (
            AddCircleMemberRequest req, IWarmupCircleRepository circle,
            IClock clock, IUnitOfWork uow, CancellationToken ct) =>
        {
            // Normalização mínima, SEM o BrazilPhoneValidator de propósito: o número vem da agenda
            // do próprio aparelho, já em E.164, e a validação de celular BR rejeitaria um contato
            // estrangeiro legítimo. Aqui não há risco a cobrir — o círculo é só um marcador, nunca
            // entra no disparo (ver WarmupCircleMember). O disparo tem os próprios gates.
            if (BrazilPhoneValidator.NormalizeTypedE164(req.Phone) is not { } e164)
            {
                return Results.BadRequest(new { error = "Número inválido: informe em E.164 (ex.: +5571999998888)." });
            }
            // Idempotente: re-adicionar só atualiza o nome. Evita 409 num duplo-clique do seletor.
            var existing = await circle.GetByPhoneAsync(e164, ct);
            if (existing is not null)
            {
                existing.Rename(req.Name);
                await uow.SaveChangesAsync(ct);
                return Results.Ok(new CircleMemberDto(existing.Id, existing.PhoneE164, existing.Name));
            }
            var member = WarmupCircleMember.Create(Guid.NewGuid(), e164, req.Name, clock.UtcNow);
            await circle.AddAsync(member, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Created($"/api/warmup/circle/{member.Id}", new CircleMemberDto(member.Id, member.PhoneE164, member.Name));
        });

        group.MapDelete("/circle/{id:guid}", async (
            Guid id, IWarmupCircleRepository circle, IUnitOfWork uow, CancellationToken ct) =>
        {
            var removed = await circle.RemoveAsync(id, ct);
            if (!removed)
            {
                return Results.NotFound();
            }
            await uow.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private sealed record WarmupStatusDto(
        bool FeatureEnabled, bool Running, int MemberCount, int GroupCount, string? StartedOn,
        IReadOnlyList<WarmupMemberStatus> Members);

    private sealed record WarmupMemberStatus(string Name, string Phone, int SentToday);

    private sealed record HumanPhaseDto(
        bool Applies,
        string? StartedOn = null,
        bool Satisfied = false,
        int ActiveDays = 0,
        int MinDays = 0,
        int QualifiedPeople = 0,
        int MinPeople = 0,
        int MinInbound = 0,
        int MinOutbound = 0,
        bool AutoSendEnabled = false,
        IReadOnlyList<HumanPhasePersonDto>? Circle = null);

    private sealed record HumanPhasePersonDto(
        string Phone, string? Name, Guid? ConversationId, int Inbound, int Outbound, bool Qualified);

    private sealed record PhoneContactDto(string Phone, string? Name, bool IsMyContact);

    private sealed record CircleMemberDto(Guid Id, string Phone, string? Name);

    private sealed record AddCircleMemberRequest(string Phone, string? Name);
}
