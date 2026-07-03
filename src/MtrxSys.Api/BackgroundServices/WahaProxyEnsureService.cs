using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>Fecha o furo de TIMING do proxy. O ensure do startup (WahaWebhookEnsurer) só aplica o
/// proxy se pegar a sessão em SCAN_QR_CODE naquele instante — se ela estava em transição
/// (Starting/Stopped), o proxy nunca entra e o chip acaba saindo pelo IP da máquina em silêncio.
/// Este serviço re-chama o ensure periodicamente: assim que a sessão fica em SCAN_QR_CODE, o proxy é
/// aplicado (e a sessão religa uma vez pra ele valer). É idempotente — quando o proxy já bate, vira um
/// GET no-op — e usa o MESMO guard de SCAN_QR_CODE, então NUNCA toca numa conta viva (Working).
/// Só ativa quando há proxy configurado (Waha:ProxyServer); sem proxy, é no-op total.
/// Template: PhoneKeepAliveService / WhatsAppAutoSyncService.</summary>
public sealed class WahaProxyEnsureService(
    IServiceProvider services,
    IOptions<WahaOptions> wahaOpts,
    ILogger<WahaProxyEnsureService> log) : BackgroundService
{
    // A sessão em SCAN_QR_CODE espera o pareamento (minutos); aplicar o proxy em ~1 min é folgado, e o
    // custo entre ticks estáveis é só um GET à sessão (barato).
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var waha = wahaOpts.Value;
        if (string.IsNullOrWhiteSpace(waha.ProxyServer))
        {
            log.LogInformation("WahaProxyEnsure: sem Waha:ProxyServer — nada a re-garantir (no-op).");
            return;
        }
        var hookUrl = string.IsNullOrWhiteSpace(waha.WebhookCallbackUrl) ? null : waha.WebhookCallbackUrl;
        if (hookUrl is null)
        {
            // O proxy é aplicado junto do webhook (mesmo PUT de config); sem webhook, não há por onde.
            log.LogWarning("WahaProxyEnsure: proxy configurado mas Waha:WebhookCallbackUrl vazio — "
                + "não dá pra re-garantir o proxy por aqui.");
            return;
        }

        log.LogInformation(
            "WahaProxyEnsure ativo: re-garante o proxy da sessão a cada {Tick}s (fecha o timing do startup).",
            Tick.TotalSeconds);

        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try
                {
                    using var scope = services.CreateScope();
                    var dispatch = scope.ServiceProvider.GetRequiredService<IOptions<DispatchOptions>>().Value;
                    var client = scope.ServiceProvider.GetRequiredService<IWahaClient>();
                    // Idempotente: aplica o proxy só se a sessão está em SCAN_QR_CODE e o proxy diverge;
                    // caso contrário é um no-op (o guard interno protege conta viva).
                    await client.EnsureWebhookConfiguredAsync(
                        dispatch.SessionId, hookUrl, waha.WebhookEvents, waha.WebhookToken, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    // Best-effort: WAHA fora / falha de rede num tick não derruba o serviço — tenta no próximo.
                    log.LogWarning(ex, "WahaProxyEnsure: falha no tick (tenta de novo no próximo).");
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
