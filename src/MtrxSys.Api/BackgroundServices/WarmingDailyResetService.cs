using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
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

        var warmingDays = sp.GetRequiredService<IOptions<DispatchOptions>>().Value.WarmingResponderOnlyDays;
        if (warmingDays <= 0)
        {
            return; // trava desligada
        }

        var state = await sp.GetRequiredService<ISystemStateRepository>().GetAsync(ct);
        if (state.WarmupStartedOn is not { } since)
        {
            return; // sem marco de chip → fase não se aplica
        }

        var counts = sp.GetRequiredService<IDailySendCountsRepository>();
        var activeDays = await counts.CountActiveDaysBeforeAsync(since, brToday, ct);
        if (!WarmingPhase.IsActive(since, activeDays, warmingDays))
        {
            return; // aquecimento concluído / não se aplica → disparo normal, sem reset automático
        }

        // Só renova se o dia que fechou teve disparo (senão o aquecimento não avança nem libera).
        var yesterday = brToday.AddDays(-1);
        var yesterdayHadActivity = await counts.CountActiveDaysBeforeAsync(yesterday, brToday, ct) > 0;
        if (!yesterdayHadActivity)
        {
            log.LogInformation("Aquecimento: sem disparo ontem — não renova (dia ativo {D}/{T}).", activeDays, warmingDays);
            return;
        }

        var contacts = sp.GetRequiredService<IContactRepository>();
        var cleared = await contacts.ClearLastSentForEngagedAsync(ct);
        log.LogInformation(
            "Aquecimento: reset diário — {N} respondedores liberados pra novo disparo (dia ativo {D}/{T}).",
            cleared, activeDays, warmingDays);
    }
}
