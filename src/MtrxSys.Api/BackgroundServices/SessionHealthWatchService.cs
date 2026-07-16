using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>Vigia a saúde da sessão WAHA: detecta quando o chip SAI de WORKING (caiu/deslogou/ban) e
/// ALERTA (log alto + webhook configurável), pra o operador saber na 1ª hora em vez de descobrir dias
/// depois que o disparo parou. Alerta só nas TRANSIÇÕES (dedup: 1 alerta na queda, 1 na volta). O 1º
/// tick só estabelece a baseline — evita spam de "offline" em todo restart da API. Best-effort: um WAHA
/// fora / erro de leitura num tick NÃO afirma queda (não alerta por blip) — tenta de novo no próximo.
/// Template: WahaProxyEnsureService.</summary>
public sealed class SessionHealthWatchService(
    IServiceProvider services,
    IOptions<AlertOptions> alertOpts,
    SessionReadinessTracker readiness,
    ILogger<SessionHealthWatchService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var label = string.IsNullOrWhiteSpace(alertOpts.Value.Label) ? "" : $" {alertOpts.Value.Label}";
        var seconds = Math.Clamp(alertOpts.Value.SessionCheckSeconds, 15, 600);
        bool? lastWorking = null; // null = ainda sem baseline

        log.LogInformation("SessionHealthWatch ativo: checa a saúde da sessão a cada {S}s.", seconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            do
            {
                try
                {
                    using var scope = services.CreateScope();
                    var dispatch = scope.ServiceProvider.GetRequiredService<IOptions<DispatchOptions>>().Value;
                    var waha = scope.ServiceProvider.GetRequiredService<IWahaClient>();
                    // Resolve o notifier POR TICK do escopo (não injetar no singleton): ele é um typed
                    // HttpClient e capturá-lo pra sempre travaria o HttpMessageHandler (DNS velho em
                    // uptime longo). Igual ao IWahaClient acima.
                    var alerts = scope.ServiceProvider.GetRequiredService<IAlertNotifier>();
                    var status = await waha.GetSessionStatusAsync(dispatch.SessionId, stoppingToken);
                    var working = status == WahaSessionStatus.Working;

                    // Alimenta a janela de settle do envio manual: marca desde quando está WORKING
                    // contínuo (ou zera na queda). Ver SessionReadinessTracker.
                    if (working)
                    {
                        readiness.MarkWorking(DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        readiness.MarkNotWorking();
                    }

                    if (lastWorking is null)
                    {
                        lastWorking = working;
                        log.LogInformation("SessionHealthWatch: baseline — sessão {Status}.", status);
                    }
                    else if (lastWorking == true && !working)
                    {
                        lastWorking = false;
                        var msg = $"⚠️ MtrxSys{label}: chip OFFLINE (era WORKING, agora {status}). "
                            + "O disparo NÃO envia — reconecte na aba Celular.";
                        log.LogWarning("SessionHealthWatch: {Msg}", msg);
                        await alerts.NotifyAsync(msg, stoppingToken);
                    }
                    else if (lastWorking == false && working)
                    {
                        lastWorking = true;
                        var msg = $"✅ MtrxSys{label}: chip reconectado (WORKING). O disparo pode retomar.";
                        log.LogInformation("SessionHealthWatch: {Msg}", msg);
                        await alerts.NotifyAsync(msg, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    // WAHA fora / rede: NÃO muda a baseline (não afirma offline por blip); tenta no próximo.
                    log.LogWarning(ex, "SessionHealthWatch: falha no tick (tenta de novo no próximo).");
                }
#pragma warning restore CA1031
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown — fluxo normal.
        }
    }
}
