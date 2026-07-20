using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.Application.UseCases.Dispatch;

public interface IResponderDispatchEnqueuer
{
    Task<bool> EnqueueSingleResponderAsync(Guid contactId, CancellationToken ct);
}

/// <summary>Enfileira PRA DISPARO um contato que ACABOU de responder, durante a fase de aquecimento
/// "só respondedores". É o gatilho por-EVENTO do que o reset diário (WarmingDailyResetService) faz em
/// lote à meia-noite — só que na hora da resposta, pro operador não esperar o dia virar.
///
/// Resultado: o contato aparece em "Resultado dos envios" como "Na fila" + "Respondeu" (job Pending),
/// e uma "Pulada" velha (de quando ele estava frio) é limpa pra a linha não ficar contraditória. A
/// fila continua PAUSADA — nada sai sem o operador clicar "Iniciar envios" — a menos que já esteja
/// enviando.
///
/// SEGURANÇA: só age DENTRO da fase (WarmingPhaseService.Active); fora dela não faz nada (o fluxo
/// normal por audiências vale). Idempotente e sem envio dobrado: reusa o dedup do disparo em lote
/// (ExcludeAlreadyDispatched) — quem JÁ recebeu a campanha (LastSentAt) ou JÁ está na fila
/// (Pending/Retrying) é ignorado. NÃO marca o contato como "já disparado" — o "oi" manual continua sem
/// bloquear a campanha do robô.</summary>
public sealed class ResponderDispatchEnqueuer(
    IContactRepository contacts,
    IDispatchJobRepository jobs,
    IMessageTemplateRepository templates,
    ISystemStateRepository systemState,
    ISharedPhoneLedger ledger,
    WarmingPhaseService warmingPhase,
    IRandomSource rng,
    IClock clock,
    IUnitOfWork uow,
    ILogger<ResponderDispatchEnqueuer> log) : IResponderDispatchEnqueuer
{
    /// <summary>Enfileira o contato (por Id) pra disparo se ele estiver engajado e a fase de
    /// aquecimento estiver ativa. Retorna true se criou o job. Best-effort: o chamador engole falhas
    /// (o reset diário é a reserva).</summary>
    public async Task<bool> EnqueueSingleResponderAsync(Guid contactId, CancellationToken ct)
    {
        // Só na FASE de aquecimento por respondedores. Fora dela, o fluxo normal (audiências) vale —
        // não enfileiramos nada automático. Barato (uma leitura de estado + uma contagem) pra o caso
        // comum "fora da fase" sair cedo, antes de qualquer outra consulta.
        var state = await systemState.GetAsync(ct);
        var warming = await warmingPhase.EvaluateAsync(state, ct);
        if (!warming.Active)
        {
            return false;
        }

        // Sem template ativo não dá pra enfileirar (mesma regra do reset diário). Grita no log.
        var pool = (await templates.ListAllAsync(ct)).Where(t => t.Active).ToList();
        if (pool.Count == 0)
        {
            log.LogWarning(
                "Respondedor {ContactId} engajou, mas NÃO há template ativo — não enfileirei. "
                + "Ative/crie uma mensagem pra o disparo automático de respondedores funcionar.", contactId);
            return false;
        }

        // FONTE ÚNICA do "posso disparar pra esse contato agora?": reusa EXATAMENTE o filtro do disparo
        // em lote, restrito a este contato. ExcludeAlreadyDispatched cobre tudo de uma vez, e a ordem
        // importa — CHECAMOS antes de mutar qualquer coisa (idempotência sem desperdício):
        //   • já recebeu a campanha (LastSentAt) → NÃO re-enfileira (evita envio DOBRADO);
        //   • já está na fila (Pending/Retrying) → resposta repetida não cria 2º job;
        //   • + engajado, sem opt-out, não o próprio número, e não suprimido no ledger cross-chip.
        // Vazio = não elegível → retorna sem tocar em nada.
        var filter = new ContactFilter(
            EngagedOnly: true,
            ExcludeOptedOut: true,
            ExcludePhoneE164: state.WarmupPhone,
            ExcludeAlreadyDispatched: true,
            ContactId: contactId);
        var eligible = await ledger.FilterOutSuppressedAsync(await contacts.ListByFilterAsync(filter, ct), ct);
        if (eligible.Count == 0)
        {
            return false;
        }

        // Elegível → enfileira. Pausa a fila (salvo se JÁ estiver enviando — aí o operador já
        // autorizou; pausar sabotaria o envio em curso). Mesmo guard do endpoint (IsSendingNow).
        var wasSending = state.IsSendingNow(
            queueHasPendingJobs: await jobs.GetEarliestPendingScheduledAtAsync(ct) is not null);

        var tpl = pool[rng.NextInt(0, pool.Count)];
        await jobs.AddAsync(DispatchJob.Schedule(Guid.NewGuid(), contactId, tpl.Id, clock.UtcNow), ct);

        // Só ESCREVE system_state quando realmente precisa virar a chave (fila ociosa E ainda não
        // pausada manualmente). Em regime de aquecimento a fila já está MANUAL-pausada, então pular a
        // escrita evita disputar o token xmin do singleton quando duas respostas chegam juntas — sem o
        // guard, a perdedora estouraria concorrência e teria o job (mesmo SaveChanges) revertido junto.
        // Jobs novos têm PK própria, então inserts concorrentes não colidem.
        if (!wasSending && !state.IsManuallyPaused)
        {
            state.Pause(SystemStateAggregate.ManualPauseReason);
            await systemState.UpdateAsync(state, ct);
        }
        await uow.SaveChangesAsync(ct);

        // Limpa uma "Pulada"/"Falhou" velha (de quando o contato estava frio) DEPOIS de o job estar
        // commitado. Autocommit (ExecuteDelete, fora do uow): se rodasse ANTES e o SaveChanges falhasse,
        // o contato ficaria sem NENHUMA linha (sumia do relatório). Depois, o pior caso é mostrar
        // "Na fila" + "Pulada" por um instante (cosmético, some no próximo ciclo). Como passou o dedup,
        // LastSentAt é null → não há "Enviada" a apagar (só skip/falha).
        await jobs.DeleteHistoryByContactAsync(contactId, ct);

        log.LogInformation("Respondedor {ContactId} enfileirado pra disparo na hora (fase de aquecimento).", contactId);
        return true;
    }
}
