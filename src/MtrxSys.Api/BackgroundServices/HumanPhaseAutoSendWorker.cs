using MtrxSys.Core.Application.Warmup;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>Roda o <see cref="HumanPhaseAutoSender"/> num loop. O remetente é scoped (usa
/// repositórios) e resolvido por ciclo num escopo próprio, como o WarmupWorker faz com o
/// WarmupEngine.
///
/// Tick CURTO (1 min) e ciclo que manda NO MÁXIMO uma mensagem: o ritmo não vem daqui, vem dos
/// intervalos por pessoa dentro do remetente. Assim o tick é só "olhar se tem alguém na hora" — e
/// nunca vira rajada, por mais rápido que ele bata.
///
/// Sem toggle de config próprio: o remetente já se desliga sozinho quando a fase não se aplica, já
/// fechou, ou o operador não ligou o botão. Um ciclo ocioso custa uma leitura de estado.</summary>
public sealed class HumanPhaseAutoSendWorker(
    IServiceProvider services,
    ILogger<HumanPhaseAutoSendWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var sender = scope.ServiceProvider.GetRequiredService<HumanPhaseAutoSender>();
                await sender.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "Fase Humana: ciclo do envio automático falhou; tenta no próximo tick.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
