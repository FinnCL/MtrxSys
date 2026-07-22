using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Dispatch;

public interface IHybridCycleEnqueuer
{
    Task<int> BuildAndEnqueueAsync(CancellationToken ct);
}

/// <summary>Monta o LOTE DIÁRIO da fase HÍBRIDA (dia 4 → platô): mistura o Círculo de Aquecimento
/// (contatos SEUS, escolhidos, RE-ENVIÁVEIS) com FRIOS novos (campanha 1x cada), na ORDEM de envio
/// INTERCALADA — abre com um do círculo e espalha o resto entre os frios (padrão mais orgânico que
/// "todos seguros depois todos frios"). Reconstrói a fila do zero a cada chamada (renovação diária),
/// e deixa PAUSADA — nada sai sem "Iniciar envios".
///
/// Por que o círculo pode reenviar sem queimar: no MESMO chip o ledger já libera; e ENTRE chips o motor
/// ISENTA o círculo da supressão "Sent" e não o marca no ledger (reaquecer os SEUS números em vários
/// chips é intencional e seguro). Frios seguem o dedup (ExcludeAlreadyDispatched): campanha 1x;
/// não-respondedor e opt-out ficam fora. O sensor de entrega ignora o círculo pra não cegar.</summary>
public sealed class HybridCycleEnqueuer(
    IWarmupCircleRepository circle,
    IContactRepository contacts,
    IDispatchJobRepository jobs,
    IMessageTemplateRepository templates,
    ISystemStateRepository systemState,
    WarmupManager warmup,
    ISharedPhoneLedger ledger,
    BrazilPhoneValidator phones,
    IRandomSource rng,
    IClock clock,
    IUnitOfWork uow,
    ILogger<HybridCycleEnqueuer> log) : IHybridCycleEnqueuer
{
    public async Task<int> BuildAndEnqueueAsync(CancellationToken ct)
    {
        // Sem template ativo não dá pra montar (mesma regra do reset diário).
        var pool = (await templates.ListAllAsync(ct)).Where(t => t.Active).ToList();
        if (pool.Count == 0)
        {
            log.LogWarning("Híbrido: NÃO há template ativo — não montei o lote. Ative/crie uma mensagem.");
            return 0;
        }

        var state = await systemState.GetAsync(ct);

        // NÃO reconstruir uma fila que já está enviando (o operador já autorizou) — evita derrubar o
        // envio em curso. À meia-noite a fila está pausada, então o reset passa direto por aqui.
        var wasSending = state.IsSendingNow(
            queueHasPendingJobs: await jobs.GetEarliestPendingScheduledAtAsync(ct) is not null);
        if (wasSending)
        {
            log.LogInformation("Híbrido: fila já enviando; não reconstruo agora.");
            return 0;
        }

        // 1) Resolve o CÍRCULO (cria contato faltante, como o HumanPhaseAutoSender), pulando opt-out/descartado.
        var members = await circle.ListAsync(ct);
        var circleContacts = new List<Contact>();
        if (members.Count > 0)
        {
            var byPhone = await contacts.GetByPhonesAsync(members.Select(m => m.PhoneE164).ToArray(), ct);
            foreach (var m in members)
            {
                if (!byPhone.TryGetValue(m.PhoneE164, out var c))
                {
                    // Nasce SEM ImportedByPhone do chip? Não: marca o chip conectado pra o gate-por-chip
                    // do motor não pular (co-membro do próprio operador). optIn agora.
                    c = Contact.Create(
                        id: Guid.NewGuid(),
                        phone: phones.NormalizeTrusted(m.PhoneE164),
                        name: m.Name,
                        groupTag: null,
                        theme: null,
                        optInAt: clock.UtcNow,
                        importedByPhone: state.WarmupPhone);
                    await contacts.AddAsync(c, ct);
                }
                if (c.OptOutAt is null && c.DeletedAt is null)
                {
                    // Gate-por-chip do motor (anti-463) PULA quem não foi importado por ESTE chip. O
                    // círculo (seus números) é co-membro do próprio chip; contatos criados na Fase Humana
                    // nascem SEM ImportedByPhone — sem garantir isto, TODO envio do círculo viraria "Pulada"
                    // (o feature falharia em silêncio). Vale pra novos E existentes.
                    if (state.WarmupPhone is { } chip && c.EnsureImportedBy(chip))
                    {
                        await contacts.UpdateAsync(c, ct);
                    }
                    circleContacts.Add(c);
                }
            }
        }
        var circleIds = circleContacts.Select(c => c.Id).ToHashSet();

        // 2) RECONSTRÓI a fila: limpa a fila pendente de ontem (não-enviada) e o histórico do círculo,
        //    e reabre o círculo (LastSentAt) — assim o lote de hoje nasce limpo e intercalado. NB: estes
        //    são autocommit (fora do uow); se o SaveChanges final falhar, a fila fica vazia até o próximo
        //    ciclo (o reset re-tenta em ≤10min e reconstrói). Mesmo padrão do reset dos dias 1-3.
        await jobs.ClearPendingAsync(ct);
        if (circleContacts.Count > 0)
        {
            await contacts.ClearLastSentForPhonesAsync(circleContacts.Select(c => c.Phone.E164).ToArray(), ct);
            foreach (var c in circleContacts)
            {
                await jobs.DeleteHistoryByContactAsync(c.Id, ct); // some o "Enviada/Pulada" de ontem
            }
        }

        // 3) FRIOS novos elegíveis (campanha 1x): dedup + opt-out + não o próprio número + ledger; MENOS
        //    o círculo (que acabamos de reabrir e entra por fora do filtro).
        var coldFilter = new ContactFilter(
            EngagedOnly: false,
            ExcludeOptedOut: true,
            ExcludePhoneE164: state.WarmupPhone,
            ExcludeAlreadyDispatched: true);
        var cold = (await ledger.FilterOutSuppressedAsync(await contacts.ListByFilterAsync(coldFilter, ct), ct))
            .Where(c => !circleIds.Contains(c.Id))
            .ToList();

        // 4) Teto da curva: o círculo entra INTEIRO; os frios pegam o que sobra do teto do dia.
        var snap = await warmup.GetSnapshotAsync(ct);
        var coldAvailable = cold.Count;
        var coldTake = snap.UnlimitedToday
            ? coldAvailable
            : Math.Max(0, snap.EffectiveLimit - circleContacts.Count);
        if (!snap.UnlimitedToday && coldTake == 0 && coldAvailable > 0)
        {
            log.LogWarning(
                "Híbrido: círculo ({Circle}) ocupa todo o teto do dia ({Limit}) — {Cold} frios ficaram de "
                + "fora. Reduza o círculo pra crescer nos frios.", circleContacts.Count, snap.EffectiveLimit, coldAvailable);
        }
        cold = cold.Take(coldTake).ToList();

        // 5) INTERCALA: o SEED (círculo re-enviável + os engajados "Respondeu" que caíram nos frios) ABRE
        //    a fila; os frios NÃO-engajados são espalhados no meio (Bresenham). "Respondeu primeiro" é por
        //    STATUS (igual o Disparar manual), sem depender de estar marcado. O RENOVAR (re-disparo)
        //    segue sendo SÓ do círculo (o LastSentAt foi zerado só pra ele, no passo 2).
        var engagedCold = cold.Where(c => ContactStages.IsEngaged(c.Stage)).ToList();
        var freshCold = cold.Where(c => !ContactStages.IsEngaged(c.Stage)).ToList();
        var seq = DispatchInterleave.Interleave([.. circleContacts, .. engagedCold], freshCold);
        if (seq.Count == 0)
        {
            log.LogInformation("Híbrido: nada pra enfileirar (sem círculo elegível nem frios).");
            // Ainda assim pausa (fila foi limpa): garante que nada sai sem o operador.
            state.Pause(SystemStateAggregate.ManualPauseReason);
            await systemState.UpdateAsync(state, ct);
            await uow.SaveChangesAsync(ct);
            return 0;
        }

        // 6) Enfileira na ordem dos slots (ScheduledAt só ORDENA; o ritmo real é o DelayPolicy do motor).
        var now = clock.UtcNow;
        for (var i = 0; i < seq.Count; i++)
        {
            var tpl = pool[rng.NextInt(0, pool.Count)];
            await jobs.AddAsync(DispatchJob.Schedule(Guid.NewGuid(), seq[i].Id, tpl.Id, now.AddMilliseconds(i)), ct);
        }

        // 7) Fila PAUSADA (nada sai sem "Iniciar envios").
        state.Pause(SystemStateAggregate.ManualPauseReason);
        await systemState.UpdateAsync(state, ct);
        await uow.SaveChangesAsync(ct);

        log.LogInformation(
            "Híbrido: lote montado — {Circle} do círculo + {Cold} frios, intercalado e PAUSADO.",
            circleContacts.Count, cold.Count);
        return seq.Count;
    }

}
