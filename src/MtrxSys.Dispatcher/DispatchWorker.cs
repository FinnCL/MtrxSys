using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MtrxSys.Dispatcher;

public sealed class DispatchWorker(IServiceScopeFactory scopes, ILogger<DispatchWorker> log) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes;
    private readonly ILogger<DispatchWorker> _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DispatchWorker iniciado.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var engine = scope.ServiceProvider.GetRequiredService<DispatchEngine>();
                await engine.RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // top-level worker loop — engulir para não derrubar o BackgroundService
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha no ciclo do DispatchEngine — aguardando antes de reentrar.");
            }
#pragma warning restore CA1031

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        _log.LogInformation("DispatchWorker parando.");
    }
}
