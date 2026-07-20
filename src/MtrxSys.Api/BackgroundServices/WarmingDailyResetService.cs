using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Dispatch;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>Reset diário do AQUECIMENTO POR RESPONDEDORES. À meia-noite de Brasília, enquanto o chip está
/// nos primeiros N dias ativos (ver DispatchOptions.WarmingResponderOnlyDays), libera os respondedores
/// (zera o LastSentAt de quem engajou) pra você re-disparar pros MESMOS no dia seguinte — já sabemos que
/// mandar pra quem respondeu não derruba o chip. Só renova se HOUVE disparo no dia que fechou: sem
/// atividade, o aquecimento não avança nem libera (regra "se disparou, renova; se não, não").
///
/// Fora da fase (N dias ativos cumpridos, ou WarmupStartedOn ausente, ou trava desligada) não faz nada —
/// o disparo volta ao normal (todas as audiências, sem reset automático). Estado em memória (o dia já
/// processado): reiniciar a API só re-ancora no dia atual, nunca reseta retroativo. Template:
/// SessionHealthWatchService.</summary>
public sealed class WarmingDailyResetService(
    IServiceProvider services,
    ILogger<WarmingDailyResetService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? lastProcessed = null;
        log.LogInformation("WarmingDailyReset ativo: libera os respondedores à meia-noite de Brasília durante o aquecimento.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            do
            {
                try
                {
                    var brToday = IClock.ToBrasiliaDate(DateTimeOffset.UtcNow);
                    // 1ª passada pós-startup: só ancora o dia (não reseta retroativo num restart da API).
                    if (lastProcessed is null)
                    {
                        lastProcessed = brToday;
                        continue;
                    }
                    if (brToday <= lastProcessed)
                    {
                        continue; // ainda o mesmo dia BRT — nada a fazer
                    }

                    await RunForNewDayAsync(brToday, stoppingToken);
                    lastProcessed = brToday;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Falha no reset diário do aquecimento; tenta no próximo tick.");
                }
#pragma warning restore CA1031
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task RunForNewDayAsync(DateOnly brToday, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var state = await sp.GetRequiredService<ISystemStateRepository>().GetAsync(ct);
        // Fonte única da fase (mesma conta do disparo e do relatório). Active=false cobre trava
        // desligada, chip sem marco e aquecimento já concluído → disparo normal, sem reset automático.
        var warming = await sp.GetRequiredService<WarmingPhaseService>().EvaluateAsync(state, ct);
        // ResponderOnly (dias 1-3) e Hybrid (dia 4 → platô) renovam a fila diária; Mature / trava off não.
        if (!warming.Active && !warming.IsHybrid)
        {
            return;
        }

        // Só renova se o dia que fechou teve disparo (senão o aquecimento não avança nem libera).
        var counts = sp.GetRequiredService<IDailySendCountsRepository>();
        var yesterday = brToday.AddDays(-1);
        var yesterdayHadActivity = await counts.CountActiveDaysBeforeAsync(yesterday, brToday, ct) > 0;
        if (!yesterdayHadActivity)
        {
            log.LogInformation(
                "Aquecimento: sem disparo ontem — não renova (dia ativo {D}/{T}).",
                warming.ActiveDays, warming.WarmingDays);
            return;
        }

        // FASE HÍBRIDA (dia 4 → platô): monta o lote misto (círculo re-enviável + frios novos,
        // intercalado) via o builder único, e PAUSA. Sai daqui — o resto abaixo é só ResponderOnly.
        if (warming.IsHybrid)
        {
            var built = await sp.GetRequiredService<IHybridCycleEnqueuer>().BuildAndEnqueueAsync(ct);
            log.LogInformation(
                "Aquecimento HÍBRIDO: lote diário montado ({N} contatos) e PAUSADO. Dia ativo {D}/{T}.",
                built, warming.ActiveDays, warming.WarmingDays);
            return;
        }

        var contacts = sp.GetRequiredService<IContactRepository>();
        var jobs = sp.GetRequiredService<IDispatchJobRepository>();
        var templates = sp.GetRequiredService<IMessageTemplateRepository>();
        var rng = sp.GetRequiredService<IRandomSource>();
        var ledger = sp.GetRequiredService<ISharedPhoneLedger>();
        var clock = sp.GetRequiredService<IClock>();
        var uow = sp.GetRequiredService<IUnitOfWork>();
        var stateRepo = sp.GetRequiredService<ISystemStateRepository>();

        // 1. Libera os respondedores (LastSentAt) E limpa o histórico deles — pra o ciclo do dia
        //    começar LIMPO, sem o "Enviada" de ontem duplicando com o "Na fila" de hoje. Bulk, fora do uow.
        var freed = await contacts.ClearLastSentForEngagedAsync(ct);
        await jobs.DeleteEngagedHistoryAsync(ct);

        // Templates ATIVOS pro rodízio (mesmo pote do disparo normal). Sem nenhum ativo → só libera;
        // não dá pra re-enfileirar sem mensagem.
        var pool = (await templates.ListAllAsync(ct)).Where(t => t.Active).ToList();
        if (pool.Count == 0)
        {
            log.LogWarning(
                "Aquecimento: reset diário liberou {N} respondedores, mas NÃO há template ativo — não "
                + "re-enfileirei. Ative/crie uma mensagem pra o re-envio automático funcionar.", freed);
            return;
        }

        // 2. Público SEGURO: engajado, sem opt-out, não o próprio número, e sem job na fila. (1ª das 3
        //    blindagens; as outras: fila PAUSADA abaixo, e o motor PULA não-respondedor no envio.)
        var filter = new ContactFilter(
            EngagedOnly: true, ExcludeOptedOut: true,
            ExcludePhoneE164: state.WarmupPhone, ExcludeAlreadyDispatched: true);
        var targets = await ledger.FilterOutSuppressedAsync(await contacts.ListByFilterAsync(filter, ct), ct);
        if (targets.Count == 0)
        {
            log.LogInformation("Aquecimento: reset diário — {N} liberados, nenhum pra re-enfileirar agora.", freed);
            return;
        }

        // 3. Cria os jobs (rodízio) e PAUSA a fila (2ª blindagem): NADA sai sem o operador clicar
        //    "Iniciar envios" de manhã — evita envio automático de madrugada e mantém o controle da hora.
        var now = clock.UtcNow;
        var idx = 0;
        foreach (var c in targets)
        {
            var tpl = pool[rng.NextInt(0, pool.Count)];
            await jobs.AddAsync(DispatchJob.Schedule(Guid.NewGuid(), c.Id, tpl.Id, now.AddMilliseconds(idx)), ct);
            idx++;
        }
        state.Pause(SystemStateAggregate.ManualPauseReason);
        await stateRepo.UpdateAsync(state, ct);
        await uow.SaveChangesAsync(ct);

        // Fila 100% respondedores (mesmo contrato do endpoint de disparo): remove qualquer job legado de
        // não-respondedor que tenha sobrado na fila — senão ele apareceria na tabela e o motor teria que
        // pulá-lo. Os jobs recém-criados acima são de engajados, não são afetados. (Bulk, fora do uow.)
        await jobs.DeleteNonEngagedPendingAsync(ct);

        log.LogInformation(
            "Aquecimento: reset diário — {N} respondedores re-enfileirados e PAUSADOS (clique 'Iniciar "
            + "envios'). Dia ativo {D}/{T}.", targets.Count, warming.ActiveDays, warming.WarmingDays);
    }
}
