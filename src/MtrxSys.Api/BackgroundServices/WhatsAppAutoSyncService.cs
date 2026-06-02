using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Webhooks;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>
/// Sincroniza chats/grupos/mensagens da WAHA periodicamente, sem clique manual.
/// O webhook só dispara em mensagens recebidas; eventos como "entrei num grupo" não
/// geram mensagem, então sem este loop o grupo só apareceria após sync manual.
/// </summary>
public sealed class WhatsAppAutoSyncService(
    IServiceProvider services,
    IOptions<SyncOptions> syncOpts,
    IOptions<DispatchOptions> dispatchOpts,
    IOptions<WahaOptions> wahaOpts,
    ILogger<WhatsAppAutoSyncService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = syncOpts.Value;
        if (!opts.Enabled)
        {
            log.LogInformation("Auto-sync desabilitado (Sync:Enabled=false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(15, opts.IntervalSeconds));
        var perChat = Math.Clamp(opts.MessagesPerChat, 1, 200);
        log.LogInformation(
            "Auto-sync ativo: intervalo {Seconds}s, {PerChat} msgs/chat.",
            interval.TotalSeconds, perChat);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(perChat, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auto-sync: ciclo falhou; tentando no próximo intervalo.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(int perChat, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var waha = sp.GetRequiredService<IWahaClient>();
        var sessionId = dispatchOpts.Value.SessionId;
        var status = await waha.GetSessionStatusAsync(sessionId, ct);

        // Religa a sessão parada (chip pareado reusa a auth salva, sem QR) pra o disparo rodar
        // desassistido — sem isso, depois de um restart o Dispatcher fica pausado até alguém
        // clicar "Iniciar sessão". Só de Stopped: Failed fica como "chip com falha" (decisão do
        // operador) e os demais estados são transitórios. O próximo tick já sincroniza.
        if (status == WahaSessionStatus.Stopped && wahaOpts.Value.AutoStart)
        {
            log.LogInformation("Auto-start: sessão {Session} parada, iniciando.", sessionId);
            await waha.EnsureSessionStartedAsync(sessionId, ct);
            return;
        }

        if (status != WahaSessionStatus.Working)
        {
            log.LogDebug("Auto-sync: sessão {Status}, pulando ciclo.", status);
            return;
        }

        var sync = sp.GetRequiredService<WhatsAppSyncService>();
        var result = await sync.SyncAsync(perChat, ct);
        if (result.MessagesImported > 0 || result.ContactsCreated > 0)
        {
            log.LogInformation(
                "Auto-sync: {Chats} chats, +{Msgs} msgs, +{Contacts} contatos.",
                result.ChatsTouched, result.MessagesImported, result.ContactsCreated);
        }
    }
}
