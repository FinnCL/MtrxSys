using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Engine "redroid" do aparelho virtual: Android rodando DIRETO no kernel do host Linux
/// (binder/ashmem), SEM QEMU/KVM — ~0.5-1 GB por instância, o que permite os 10 ambientes num
/// servidor só. Provisiona/liga/desliga via `docker` CLI (socket montado) e fala com o Android por
/// `adb` sobre TCP (o redroid NÃO traz adb dentro; a API tem o cliente adb e conecta em
/// {AdbHost}:{AdbPort}, publicado pelo container). A tela NÃO é noVNC (ViewUrl fica null): embute via
/// ws-scrcpy no front (VITE_EMULATOR_KIND=scrcpy). Contrato de State idêntico ao docker-android
/// (unavailable/not_created/exited/running) pra o PhoneKeepAliveService rodar sem mudança. Fail-safe:
/// sem docker acessível, tudo vira "unavailable" e a aba degrada limpo. Ver docs/phone-primary-server.md.</summary>
internal sealed class RedroidPhoneOrchestrator(IOptions<PhoneOptions> opts, IHttpClientFactory http)
    : IPhoneOrchestrator
{
    private PhoneOptions Opts => opts.Value;

    // Serial do device pro adb TCP: o mesmo host:porta em que o redroid publica o adb (5555 interno).
    private string Serial => $"{Opts.AdbHost}:{Opts.AdbPort}";

    public async Task<PhoneStatus> GetStatusAsync(CancellationToken ct)
    {
        var (state, running, unavailable) = await DockerCli.InspectStatusAsync(Opts.ContainerName, ct);
        // ViewUrl sempre null: a tela do redroid vem do ws-scrcpy (front), não de um noVNC embutido.
        return unavailable ? new PhoneStatus(state, false, null) : new PhoneStatus(state, running, null);
    }

    public async Task<bool> IsBootedAsync(CancellationToken ct)
    {
        await AdbConnectAsync(ct);
        // adb só responde depois do Android subir; sys.boot_completed=1 = home pronta pra instalar o APK.
        var (code, outp, _) = await DockerCli.RunAsync("adb", ct,
            "-s", Serial, "shell", "getprop", "sys.boot_completed");
        return code == 0 && outp.Trim() == "1";
    }

    public async Task<PhoneStatus> ProvisionAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (status.State == "unavailable")
        {
            return status; // sem docker: nada a provisionar (a aba mostra "indisponível").
        }
        if (status.State == "not_created")
        {
            // docker run do redroid: SEM /dev/kvm. O binder/ashmem vêm do kernel do host (por isso o
            // --privileged por padrão). Volume em /data (estado do Android/sessão do WhatsApp).
            // adb publicado em 0.0.0.0:AdbPort (NÃO 127.0.0.1): a API roda em container bridged e
            // alcança este container pelo host-gateway (host.docker.internal) — uma porta em 127.0.0.1
            // do host NÃO responde por esse caminho. A exposição fica travada no firewall
            // (deploy/setup-firewall.sh dropa 5555-5564 de fora da rede docker). androidboot.* = cmdline.
            var args = new List<string> { "run", "-itd", "--name", Opts.ContainerName };
            if (Opts.RedroidPrivileged)
            {
                args.Add("--privileged");
            }
            if (!string.IsNullOrWhiteSpace(Opts.RestartPolicy))
            {
                args.AddRange(["--restart", Opts.RestartPolicy]);
            }
            if (!string.IsNullOrWhiteSpace(Opts.MemoryLimit))
            {
                args.AddRange(["--memory", Opts.MemoryLimit]);
            }
            if (!string.IsNullOrWhiteSpace(Opts.Cpus))
            {
                args.AddRange(["--cpus", Opts.Cpus]);
            }
            args.AddRange([
                "-p", $"{Opts.AdbPort}:5555",
                "-v", $"{Opts.VolumeName}:/data",
                Opts.RedroidImage,
                $"androidboot.redroid_width={Opts.Width}",
                $"androidboot.redroid_height={Opts.Height}",
                $"androidboot.redroid_dpi={Opts.Dpi}",
                $"androidboot.redroid_gpu_mode={Opts.GpuMode}",
            ]);
            await DockerCli.DockerAsync(ct, args.ToArray());
            return await GetStatusAsync(ct);
        }
        // Já existe: só garante ligado.
        return await StartAsync(ct);
    }

    public async Task<PhoneStatus> StartAsync(CancellationToken ct)
    {
        await DockerCli.DockerAsync(ct, "start", Opts.ContainerName);
        return await GetStatusAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct) =>
        await DockerCli.DockerAsync(ct, "stop", Opts.ContainerName);

    public async Task<string> GetLogsAsync(int tail, CancellationToken ct)
    {
        var n = Math.Clamp(tail, 1, 2000);
        var (_, outp, err) = await DockerCli.DockerAsync(ct, "logs", "--tail", n.ToString(), Opts.ContainerName);
        return string.IsNullOrWhiteSpace(outp) ? err : outp;
    }

    public async Task<string> InstallWhatsAppAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Opts.WhatsAppApkUrl))
        {
            return "Defina Phone:WhatsAppApkUrl com a URL do APK do WhatsApp (não há URL oficial " +
                   $"estável — use a sua). Alternativa manual: adb -s {Serial} install -r whatsapp.apk";
        }
        // O APK é BAIXADO por HTTP: a URL precisa ser http(s) ABSOLUTA (não caminho de arquivo local
        // nem host sem esquema), senão o GetAsync estoura "invalid request URI". Mensagem clara aqui.
        if (!DockerCli.IsValidApkUrl(Opts.WhatsAppApkUrl))
        {
            return $"Phone:WhatsAppApkUrl inválida (\"{Opts.WhatsAppApkUrl}\") — precisa ser uma URL " +
                   "http(s) ABSOLUTA (ex.: https://seu-host/whatsapp.apk). Um caminho de arquivo local " +
                   $"NÃO funciona (o APK é baixado por HTTP): hospede o .apk, ou instale manual via adb -s {Serial} install -r whatsapp.apk.";
        }

        // Baixa o APK (helper compartilhado, com teto) e instala via adb TCP. O redroid não tem Play
        // Store; o sideload do APK é o caminho (e o único que precisamos pro WhatsApp).
        var (tmp, downloadErr) = await DockerCli.DownloadApkToTempAsync(http, Opts.WhatsAppApkUrl, ct);
        if (downloadErr is not null)
        {
            return downloadErr;
        }
        try
        {
            await AdbConnectAsync(ct);
            // O APK é local à API (mesmo container do adb) — não precisa de `docker cp`, o adb envia.
            var (_, outp, err) = await DockerCli.RunAsync("adb", ct, "-s", Serial, "install", "-r", tmp!);
            return string.IsNullOrWhiteSpace(outp) ? err : outp;
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Falha ao instalar o WhatsApp: {ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            DockerCli.SafeDelete(tmp);
        }
    }

    public async Task<string> SetProxyAsync(string? hostPort, CancellationToken ct)
    {
        var (value, err) = DockerCli.NormalizeProxyValue(hostPort);
        if (err is not null)
        {
            return err;
        }
        await AdbConnectAsync(ct);
        var (_, outp, e) = await DockerCli.RunAsync("adb", ct,
            "-s", Serial, "shell", "settings", "put", "global", "http_proxy", value!);
        var result = string.IsNullOrWhiteSpace(outp) ? e : outp;
        return string.IsNullOrWhiteSpace(result) ? $"http_proxy = {value}" : result;
    }

    // NB: NÃO implementa SendWhatsAppMessageAsync — o redroid tem bug de foco de input (mCurrentFocus=
    // null → nenhum toque/texto chega a lugar nenhum), então o envio pela UI é impossível aqui. Usa o
    // default da interface ("não suportado neste engine"). O engine que envia é o docker-android.

    // Garante a conexão adb-TCP antes de cada operação de device. `adb connect` é idempotente (se já
    // conectado, é no-op); best-effort — se falhar, o comando seguinte devolve o erro real.
    private async Task AdbConnectAsync(CancellationToken ct) =>
        await DockerCli.RunAsync("adb", ct, "connect", Serial);
}
