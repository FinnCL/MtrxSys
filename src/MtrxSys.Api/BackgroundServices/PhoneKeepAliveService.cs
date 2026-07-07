using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>
/// Mantém o aparelho PRIMÁRIO (emulador) leve. Duas responsabilidades:
///  • stop-after-pair: depois que o WAHA vincula (sessão WORKING) + uma carência, DESLIGA o emulador
///    — no regime normal só o WAHA roda (ele dispara sozinho ~14 dias sem o principal).
///  • keep-alive: a cada ~10 dias acorda o primário rapidinho (o WhatsApp desloga o companion se o
///    principal sumir ~14 dias), num horário escalonado por stack, e desliga de novo.
/// Fail-safe: sem docker/KVM (PhoneStatus "unavailable") vira no-op. Template: WhatsAppAutoSyncService.
/// </summary>
public sealed class PhoneKeepAliveService(
    IServiceProvider services,
    IOptions<PhoneOptions> phoneOpts,
    IOptions<DispatchOptions> dispatchOpts,
    IClock clock,
    PhoneKeepAliveSignal signal,
    ILogger<PhoneKeepAliveService> log) : BackgroundService
{
    // Primeira vez que vimos o WAHA WORKING com o emulador ligado — base da carência do stop-after-pair.
    private DateTimeOffset? _firstWorkingSeen;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = phoneOpts.Value;
        if (!opts.KeepAliveEnabled && !opts.StopAfterPairEnabled)
        {
            log.LogInformation("PhoneKeepAlive desabilitado (KeepAlive e StopAfterPair = false).");
            return;
        }

        var tick = TimeSpan.FromSeconds(Math.Max(15, opts.KeepAliveTickSeconds));
        log.LogInformation(
            "PhoneKeepAlive ativo: tick {Tick}s · keep-alive a cada {Hours}h · stop-after-pair {Stop}.",
            tick.TotalSeconds, opts.KeepAliveIntervalHours, opts.StopAfterPairEnabled);

        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                await RunOnceAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "PhoneKeepAlive: ciclo falhou; tenta no próximo tick.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(PhoneOptions opts, CancellationToken ct)
    {
        var manual = signal.Consume();

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var phone = sp.GetRequiredService<IPhoneOrchestrator>();

        var status = await phone.GetStatusAsync(ct);
        if (status.State == "unavailable")
        {
            return; // sem docker/KVM: no-op (a aba já mostra "indisponível")
        }

        // Acordar-agora manual OU keep-alive vencido → ciclo de wake (o manual pula a janela/agenda).
        if (opts.KeepAliveEnabled && (manual || await IsKeepAliveDueAsync(sp, opts, ct)))
        {
            await WakeCycleAsync(sp, phone, opts, manual, ct);
            return;
        }

        // Senão: emulador ligado + WAHA já vinculado → desliga (stop-after-pair).
        if (opts.StopAfterPairEnabled && status.Running)
        {
            await MaybeStopAfterPairAsync(sp, phone, opts, ct);
        }
        else if (!status.Running)
        {
            _firstWorkingSeen = null; // desligado: zera o relógio da carência
        }
    }

    private async Task MaybeStopAfterPairAsync(
        IServiceProvider sp, IPhoneOrchestrator phone, PhoneOptions opts, CancellationToken ct)
    {
        var waha = sp.GetRequiredService<IWahaClient>();
        var wahaStatus = await waha.GetSessionStatusAsync(dispatchOpts.Value.SessionId, ct);
        if (wahaStatus != WahaSessionStatus.Working)
        {
            _firstWorkingSeen = null; // ainda registrando/pareando: NUNCA desliga
            return;
        }

        _firstWorkingSeen ??= clock.UtcNow;
        var elapsed = clock.UtcNow - _firstWorkingSeen.Value;
        if (elapsed < TimeSpan.FromMinutes(opts.StopAfterPairGraceMinutes))
        {
            return; // deixa o sync inicial de histórico terminar antes de dormir
        }

        log.LogInformation(
            "Stop-after-pair: WAHA WORKING há {Min} min; {Container} vai dormir (WAHA segue disparando).",
            (int)elapsed.TotalMinutes, opts.ContainerName);
        await RecordOnlineAsync(sp, ct);
        await phone.StopAsync(ct);
        _firstWorkingSeen = null;
    }

    private async Task WakeCycleAsync(
        IServiceProvider sp, IPhoneOrchestrator phone, PhoneOptions opts, bool manual, CancellationToken ct)
    {
        log.LogInformation("Keep-alive: acordando {Container} ({Reason}).",
            opts.ContainerName, manual ? "manual" : "agendado");

        var st = await phone.GetStatusAsync(ct);
        if (st.State == "not_created")
        {
            await phone.ProvisionAsync(ct);
        }
        else if (!st.Running)
        {
            await phone.StartAsync(ct);
        }

        if (!await WaitForBootAsync(phone, TimeSpan.FromSeconds(opts.KeepAliveBootTimeoutSeconds), ct))
        {
            log.LogWarning("Keep-alive: {Container} não bootou a tempo; desligando (tenta no próximo ciclo).",
                opts.ContainerName);
            await phone.StopAsync(ct);
            return; // NÃO grava online: o primário não subiu de fato
        }

        // Android subiu → o primário está online (o WhatsApp reconecta em background). Espera o WAHA
        // voltar a WORKING como prova de saúde do vínculo e segura um tempo pro WhatsApp reconectar.
        var working = await WaitForWorkingAsync(
            sp, dispatchOpts.Value.SessionId, TimeSpan.FromSeconds(opts.KeepAliveReconnectTimeoutSeconds), ct);
        await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opts.KeepAliveHoldMinutes)), ct);

        await RecordOnlineAsync(sp, ct); // primário esteve online → reinicia a janela de ~14 dias
        if (!working)
        {
            log.LogWarning(
                "Keep-alive: {Container} subiu mas o WAHA NÃO voltou a WORKING — o vínculo pode ter caído; re-parear por QR.",
                opts.ContainerName);
        }

        await phone.StopAsync(ct);
        log.LogInformation("Keep-alive: {Container} dormindo de novo.", opts.ContainerName);
    }

    private async Task<bool> IsKeepAliveDueAsync(IServiceProvider sp, PhoneOptions opts, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<ISystemStateRepository>();
        var state = await repo.GetAsync(ct);
        var last = state.PhonePrimaryLastOnlineUtc;
        if (last is null)
        {
            return false; // nunca pareado: nada a manter vivo ainda
        }
        if (clock.UtcNow - last.Value < TimeSpan.FromHours(Math.Max(0, opts.KeepAliveIntervalHours)))
        {
            return false;
        }
        return IsInStaggerWindow(opts); // já vencido: só na janela escalonada deste stack
    }

    // Cada stack acorda numa faixa distinta do dia (slot 0-9 → janela de ~2.4h) pra os 10 não subirem
    // juntos. Slot explícito (0-9) ou derivado de um hash estável do ContainerName.
    private bool IsInStaggerWindow(PhoneOptions opts)
    {
        var slot = opts.KeepAliveStaggerSlot is >= 0 and <= 9
            ? opts.KeepAliveStaggerSlot
            : StableSlot(opts.ContainerName);
        var hour = clock.UtcNow.Hour;
        return hour >= slot * 24 / 10 && hour < (slot + 1) * 24 / 10;
    }

    // FNV-1a — estável ENTRE processos (string.GetHashCode é randomizado por processo, então dois
    // stacks poderiam colidir de forma diferente a cada boot).
    private static int StableSlot(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)(hash % 10);
    }

    private static async Task<bool> WaitForBootAsync(IPhoneOrchestrator phone, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await phone.IsBootedAsync(ct))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return false;
    }

    private static async Task<bool> WaitForWorkingAsync(
        IServiceProvider sp, string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var waha = sp.GetRequiredService<IWahaClient>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await waha.GetSessionStatusAsync(sessionId, ct) == WahaSessionStatus.Working)
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return false;
    }

    private async Task RecordOnlineAsync(IServiceProvider sp, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<ISystemStateRepository>();
        var uow = sp.GetRequiredService<IUnitOfWork>();
        var state = await repo.GetAsync(ct);
        state.RecordPhonePrimaryOnline(clock.UtcNow);
        await repo.UpdateAsync(state, ct);
        await uow.SaveChangesAsync(ct);
    }
}
