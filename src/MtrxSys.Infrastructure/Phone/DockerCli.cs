using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Helpers compartilhados pelos orquestradores de "aparelho virtual" (docker-android e
/// redroid): rodar um processo CLI (docker/adb) com teto próprio de tempo, e o download de APK com
/// limite de bytes. Extraído do <see cref="DockerCliPhoneOrchestrator"/> pra os dois engines
/// reusarem sem duplicar (o do redroid roda tanto `docker` quanto `adb`).</summary>
internal static class DockerCli
{
    // Proxy aceito = host:porta (IP ou hostname + porta). Trava metacaracteres de shell (; $ | espaço…)
    // antes de o valor chegar no `adb shell`, fechando injeção de comando no device.
    public static readonly Regex ProxyHostPort =
        new(@"^[A-Za-z0-9.\-]{1,255}:\d{1,5}$", RegexOptions.Compiled);

    // Teto do download do APK (WhatsApp real ~100 MB). Protege a RAM da API contra uma URL apontando
    // pra arquivo gigante/malicioso.
    public const long MaxApkBytes = 400L * 1024 * 1024;

    // Teto próprio de cada chamada ao CLI, ALÉM do ct do request: um `docker exec`/`adb` travado
    // (emulador enroscado, socket lento) não pode pendurar a requisição — nem sem cliente impaciente.
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A URL do APK é válida pro download? Precisa ser http(s) ABSOLUTA — o instalador baixa
    /// por HTTP (HttpClient.GetAsync), então caminho de arquivo local ou host sem esquema falham com
    /// "invalid request URI". Valida antes pra devolver mensagem clara em vez da exceção crua.</summary>
    public static bool IsValidApkUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Roda `docker` com os argumentos dados.</summary>
    public static Task<(int Code, string Out, string Err)> DockerAsync(
        CancellationToken ct, params string[] args) => RunAsync("docker", ct, args);

    /// <summary>Estado do container via `docker inspect` (+ fallback `docker version` pra distinguir
    /// "container não criado" de "docker inacessível"). Compartilhado pelos dois engines.</summary>
    public static async Task<(string State, bool Running, bool Unavailable)> InspectStatusAsync(
        string containerName, CancellationToken ct)
    {
        var (code, outp, _) = await DockerAsync(ct, "inspect", "-f", "{{.State.Status}}", containerName);
        if (code != 0)
        {
            // inspect falhou: ou o container não existe, ou o docker não está acessível.
            var (vcode, _, _) = await DockerAsync(ct, "version", "--format", "{{.Server.Version}}");
            return (vcode == 0 ? "not_created" : "unavailable", false, true);
        }
        var state = outp.Trim();
        return (state, string.Equals(state, "running", StringComparison.OrdinalIgnoreCase), false);
    }

    /// <summary>Normaliza o valor do http_proxy global do Android: vazio → ":0" (desliga); senão
    /// valida host:porta (trava injeção de shell). Compartilhado pelos dois engines.</summary>
    public static (string? Value, string? Error) NormalizeProxyValue(string? hostPort)
    {
        if (string.IsNullOrWhiteSpace(hostPort))
        {
            return (":0", null);
        }
        var trimmed = hostPort.Trim();
        return ProxyHostPort.IsMatch(trimmed)
            ? (trimmed, null)
            : (null, "Proxy inválido. Use host:porta (ex.: 203.0.113.5:8080) — sem espaços ou símbolos.");
    }

    /// <summary>Baixa o APK pra um arquivo temporário, com TETO de bytes (protege a RAM/disco da API
    /// contra uma URL apontando pra arquivo gigante/malicioso). Retorna (caminho, null) no sucesso ou
    /// (null, mensagem) no erro. O chamador instala e apaga com <see cref="SafeDelete"/> no finally.
    /// Compartilhado pelos dois engines (era copiado nos dois InstallWhatsApp).</summary>
    public static async Task<(string? Path, string? Error)> DownloadApkToTempAsync(
        IHttpClientFactory httpFactory, string url, CancellationToken ct)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wa-{Guid.NewGuid():N}.apk");
        try
        {
            using var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            // ResponseHeadersRead + teto: recusa por Content-Length e corta no limite ao copiar — sem
            // carregar o corpo inteiro em memória.
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is > MaxApkBytes)
            {
                SafeDelete(tmp);
                return (null, $"APK grande demais ({resp.Content.Headers.ContentLength} bytes; teto "
                    + $"{MaxApkBytes / (1024 * 1024)} MB). Verifique a URL em Phone:WhatsAppApkUrl.");
            }
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                await CopyWithLimitAsync(src, dst, MaxApkBytes, ct);
            }
            return (tmp, null);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tmp);
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            SafeDelete(tmp);
            return (null, $"Falha ao baixar o APK: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>Apaga um arquivo best-effort (não lança) — pro finally do install.</summary>
    public static void SafeDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
#pragma warning disable CA1031
        catch { /* best-effort: arquivo já removido/inacessível não pode derrubar o fluxo */ }
#pragma warning restore CA1031
    }

    /// <summary>Roda um CLI (<paramref name="exe"/> = "docker" ou "adb"), lendo stdout/stderr
    /// concorrente (evita deadlock do pipe) com teto de tempo e kill do processo travado.</summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(
        string exe, CancellationToken ct, params string[] args)
    {
        using var timeoutCts = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var lct = linked.Token;
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 🔴 Redirecionar a ENTRADA também, mesmo sem nada pra escrever nela. Sem isto o filho
                // HERDA o stdin do processo pai, e um `adb` herdeiro queima a entrada do console
                // interativo: medido em 2026-07-30, o `mtrx phone console` lia a primeira linha antes
                // da chamada ao adb e recebia null em TODAS as seguintes. Fechar logo após o start
                // entrega EOF a quem tentar ler, que é o comportamento correto pra um CLI que não
                // conversa por stdin.
                RedirectStandardInput = true,
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
                return (-1, string.Empty, $"{exe} CLI indisponível");
            }
            p.StandardInput.Close();
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
            return (-1, string.Empty, $"{exe} excedeu {CommandTimeout.TotalSeconds:0}s (comando travado?).");
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

    /// <summary>Copia stream→arquivo cortando no teto: sem Content-Length confiável (ou mentiroso),
    /// aborta assim que passa do limite em vez de encher o disco/memória. O arquivo parcial é apagado
    /// no finally do chamador.</summary>
    public static async Task CopyWithLimitAsync(Stream src, Stream dst, long maxBytes, CancellationToken ct)
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

    // Mata o processo (e a árvore) que ficou travado no timeout/cancelamento. Best-effort.
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
