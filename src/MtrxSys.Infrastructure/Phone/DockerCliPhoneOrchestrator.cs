using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Controla o "aparelho virtual" (Android em container) via `docker` CLI sobre o socket
/// montado. Tudo é fail-safe: se o docker não estiver acessível (ex.: host sem o socket), os métodos
/// devolvem "unavailable" em vez de estourar — assim a aba "Celular" degrada de forma limpa.
/// Exige um host com /dev/kvm (servidor Linux) pra o Android de fato bootar.</summary>
internal sealed class DockerCliPhoneOrchestrator(IOptions<PhoneOptions> opts, IHttpClientFactory http)
    : IPhoneOrchestrator
{
    private PhoneOptions Opts => opts.Value;

    // Proxy aceito = host:porta (IP ou hostname + porta). Trava metacaracteres de shell (; $ | espaço…)
    // antes de o valor chegar no `adb shell`, fechando injeção de comando no device.
    private static readonly Regex ProxyHostPort = new(@"^[A-Za-z0-9.\-]{1,255}:\d{1,5}$", RegexOptions.Compiled);

    // Teto do download do APK (WhatsApp real ~100 MB). Protege a RAM da API contra uma URL apontando
    // pra arquivo gigante/malicioso.
    private const long MaxApkBytes = 400L * 1024 * 1024;

    public async Task<PhoneStatus> GetStatusAsync(CancellationToken ct)
    {
        var (code, outp, _) = await RunAsync(ct, "inspect", "-f", "{{.State.Status}}", Opts.ContainerName);
        if (code != 0)
        {
            // inspect falhou: ou o container não existe, ou o docker não está acessível. Distingue
            // com um `docker version` (se responde, é só "container não criado").
            var (vcode, _, _) = await RunAsync(ct, "version", "--format", "{{.Server.Version}}");
            return new PhoneStatus(vcode == 0 ? "not_created" : "unavailable", false, null);
        }
        var state = outp.Trim();
        var running = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);
        return new PhoneStatus(state, running, running ? NullIfEmpty(Opts.ViewUrl) : null);
    }

    public async Task<bool> IsBootedAsync(CancellationToken ct)
    {
        // adb só responde depois do Android subir; sys.boot_completed=1 = home pronta pra instalar o APK.
        var (code, outp, _) = await RunAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", "getprop", "sys.boot_completed");
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
            // Cria o container do Android com KVM, noVNC e volume persistente — o equivalente ao
            // serviço `android` do compose, mas disparado pela aba (sem prompt).
            await RunAsync(ct,
                "run", "-d",
                "--name", Opts.ContainerName,
                "--restart", "unless-stopped",
                "--device", "/dev/kvm",
                "-e", $"EMULATOR_DEVICE={Opts.Device}",
                "-e", "WEB_VNC=true",
                // Bind em 127.0.0.1: o Caddy (rede host) alcança via loopback, mas o noVNC NÃO fica
                // exposto direto na internet (furando o portão). Antes era "{porta}:6080" (= 0.0.0.0).
                "-p", $"127.0.0.1:{Opts.NoVncPort}:6080",
                "-v", $"{Opts.VolumeName}:/home/androidusr",
                Opts.Image);
            return await GetStatusAsync(ct);
        }
        // Já existe: só garante ligado.
        return await StartAsync(ct);
    }

    public async Task<PhoneStatus> StartAsync(CancellationToken ct)
    {
        await RunAsync(ct, "start", Opts.ContainerName);
        return await GetStatusAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct) =>
        await RunAsync(ct, "stop", Opts.ContainerName);

    public async Task<string> GetLogsAsync(int tail, CancellationToken ct)
    {
        var n = Math.Clamp(tail, 1, 2000);
        var (_, outp, err) = await RunAsync(ct, "logs", "--tail", n.ToString(), Opts.ContainerName);
        return string.IsNullOrWhiteSpace(outp) ? err : outp;
    }

    public async Task<string> InstallWhatsAppAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Opts.WhatsAppApkUrl))
        {
            return "Defina Phone:WhatsAppApkUrl com a URL do APK do WhatsApp (não há URL oficial " +
                   "estável — use a sua). Alternativa manual: docker cp whatsapp.apk " +
                   $"{Opts.ContainerName}:/tmp/wa.apk && docker exec {Opts.ContainerName} adb install -r /tmp/wa.apk";
        }

        // Baixa o APK aqui (a API tem HttpClient), copia pro container do Android e instala via adb.
        // Mantém o orquestrador "só docker CLI" pro resto; o download é o único desvio (e é confiável).
        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.apk");
            using (var client = http.CreateClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                // Streaming com TETO: GetByteArrayAsync carregava o corpo INTEIRO em memória — uma URL
                // apontando pra arquivo enorme/malicioso estouraria a RAM da API. Aqui lemos os headers
                // primeiro, recusamos por Content-Length, e copiamos pro disco cortando no limite.
                using var resp = await client.GetAsync(
                    Opts.WhatsAppApkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is > MaxApkBytes)
                {
                    return $"APK grande demais ({resp.Content.Headers.ContentLength} bytes; teto "
                        + $"{MaxApkBytes / (1024 * 1024)} MB). Verifique a URL em Phone:WhatsAppApkUrl.";
                }
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await CopyWithLimitAsync(src, dst, MaxApkBytes, ct);
            }

            var (cpCode, _, cpErr) = await RunAsync(ct, "cp", tmp, $"{Opts.ContainerName}:/tmp/wa.apk");
            if (cpCode != 0)
            {
                return $"Falha ao copiar o APK pro container: {cpErr}";
            }

            var (_, outp, err) = await RunAsync(ct,
                "exec", Opts.ContainerName, "adb", "install", "-r", "/tmp/wa.apk");
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
            if (tmp is not null && File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    public async Task<string> SetProxyAsync(string? hostPort, CancellationToken ct)
    {
        // Vazio = limpa o proxy (":0" desliga o http_proxy global do Android).
        string value;
        if (string.IsNullOrWhiteSpace(hostPort))
        {
            value = ":0";
        }
        else
        {
            var trimmed = hostPort.Trim();
            if (!ProxyHostPort.IsMatch(trimmed))
            {
                return "Proxy inválido. Use host:porta (ex.: 203.0.113.5:8080) — sem espaços ou símbolos.";
            }
            value = trimmed;
        }
        var (_, outp, err) = await RunAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", "settings", "put", "global", "http_proxy", value);
        var result = string.IsNullOrWhiteSpace(outp) ? err : outp;
        return string.IsNullOrWhiteSpace(result) ? $"http_proxy = {value}" : result;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Teto próprio de cada chamada ao docker, ALÉM do ct do request: um `docker exec`/`adb` travado
    // (emulador enroscado, socket lento) não pode pendurar a requisição — nem sem cliente impaciente.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private static async Task<(int Code, string Out, string Err)> RunAsync(
        CancellationToken ct, params string[] args)
    {
        using var timeoutCts = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var lct = linked.Token;
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            p = Process.Start(psi);
            if (p is null)
            {
                return (-1, string.Empty, "docker CLI indisponível");
            }
            // Lê stdout E stderr CONCORRENTE: ler um até o fim e só depois o outro trava se o buffer
            // de pipe do SO (~64 KB) do fluxo não-lido encher (deadlock clássico) — ex.: `docker logs`
            // com muito stderr penduraria a leitura do stdout que nunca chega ao EOF.
            var outTask = p.StandardOutput.ReadToEndAsync(lct);
            var errTask = p.StandardError.ReadToEndAsync(lct);
            await p.WaitForExitAsync(lct);
            return (p.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(p);
            throw; // cancelamento REAL do request propaga
        }
        catch (OperationCanceledException)
        {
            // Nosso timeout (não o do request): mata o processo travado e devolve como indisponível —
            // os chamadores já tratam code<0 como "unavailable"/erro, sem pendurar a chamada.
            TryKill(p);
            return (-1, string.Empty, $"docker excedeu {CommandTimeout.TotalSeconds:0}s (comando travado?).");
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            // Sem docker/sem socket: trata como indisponível (a aba mostra "indisponível").
            return (-1, string.Empty, ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            p?.Dispose();
        }
    }

    // Copia stream→arquivo cortando no teto: sem Content-Length confiável (ou mentiroso), aborta assim
    // que passa do limite em vez de encher o disco/memória. O arquivo parcial é apagado no finally do chamador.
    private static async Task CopyWithLimitAsync(Stream src, Stream dst, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException(
                    $"APK excede o teto de {maxBytes / (1024 * 1024)} MB — download abortado.");
            }
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    // Mata o processo docker (e a árvore) que ficou travado no timeout/cancelamento. Best-effort.
    private static void TryKill(Process? p)
    {
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
            }
        }
#pragma warning disable CA1031 // best-effort: matar um processo já morto/inacessível não pode lançar
        catch
        {
            // ignora: o processo pode já ter saído entre o HasExited e o Kill.
        }
#pragma warning restore CA1031
    }
}
