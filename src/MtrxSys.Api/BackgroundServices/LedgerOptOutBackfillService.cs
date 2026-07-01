using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>
/// Rede de segurança do opt-out cross-ambiente. A propagação pro registro compartilhado (no webhook,
/// no link e no reconciler) é MELHOR-ESFORÇO/fail-open: se o banco compartilhado pisca no instante do
/// "SAIR", o MarkOptOut falha em silêncio e o opt-out fica SÓ local — os outros 9 chips seguiriam
/// disparando pra quem saiu. Este loop reempurra periodicamente todos os opt-outs LOCAIS pro registro
/// (idempotente: ON CONFLICT), garantindo que um opt-out sempre chegue lá, mesmo após uma falha
/// transitória. No-op quando o registro está desligado (Mode = Off) ou não há opt-outs.
/// </summary>
public sealed class LedgerOptOutBackfillService(
    IServiceProvider services,
    IOptions<SharedLedgerOptions> ledgerOpts,
    ILogger<LedgerOptOutBackfillService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ledgerOpts.Value.Mode == SharedLedgerMode.Off)
        {
            log.LogInformation("Backfill do registro desabilitado (SharedLedger:Mode=Off).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(60, ledgerOpts.Value.BackfillIntervalSeconds));
        log.LogInformation("Backfill de opt-out do registro ativo: a cada {Seconds}s.", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // rede de segurança: um ciclo que falha não pode derrubar o host
            catch (Exception ex)
            {
                log.LogWarning(ex, "Backfill de opt-out: ciclo falhou; tenta no próximo intervalo.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var ledger = sp.GetRequiredService<ISharedPhoneLedger>();
        if (!ledger.IsEnabled)
        {
            return;
        }

        var contacts = sp.GetRequiredService<IContactRepository>();
        var phones = await contacts.ListOptedOutPhonesAsync(ct);
        if (phones.Count == 0)
        {
            return;
        }
        // UPSERT em LOTE (1 query): sem churn (só escreve o que ainda não é opt-out) e sem
        // sobrescrever a proveniência. Antes era 1 conexão/upsert por telefone a cada ciclo.
        await ledger.MarkOptOutBatchAsync(phones, ct);
        log.LogDebug("Backfill de opt-out: reafirmei {Count} opt-out(s) local(is) no registro.", phones.Count);
    }
}
