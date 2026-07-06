using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.Warmup;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>Roda o ciclo do <see cref="WarmupEngine"/> num loop (PeriodicTimer). O motor é scoped
/// (usa repositório) e resolvido por ciclo num escopo próprio, como o DispatchWorker faz com o
/// DispatchEngine. No-op quando a feature não está configurada (WarmupEngine:Enabled=false).</summary>
public sealed class WarmupWorker(
    IServiceProvider services,
    IOptions<WarmupEngineOptions> opts,
    ILogger<WarmupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = opts.Value;
        if (!o.Enabled)
        {
            log.LogInformation("WarmupEngine desabilitado (WarmupEngine:Enabled=false).");
            return;
        }

        var tick = TimeSpan.FromSeconds(Math.Max(15, o.TickSeconds));
        log.LogInformation(
            "WarmupEngine ativo: tick {Tick}s · {Members} membros · {Groups} grupos.",
            tick.TotalSeconds, o.Members.Count, o.GroupInviteLinks.Count);

        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var engine = scope.ServiceProvider.GetRequiredService<WarmupEngine>();
                await engine.RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "WarmupEngine: ciclo falhou; tenta no próximo tick.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
