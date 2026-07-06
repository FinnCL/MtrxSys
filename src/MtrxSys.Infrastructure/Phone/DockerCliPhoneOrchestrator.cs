using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Controla o "aparelho virtual" (Android em container, docker-android) via `docker` CLI
/// sobre o socket montado. Tudo é fail-safe: se o docker não estiver acessível (ex.: host sem o
/// socket), os métodos devolvem "unavailable" em vez de estourar — assim a aba "Celular" degrada de
/// forma limpa. Exige um host com /dev/kvm (servidor Linux) pra o Android de fato bootar. Os helpers
/// de processo/download ficam em <see cref="DockerCli"/> (compartilhados com o engine redroid).</summary>
internal sealed class DockerCliPhoneOrchestrator(IOptions<PhoneOptions> opts, IHttpClientFactory http)
    : IPhoneOrchestrator
{
    private PhoneOptions Opts => opts.Value;

    public async Task<PhoneStatus> GetStatusAsync(CancellationToken ct)
    {
        var (state, running, unavailable) = await DockerCli.InspectStatusAsync(Opts.ContainerName, ct);
        if (unavailable)
        {
            return new PhoneStatus(state, false, null); // "not_created" ou "unavailable"
        }
        // docker-android: quando rodando, a tela é o noVNC embutido (ViewUrl).
        return new PhoneStatus(state, running, running ? NullIfEmpty(Opts.ViewUrl) : null);
    }

    public async Task<bool> IsBootedAsync(CancellationToken ct)
    {
        // adb só responde depois do Android subir; sys.boot_completed=1 = home pronta pra instalar o APK.
        var (code, outp, _) = await DockerCli.DockerAsync(ct,
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
            var args = new List<string> { "run", "-d", "--name", Opts.ContainerName };
            // Política de restart: default "no" — o primário fica desligado no regime normal (acordado
            // só no keep-alive), então não deve voltar sozinho após reboot do host.
            if (!string.IsNullOrWhiteSpace(Opts.RestartPolicy))
            {
                args.AddRange(["--restart", Opts.RestartPolicy]);
            }
            // Tetos de recurso: um emulador não rouba o host dos outros 9 (regra pra escalar os 10).
            if (!string.IsNullOrWhiteSpace(Opts.MemoryLimit))
            {
                args.AddRange(["--memory", Opts.MemoryLimit]);
            }
            if (!string.IsNullOrWhiteSpace(Opts.Cpus))
            {
                args.AddRange(["--cpus", Opts.Cpus]);
            }
            args.AddRange([
                "--device", "/dev/kvm",
                "-e", $"EMULATOR_DEVICE={Opts.Device}",
                "-e", "WEB_VNC=true",
                // Bind em 127.0.0.1: o Caddy (rede host) alcança via loopback, mas o noVNC NÃO fica
                // exposto direto na internet (furando o portão). Antes era "{porta}:6080" (= 0.0.0.0).
                "-p", $"127.0.0.1:{Opts.NoVncPort}:6080",
                "-v", $"{Opts.VolumeName}:/home/androidusr",
                Opts.Image,
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
        // APK LOCAL (sem hospedar): se houver um .apk embutido/montado no container do api
        // (default /app/whatsapp.apk, ou o path em PHONE_LOCAL_APK), instala DIRETO dele —
        // docker cp + adb install — sem baixar por URL. É o "embutir o comando": funciona em
        // qualquer ambiente, offline, sem gate/hosting. Só cai no download se NÃO houver local.
        var localApk = System.Environment.GetEnvironmentVariable("PHONE_LOCAL_APK") ?? "/app/whatsapp.apk";
        if (System.IO.File.Exists(localApk))
        {
            try
            {
                var (cpCode, _, cpErr) = await DockerCli.DockerAsync(ct, "cp", localApk, $"{Opts.ContainerName}:/tmp/wa.apk");
                if (cpCode != 0)
                {
                    return $"Falha ao copiar o APK local pro container: {cpErr}";
                }
                var (_, outp, err) = await DockerCli.DockerAsync(ct,
                    "exec", Opts.ContainerName, "adb", "install", "-r", "/tmp/wa.apk");
                return string.IsNullOrWhiteSpace(outp) ? err : outp;
            }
#pragma warning disable CA1031
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"Falha ao instalar o WhatsApp (APK local): {ex.Message}";
            }
#pragma warning restore CA1031
        }

        if (string.IsNullOrWhiteSpace(Opts.WhatsAppApkUrl))
        {
            return "Defina Phone:WhatsAppApkUrl com a URL do APK do WhatsApp (não há URL oficial " +
                   "estável — use a sua). Alternativa manual: docker cp whatsapp.apk " +
                   $"{Opts.ContainerName}:/tmp/wa.apk && docker exec {Opts.ContainerName} adb install -r /tmp/wa.apk";
        }
        // O APK é BAIXADO por HTTP: a URL precisa ser http(s) ABSOLUTA (não caminho de arquivo local
        // nem host sem esquema), senão o GetAsync estoura "invalid request URI". Mensagem clara aqui.
        if (!DockerCli.IsValidApkUrl(Opts.WhatsAppApkUrl))
        {
            return $"Phone:WhatsAppApkUrl inválida (\"{Opts.WhatsAppApkUrl}\") — precisa ser uma URL " +
                   "http(s) ABSOLUTA (ex.: https://seu-host/whatsapp.apk). Um caminho de arquivo local " +
                   "NÃO funciona (o APK é baixado por HTTP): hospede o .apk, ou instale manual via " +
                   $"docker cp + docker exec {Opts.ContainerName} adb install -r /tmp/wa.apk.";
        }

        // Baixa o APK (helper compartilhado, com teto), copia pro container e instala via adb DENTRO
        // dele (o docker-android traz o adb). O download é o único desvio do "só docker CLI".
        var (tmp, downloadErr) = await DockerCli.DownloadApkToTempAsync(http, Opts.WhatsAppApkUrl, ct);
        if (downloadErr is not null)
        {
            return downloadErr;
        }
        try
        {
            var (cpCode, _, cpErr) = await DockerCli.DockerAsync(ct, "cp", tmp!, $"{Opts.ContainerName}:/tmp/wa.apk");
            if (cpCode != 0)
            {
                return $"Falha ao copiar o APK pro container: {cpErr}";
            }

            var (_, outp, err) = await DockerCli.DockerAsync(ct,
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
            DockerCli.SafeDelete(tmp);
        }
    }

    public async Task<string> SendKeyAsync(string key, CancellationToken ct)
    {
        // A tela do emulador está em GESTOS (sem barra ◁○□). O keyevent via adb funciona sempre (não
        // é filtrado fora da tela de registro). Mapeia back/home/recents pros keycodes do Android.
        var code = key switch
        {
            "back" => "4",
            "home" => "3",
            "recents" => "187",
            _ => null,
        };
        if (code is null)
        {
            return $"tecla desconhecida: {key} (use back/home/recents).";
        }
        var (rc, outp, err) = await DockerCli.DockerAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", "input", "keyevent", code);
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    public async Task<string> SendTextAsync(string text, CancellationToken ct)
    {
        // adb input text: espaço vira %s; mantém só caracteres seguros (alfanumérico + - _ . @ +),
        // que cobrem código de pareamento, número e email — e evitam injeção no shell do device.
        var sb = new System.Text.StringBuilder();
        foreach (var c in text ?? "")
        {
            if (c == ' ') sb.Append("%s");
            else if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '+') sb.Append(c);
        }
        var safe = sb.ToString();
        if (safe.Length == 0)
        {
            return "texto vazio ou sem caracteres válidos.";
        }
        var (rc, outp, err) = await DockerCli.DockerAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", "input", "text", safe);
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    public async Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct)
    {
        // Grava o número na AGENDA do Android do emulador (contacts provider) — NÃO na UI do WhatsApp.
        // Objetivo: o disparo sai pra um "contato salvo" (perfil menos-robô, ajuda anti-ban). Chamado
        // pelo DispatchEngine ANTES de cada envio, então é IDEMPOTENTE (não duplica) e best-effort
        // (uma falha aqui nunca pode derrubar o envio — o disparo é o que importa).
        var digits = new string((phoneE164 ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
        {
            return "phone inválido";
        }

        // 1) Já está na agenda? phone_lookup normaliza e casa pelos últimos dígitos ("Row:" = achou).
        var (lc, lookup, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "query",
            "--uri", $"content://com.android.contacts/phone_lookup/{digits}", "--projection", "display_name");
        if (lc == 0 && lookup.Contains("Row:", System.StringComparison.Ordinal))
        {
            return "já existe";
        }

        // 2) Cria o raw contact (conta LOCAL: account_type/name vazios → some com pm clear na troca de chip).
        await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "insert", "--uri", "content://com.android.contacts/raw_contacts",
            "--bind", "account_type:s:", "--bind", "account_name:s:");

        // 3) Pega o _id recém-criado. O disparo é SEQUENCIAL (um envio por vez), então o MAIOR _id é o
        //    nosso — sem corrida. (content insert não devolve o id, daí a leitura.)
        var (_, rows, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "query",
            "--uri", "content://com.android.contacts/raw_contacts", "--projection", "_id");
        var rid = MaxRawContactId(rows);
        if (rid <= 0)
        {
            return "falha ao obter raw_contact_id";
        }

        // 4) Nome (sanitizado: só alfanumérico — espaço/aspas quebrariam no re-split do adb shell) + telefone.
        var safeName = SanitizeContactName(name, digits);
        await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "insert", "--uri", "content://com.android.contacts/data",
            "--bind", $"raw_contact_id:i:{rid}", "--bind", "mimetype:s:vnd.android.cursor.item/name",
            "--bind", $"data1:s:{safeName}");
        var (rc, outp, err) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "insert", "--uri", "content://com.android.contacts/data",
            "--bind", $"raw_contact_id:i:{rid}", "--bind", "mimetype:s:vnd.android.cursor.item/phone_v2",
            "--bind", $"data1:s:{digits}");
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    // Maior _id entre as linhas do content query — o raw contact recém-inserido (disparo sequencial).
    private static int MaxRawContactId(string rows)
    {
        var max = 0;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(rows ?? string.Empty, @"_id=(\d+)"))
        {
            if (int.TryParse(m.Groups[1].Value, out var v) && v > max)
            {
                max = v;
            }
        }
        return max;
    }

    // Nome do contato só com alfanumérico (o valor vai como um arg pro adb shell, que re-divide em
    // espaço; aspas/espaço quebrariam). Vazio → "C" + dígitos, pra nunca gravar contato sem nome.
    private static string SanitizeContactName(string? name, string fallbackDigits)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "C" + fallbackDigits;
        }
        var clean = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? "C" + fallbackDigits : clean;
    }

    public async Task<string> GetWhatsAppNumberAsync(CancellationToken ct)
    {
        // Lê o registration_jid dos prefs do WhatsApp no emulador — o número CANÔNICO da conta
        // (o que o WhatsApp usa de fato). Auto-preenche o Passo 2 e evita digitar o número errado.
        var (_, outp, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", $"grep -ah registration_jid /data/data/{Opts.WhatsAppPackage}/shared_prefs/*.xml 2>/dev/null");
        var m = System.Text.RegularExpressions.Regex.Match(outp ?? "", @"registration_jid[^0-9]*([0-9]{12,13})");
        return m.Success ? m.Groups[1].Value : "";
    }

    public async Task<string> OpenUrlAsync(string url, CancellationToken ct)
    {
        // Abre uma URL via intent VIEW no Android (adb am start) — o "deep link de vínculo por QR":
        // a URL do QR do WAHA (https://wa.me/settings/linked_devices#2@...) abre o diálogo "Deseja
        // conectar um dispositivo?" no WhatsApp do emulador, SEM câmera nem rate limit. O usuário toca
        // "Continuar" na tela. Passa como UMA string ao device shell com aspas simples: o '#' da URL
        // viraria comentário no shell se não citado (truncaria o fragmento com os dados de pareamento).
        if (string.IsNullOrWhiteSpace(url)
            || !url.StartsWith("https://wa.me/", System.StringComparison.Ordinal)
            || url.Contains('\''))
        {
            return "url inválida (esperado https://wa.me/... sem aspas).";
        }
        var (rc, outp, err) = await DockerCli.DockerAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", $"am start -a android.intent.action.VIEW -d '{url}'");
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    public async Task<string> ClearWhatsAppAsync(CancellationToken ct)
    {
        // "Trocar chip": zera os dados do WhatsApp no emulador (pm clear) → volta pra tela de
        // boas-vindas, pronto pra registrar OUTRO número. A conta velha some do APP (não do servidor
        // do WhatsApp). Depois re-lança o app na tela de boas-vindas.
        var (rc, outp, err) = await DockerCli.DockerAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", "pm", "clear", Opts.WhatsAppPackage);
        if (rc != 0)
        {
            return string.IsNullOrWhiteSpace(err) ? outp : err;
        }
        // Pré-concede a CÂMERA: sem isso, o dialog de permissão trava a navegação até "Insira o
        // código" na hora de vincular o WAHA. Concedida, a tela do scanner abre direto e dá pra ir em
        // "Conectar com número de telefone" sem interrupção.
        await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            "pm", "grant", Opts.WhatsAppPackage, "android.permission.CAMERA");
        await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            "monkey", "-p", Opts.WhatsAppPackage, "-c", "android.intent.category.LAUNCHER", "1");
        return "ok";
    }

    public async Task<string> SetProxyAsync(string? hostPort, CancellationToken ct)
    {
        var (value, err) = DockerCli.NormalizeProxyValue(hostPort);
        if (err is not null)
        {
            return err;
        }
        var (_, outp, e) = await DockerCli.DockerAsync(ct,
            "exec", Opts.ContainerName, "adb", "shell", "settings", "put", "global", "http_proxy", value!);
        var result = string.IsNullOrWhiteSpace(outp) ? e : outp;
        return string.IsNullOrWhiteSpace(result) ? $"http_proxy = {value}" : result;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
