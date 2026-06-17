using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Groups;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>
/// Consome a fila de coletas (disparadas pela aba Coletor) e roda o CollectGroupLinksUseCase em
/// background. A coleta envolve descoberta (busca por nicho ou Telegram) + validação throttled pela
/// página pública do convite, então não pode bloquear o request — o front dispara e faz polling.
/// </summary>
public sealed class CollectorWorker(
    IServiceProvider services,
    IGroupCollectorChannel queue,
    ISearchUsageMeter searchMeter,
    ILogger<CollectorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var useCase = scope.ServiceProvider.GetRequiredService<CollectGroupLinksUseCase>();
                var search = scope.ServiceProvider.GetRequiredService<IGroupLinkSearchSource>();
                // Mede quantas requisições de busca ESTA coleta gastou (delta) + o total acumulado —
                // vira histórico timestampado no log do container (docker logs mtrx-api).
                var reqBefore = await searchMeter.GetCountAsync(stoppingToken);
                var r = await useCase.ExecuteAsync(request.Keyword, stoppingToken);
                var reqTotal = await searchMeter.GetCountAsync(stoppingToken);
                var reqUsed = reqTotal - reqBefore;
                log.LogInformation(
                    "Coleta concluída (keyword={Keyword}): {Total} vistos, +{New} novos, {Resolved} resolvidos, "
                    + "{Invalid} inválidos, {Foreign} estrangeiros, {Pending} ainda na fila, "
                    + "{Visited} canais ({Failed} falharam) | busca {Engine}: +{ReqUsed} req (total {ReqTotal}).",
                    request.Keyword ?? "(variado)", r.TotalSeen, r.NewLinks, r.Resolved, r.Invalid,
                    r.Foreign, r.RemainingPending, r.ChannelsVisited, r.ChannelsFailed,
                    search.Engine, reqUsed, reqTotal);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "Coleta falhou; aguardando o próximo pedido.");
            }
#pragma warning restore CA1031
        }
    }
}
