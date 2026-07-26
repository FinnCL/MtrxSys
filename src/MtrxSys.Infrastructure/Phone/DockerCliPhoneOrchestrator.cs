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
    : IPhoneOrchestrator, IDisposable
{
    // Quanto tempo o WhatsApp pode levar pra reconhecer um contato novo da agenda. MEDIDO em produção
    // (2026-07-23): o Google subiu em ~90s e o espelho `com.whatsapp` apareceu entre ~2,5 e ~7 min.
    // Só depois desta janela a AUSÊNCIA do espelho vira prova de que o número não tem WhatsApp.
    //
    // TEM QUE SER BEM MAIOR que o intervalo com que o motor re-pergunta (DispatchEngine.
    // EmulatorSyncGraceSeconds, hoje 8 min). Os dois já foram IGUAIS: o job voltava exatamente quando
    // a janela vencia, então um espelho um pouco mais lento que a média virava veredito "não tem
    // WhatsApp" — que é TERMINAL (MarkSkipped) — na PRIMEIRA tentativa, sem segunda chance. Com 20
    // min o contato é re-perguntado ~2 vezes antes de qualquer descarte, e a medição de 7 min ganha
    // quase 3x de folga. Se mexer no intervalo do motor, mexa aqui junto.
    private static readonly TimeSpan MirrorSyncGrace = TimeSpan.FromMinutes(20);

    private PhoneOptions Opts => opts.Value;
    // Serializa as operações de UI (o emulador não roda 2 uiautomator dump ao mesmo tempo, e o dump usa
    // um arquivo fixo). O provider é singleton; garante 1 envio por vez por emulador. Ver SendWhatsApp.
    private readonly SemaphoreSlim _uiLock = new(1, 1);
    // 0/1 (Interlocked): já concedeu READ/WRITE_CONTACTS ao WhatsApp nesta instância? Um chip novo /
    // pm clear RESETA as permissões; sem contatos o WhatsApp não monta o espelho da agenda → o disparo
    // pula TODOS os números ("não tem WhatsApp") → 0 envios (provado 2026-07-24). Concede lazy no 1º save.
    private int _contactsGranted;

    public void Dispose() => _uiLock.Dispose();

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

    public async Task<bool> IsEgressProxyUpAsync(CancellationToken ct)
    {
        // MESMA pós-condição do watchdog (emulator-watchdog.sh): gost escutando na :12345 E regra REDIRECT
        // presente no nat OUTPUT, DENTRO do Android. Requer root (build userdebug). Qualquer não-zero
        // (container ausente, adb mudo, sem root, gost/regra fora) → false. Fail-safe: a UI só libera o
        // registro do chip quando isto é true, então o número nunca sai pelo IP do datacenter por engano.
        var (code, _, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            "su 0 iptables -t nat -C OUTPUT -p tcp -j REDIRECT --to-ports 12345 && su 0 ss -ltn 2>/dev/null | grep -q :12345");
        return code == 0;
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
                // Display 404×850 (= aspect-ratio do iframe da aba Celular): a tela preenche sem sobrar
                // faixa lateral com a barra de controle do emulador. Sem isto o budtmo cai no default 500
                // e a tela não fica limpa como a do A. Vale pra TODOS os stacks (todos usam este provision).
                "-e", $"SCREEN_WIDTH={Opts.ScreenWidth}",
                "-e", $"SCREEN_HEIGHT={Opts.ScreenHeight}",
                "-e", $"SCREEN_DEPTH={Opts.ScreenDepth}",
                // Bind em 127.0.0.1: o Caddy (rede host) alcança via loopback, mas o noVNC NÃO fica
                // exposto direto na internet (furando o portão). Antes era "{porta}:6080" (= 0.0.0.0).
                "-p", $"127.0.0.1:{Opts.NoVncPort}:6080",
                "-v", $"{Opts.VolumeName}:/home/androidusr",
            ]);
            // GPU/args extras (default -gpu swangle_indirect): sobrescreve o swiftshader que crashava o
            // qemu ao abrir o WhatsApp. Só entra se não-vazio → paridade com o A sem quebrar quem zera.
            if (!string.IsNullOrWhiteSpace(Opts.EmulatorAdditionalArgs))
            {
                args.AddRange(["-e", $"EMULATOR_ADDITIONAL_ARGS={Opts.EmulatorAdditionalArgs}"]);
            }
            args.Add(Opts.Image);
            await DockerCli.DockerAsync(ct, args.ToArray());
            return await GetStatusAsync(ct);
        }
        // Já existe: só garante ligado.
        return await StartAsync(ct);
    }

    public async Task<bool> IsComposeManagedAsync(CancellationToken ct)
    {
        // Lê o label com.docker.compose.project. Presente/não-vazio = o container veio de um `docker compose`
        // (ex.: o A pelo emulator-a.yml) → o reset por docker-run recriaria errado. `<no value>` (label
        // ausente) e rc!=0 (container inexistente) contam como NÃO-gerenciado.
        var (rc, outp, _) = await DockerCli.DockerAsync(ct,
            "inspect", "-f", "{{index .Config.Labels \"com.docker.compose.project\"}}", Opts.ContainerName);
        var val = outp.Trim();
        return rc == 0 && val.Length > 0 && val != "<no value>";
    }

    public async Task<PhoneStatus> ResetEmulatorAsync(CancellationToken ct)
    {
        // Reset FORTE (aparelho novo): derruba o container e APAGA o volume de dados — agenda, conta
        // Google, WhatsApp e a identidade do device somem TODOS. Depois ProvisionAsync recria do zero
        // (o State vira "not_created"). É o nível 2 do "trocar chip": o pm clear (ClearWhatsAppAsync) só
        // zera o WhatsApp e mantém o resto; aqui o aparelho nasce limpo. Segunda linha — só quando há
        // suspeita de correlação por device (número novo morrendo rápido no mesmo aparelho).
        // -f: remove mesmo rodando. O volume só sai DEPOIS do container (senão está "in use").
        await DockerCli.DockerAsync(ct, "rm", "-f", Opts.ContainerName);
        await DockerCli.DockerAsync(ct, "volume", "rm", "-f", Opts.VolumeName);
        // Também limpa a flag "desligado de propósito", pra o aparelho novo não nascer marcado como off.
        await DockerCli.DockerAsync(ct, "volume", "rm", "-f", $"{Opts.ContainerName}-off");
        return await ProvisionAsync(ct);
    }

    public async Task<PhoneStatus> StartAsync(CancellationToken ct)
    {
        // Remove a flag "desligado DE PROPÓSITO" — o emulator-watchdog volta a religar em crash normalmente.
        await DockerCli.DockerAsync(ct, "volume", "rm", "-f", $"{Opts.ContainerName}-off");
        await DockerCli.DockerAsync(ct, "start", Opts.ContainerName);
        return await GetStatusAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // Marca "desligado DE PROPÓSITO" (flag-volume `<container>-off`) ANTES de parar, pro
        // emulator-watchdog NÃO religar (senão ele trata o Exited como crash e reergue em ~20s).
        // O "Ligar" (StartAsync) remove a flag. Fail-safe: se o docker não responder, não quebra.
        await DockerCli.DockerAsync(ct, "volume", "create", $"{Opts.ContainerName}-off");
        await DockerCli.DockerAsync(ct, "stop", Opts.ContainerName);
    }

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

    public async Task<bool?> IsOnWhatsAppAsync(string phoneE164, CancellationToken ct)
    {
        var digits = new string((phoneE164 ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
        {
            return null;
        }
        // 1) Acha o contato AGREGADO. O phone_lookup do `content query` casa STRING, não número: um
        //    contato gravado como "+55…" NÃO é achado por "55…" e vice-versa (medido no aparelho).
        //    Por isso tenta os dois formatos — nós gravamos sem "+", o WhatsApp escreve com.
        var contactId = await LookupContactIdAsync(digits, ct)
            ?? await LookupContactIdAsync("%2B" + digits, ct);
        if (contactId is null)
        {
            return null; // nem está na agenda: não dá pra afirmar nada (quem chama salva e re-pergunta)
        }
        // 2) O contato agregado tem a marca que o WhatsApp cria pra quem é usuário? A agregação junta
        //    o nosso raw contact e o do WhatsApp sob o MESMO contact_id, mesmo com o "+" diferente —
        //    então a marca aparece aqui independentemente de qual dos dois o lookup achou.
        //    SEM --projection de propósito: a linha crua traz o mimetype E o carimbo de última
        //    atualização de uma vez (o `content query` não devolve projeção de várias colunas).
        var (rc, data, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "query",
            "--uri", $"content://com.android.contacts/contacts/{contactId}/data");
        if (rc != 0)
        {
            return null;
        }
        if (data.Contains("vnd.com.whatsapp.profile", System.StringComparison.Ordinal))
        {
            return true;
        }
        // Sem a marca: só é "NÃO tem WhatsApp" se o contato já teve TEMPO de sincronizar. Um contato
        // recém-salvo fica minutos sem espelho — devolver false aqui faria quem chama descartá-lo
        // como inexistente (o job é marcado terminal), perdendo um contato bom pra sempre.
        var stamp = System.Text.RegularExpressions.Regex.Match(data, @"contact_last_updated_timestamp=(\d+)");
        if (!stamp.Success
            || !long.TryParse(stamp.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            return null;
        }
        // Compara com o relógio DO APARELHO, não com o do host. O carimbo acima é gerado lá dentro; se
        // o emulador atrasar em relação ao host, a diferença INFLA e a gente declararia "não tem
        // WhatsApp" cedo demais — e esse veredito é terminal (o job é descartado). Duas leituras do
        // mesmo relógio nunca divergem. Sem conseguir a hora do device, prefere não afirmar nada.
        var deviceNow = await GetDeviceNowAsync(ct);
        if (deviceNow is null)
        {
            return null;
        }
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return deviceNow.Value - updatedAt > MirrorSyncGrace ? false : null;
    }

    // "Agora" segundo o próprio Android (epoch em segundos). null se não der pra ler.
    private async Task<DateTimeOffset?> GetDeviceNowAsync(CancellationToken ct)
    {
        var (rc, outp, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "date", "+%s");
        if (rc != 0)
        {
            return null;
        }
        var m = System.Text.RegularExpressions.Regex.Match(outp ?? "", @"\d{10,}");
        return m.Success
            && long.TryParse(m.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : null;
    }

    // contact_id do primeiro resultado do phone_lookup, ou null. `query` já vem no formato "Row: 0 col=valor".
    private async Task<string?> LookupContactIdAsync(string lookupValue, CancellationToken ct)
    {
        var (rc, outp, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "query",
            "--uri", $"content://com.android.contacts/phone_lookup/{lookupValue}",
            "--projection", "contact_id");
        if (rc != 0)
        {
            return null;
        }
        var m = System.Text.RegularExpressions.Regex.Match(outp ?? "", @"contact_id=(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Concede READ/WRITE_CONTACTS ao WhatsApp (idempotente). Sem isso ele não lê a agenda do emulador →
    // espelho vazio → todo número cai como "não tem WhatsApp". Retorna true se ambos os grants deram OK.
    private async Task<bool> GrantContactsPermissionAsync(CancellationToken ct)
    {
        var (r1, _, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            "pm", "grant", Opts.WhatsAppPackage, "android.permission.READ_CONTACTS");
        var (r2, _, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            "pm", "grant", Opts.WhatsAppPackage, "android.permission.WRITE_CONTACTS");
        return r1 == 0 && r2 == 0;
    }

    public async Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct)
    {
        // Garante o READ/WRITE_CONTACTS do WhatsApp na 1ª vez (retenta até conseguir, depois cacheia via
        // flag). Sem isso o WhatsApp não constrói o espelho da agenda e o disparo pula todos os números.
        if (Volatile.Read(ref _contactsGranted) == 0 && await GrantContactsPermissionAsync(ct))
        {
            Interlocked.Exchange(ref _contactsGranted, 1);
        }

        // Grava o número na AGENDA do Android do emulador (contacts provider) — NÃO na UI do WhatsApp.
        // Objetivo: o disparo sai pra um "contato salvo" (perfil menos-robô, ajuda anti-ban). Chamado
        // pelo DispatchEngine ANTES de cada envio, então é IDEMPOTENTE (não duplica) e best-effort
        // (uma falha aqui nunca pode derrubar o envio — o disparo é o que importa).
        var digits = new string((phoneE164 ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
        {
            return "phone inválido";
        }

        // 1) Já está na agenda? Testa os DOIS formatos: este phone_lookup casa STRING, não número —
        //    um contato gravado pelo WhatsApp como "+55…" não é achado por "55…". Com um formato só,
        //    o "já existe" falhava pra esses e criávamos uma DUPLICATA a cada disparo.
        if (await LookupContactIdAsync(digits, ct) is not null
            || await LookupContactIdAsync("%2B" + digits, ct) is not null)
        {
            return "já existe";
        }

        // 2) Cria o raw contact. Conta VAZIA de propósito: num aparelho COM conta Google o Android
        //    atribui o contato à conta PADRÃO sozinho (medido: o registro nasce em com.google e
        //    sincroniza); sem conta nenhuma ele fica local e some no pm clear da troca de chip.
        //    O rc É verificado: sem isso, uma falha de infra (container errado, socket ausente, adb
        //    fora) seguia adiante e só aparecia no passo 3 como "falha ao obter raw_contact_id" —
        //    sintoma que aponta pro lugar errado e custou uma hora de diagnóstico em produção.
        var (ic, io, ie) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "insert", "--uri", "content://com.android.contacts/raw_contacts",
            "--bind", "account_type:s:", "--bind", "account_name:s:");
        if (ic != 0)
        {
            return $"não criei o contato: {Detail(io, ie)}";
        }

        // 3) Pega o _id recém-criado. O disparo é SEQUENCIAL (um envio por vez), então o MAIOR _id é o
        //    nosso — sem corrida. (content insert não devolve o id, daí a leitura.)
        var (qc, rows, qe) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "query",
            "--uri", "content://com.android.contacts/raw_contacts", "--projection", "_id");
        if (qc != 0)
        {
            return $"não consegui ler a agenda: {Detail(rows, qe)}";
        }
        var rid = MaxRawContactId(rows);
        if (rid <= 0)
        {
            // A agenda respondeu, mas sem nenhum _id: aí sim é o dado que está estranho, não a infra.
            return "a agenda respondeu sem nenhum _id";
        }

        // 4) Nome (sanitizado: só alfanumérico — espaço/aspas quebrariam no re-split do adb shell) + telefone.
        //    O nome falhar deixaria um contato SEM NOME mas com telefone (ainda serve pro WhatsApp);
        //    o TELEFONE falhar deixa um contato ÓRFÃO, inútil e invisível na agenda. Por isso os dois
        //    são verificados, e o do telefone diz explicitamente que sobrou lixo.
        var safeName = SanitizeContactName(name, digits);
        var (nc, no, ne) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "insert", "--uri", "content://com.android.contacts/data",
            "--bind", $"raw_contact_id:i:{rid}", "--bind", "mimetype:s:vnd.android.cursor.item/name",
            "--bind", $"data1:s:{safeName}");
        if (nc != 0)
        {
            return $"não gravei o nome do contato {rid}: {Detail(no, ne)}";
        }
        var (rc, outp, err) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "content", "insert", "--uri", "content://com.android.contacts/data",
            "--bind", $"raw_contact_id:i:{rid}", "--bind", "mimetype:s:vnd.android.cursor.item/phone_v2",
            "--bind", $"data1:s:{digits}");
        return rc == 0
            ? "ok"
            : $"não gravei o telefone (contato {rid} ficou órfão na agenda): {Detail(outp, err)}";
    }

    // Erro do CLI em uma linha: prefere o stderr; cai no stdout quando ele vem vazio (o `content`
    // do Android imprime a mensagem de uso no stdout). Trunca porque isso vai pro log a cada job.
    private static string Detail(string? outp, string? err)
    {
        var raw = string.IsNullOrWhiteSpace(err) ? outp : err;
        var flat = (raw ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length switch
        {
            0 => "(sem detalhe)",
            > 200 => flat[..200] + "…",
            _ => flat,
        };
    }

    public async Task<WhatsAppSendResult> SendWhatsAppMessageAsync(string phoneE164, string text, CancellationToken ct)
    {
        // Caminho A anti-463: envia pela UI do WhatsApp DO EMULADOR (o primário), não pelo WAHA.
        var digits = new string((phoneE164 ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8)
        {
            return WhatsAppSendResult.Fail("phone inválido");
        }
        var url = WhatsAppUi.DeepLink(digits, text);
        if (url.Contains('\'', System.StringComparison.Ordinal))
        {
            return WhatsAppSendResult.Fail("texto gerou aspa simples (não esperado no URL-encode)."); // anti-injeção
        }
        // Um envio por vez por emulador (uiautomator dump não roda concorrente e usa arquivo fixo).
        await _uiLock.WaitAsync(ct);
        try
        {
            // Teto TOTAL do envio: cada adb já tem 60s, mas um envio faz várias chamadas — sem um teto
            // total, adb travados nos polls segurariam este lock (e a fila) por minutos. sct aborta tudo.
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, Opts.WhatsAppSendTimeoutSeconds)));
            var sct = sendCts.Token;
            // 1) Abre o chat com a mensagem já preenchida (click-to-chat). Aspas simples: o '&'/'#' da URL
            //    não podem ser interpretados pelo shell do device.
            var (rc, outp, err) = await DockerCli.DockerAsync(sct, "exec", Opts.ContainerName,
                "adb", "shell", $"am start -a android.intent.action.VIEW -d '{url}'");
            if (rc != 0)
            {
                return WhatsAppSendResult.Fail(string.IsNullOrWhiteSpace(err) ? outp : err);
            }
            // 2) POLL o botão ENVIAR aparecer (id/send surge quando há texto no campo) — não um sleep fixo:
            //    chat lento (cold start / proxy) abriria depois dos 4s e o envio seria abortado à toa.
            var send = await PollNodeCenterAsync("com.whatsapp:id/send", Opts.WhatsAppOpenWaitMs, sct);
            if (send is null)
            {
                return WhatsAppSendResult.Fail("botão enviar não apareceu (o chat não abriu ou o texto não preencheu).");
            }
            // 3) Toca enviar.
            var (tc, to, te) = await DockerCli.DockerAsync(sct, "exec", Opts.ContainerName,
                "adb", "shell", "input", "tap",
                send.Value.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
                send.Value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (tc != 0)
            {
                return WhatsAppSendResult.Fail(string.IsNullOrWhiteSpace(te) ? to : te);
            }
            // 4) CONFIRMA o envio: o campo de texto ESVAZIA quando a msg sai (correlação confiável — se o
            //    tap errou o botão, o texto fica no campo e sabemos que NÃO enviou).
            if (!await PollEntryClearedAsync(Opts.WhatsAppSendWaitMs, sct))
            {
                return WhatsAppSendResult.Fail("toquei enviar mas o campo não esvaziou — envio não confirmado.");
            }
            // 5) Status de ENTREGA (normalizado sent/delivered/read, locale-independente) — best-effort.
            return WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Estourou o teto TOTAL (não foi cancelamento do chamador): emulador lento/travado.
            return WhatsAppSendResult.Fail("envio excedeu o tempo total (emulador lento/travado?).");
        }
        finally
        {
            _uiLock.Release();
        }
    }

    // Dump da árvore de UI do WhatsApp (uiautomator) → o XML. null se falhar. Arquivo fixo é seguro:
    // as operações de UI são serializadas por _uiLock.
    private async Task<string?> DumpUiAsync(CancellationToken ct)
    {
        await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "uiautomator", "dump", "/sdcard/mtrx_ui.xml");
        var (rc, xml, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName,
            "adb", "shell", "cat", "/sdcard/mtrx_ui.xml");
        return rc == 0 && !string.IsNullOrWhiteSpace(xml) ? xml : null;
    }

    // Dump + centro (x,y) do ÚLTIMO nó com este resource-id, repetindo até timeoutMs (não um sleep fixo).
    private async Task<(int X, int Y)?> PollNodeCenterAsync(string resourceId, int timeoutMs, CancellationToken ct)
    {
        var attempts = Math.Max(1, timeoutMs / 700);
        for (var i = 0; i <= attempts; i++)
        {
            var xml = await DumpUiAsync(ct);
            var center = xml is null ? null : FindNodeCenter(xml, resourceId);
            if (center is not null)
            {
                return center;
            }
            if (i < attempts)
            {
                await Task.Delay(700, ct);
            }
        }
        return null;
    }

    // Centro (x,y) do ÚLTIMO nó com este resource-id no XML (resource-id vem antes de bounds no nó).
    private static (int X, int Y)? FindNodeCenter(string xml, string resourceId)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            "resource-id=\"" + System.Text.RegularExpressions.Regex.Escape(resourceId)
            + "\"[^>]*?bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"");
        var m = rx.Matches(xml).Cast<System.Text.RegularExpressions.Match>().LastOrDefault();
        if (m is null)
        {
            return null;
        }
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var x1 = int.Parse(m.Groups[1].Value, ic);
        var y1 = int.Parse(m.Groups[2].Value, ic);
        var x2 = int.Parse(m.Groups[3].Value, ic);
        var y2 = int.Parse(m.Groups[4].Value, ic);
        return ((x1 + x2) / 2, (y1 + y2) / 2);
    }

    // O campo de texto (id/entry) esvaziou = a msg saiu. Repete até timeoutMs.
    private async Task<bool> PollEntryClearedAsync(int timeoutMs, CancellationToken ct)
    {
        var rx = new System.Text.RegularExpressions.Regex("com.whatsapp:id/entry\"[^>]*?text=\"([^\"]*)\"");
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            var xml = await DumpUiAsync(ct);
            if (xml is not null)
            {
                var m = rx.Match(xml);
                if (!m.Success || string.IsNullOrEmpty(m.Groups[1].Value)) // sem campo (fechou) ou vazio = enviou
                {
                    return true;
                }
            }
            if (i < attempts)
            {
                await Task.Delay(500, ct);
            }
        }
        return false;
    }

    // content-desc do ÚLTIMO id/status, NORMALIZADO (locale-independente): sent/delivered/read/null.
    private async Task<string?> ReadLastMessageStatusAsync(CancellationToken ct)
    {
        var xml = await DumpUiAsync(ct);
        if (xml is null)
        {
            return null;
        }
        var rx = new System.Text.RegularExpressions.Regex(
            "resource-id=\"com.whatsapp:id/status\"[^>]*?content-desc=\"([^\"]*)\"");
        var raw = rx.Matches(xml).Cast<System.Text.RegularExpressions.Match>().LastOrDefault()?.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var r = raw.ToLowerInvariant();
        if (r.Contains("entreg") || r.Contains("deliver")) return "delivered";
        if (r.Contains("lida") || r.Contains("lido") || r.Contains("read")) return "read";
        if (r.Contains("enviad") || r.Contains("sent")) return "sent";
        return null; // desconhecido → não inventa entrega
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

    // ── Cache curto do dump das shared_prefs do WhatsApp ──────────────────────────────────────────
    // Três consumidores leem os MESMOS prefs: o dispatcher (reconcile do aquecimento, a cada ciclo), o
    // selo da aba Celular (poll) e o estado que decide o botão de recuperação. Sem cache, cada aba
    // aberta multiplica `docker exec ... adb shell` — e o adb do emulador é recurso DISPUTADO: é o mesmo
    // canal que o disparo usa pra enviar pela UI (uiautomator). Latência a mais ali significa envio mais
    // lento e, no limite, timeout no meio de um envio.
    //
    // Single-flight: quem chega durante uma leitura em curso espera e aproveita o resultado, em vez de
    // abrir um segundo `docker exec`. TTL curto (15s) porque isto muda raro — e as duas operações que
    // MUDAM o estado (pm clear e pedido de aparelho novo) invalidam na hora, então a UI nunca mostra
    // "registrado" depois de um clique que zerou a conta.
    private const long PrefsTtlMs = 15_000;
    private readonly SemaphoreSlim _prefsGate = new(1, 1);
    private string? _prefsCache;
    private long _prefsCachedAtTicks;

    private void InvalidatePrefsCache() => Volatile.Write(ref _prefsCache, null);

    private bool PrefsCacheFresh(out string? cached)
    {
        cached = Volatile.Read(ref _prefsCache);
        return cached is not null && Environment.TickCount64 - Volatile.Read(ref _prefsCachedAtTicks) < PrefsTtlMs;
    }

    private async Task<string> ReadWhatsAppPrefsAsync(CancellationToken ct)
    {
        if (PrefsCacheFresh(out var hit)) { return hit!; }

        await _prefsGate.WaitAsync(ct);
        try
        {
            if (PrefsCacheFresh(out var second)) { return second!; }

            // UMA ida ao device pra tudo. A sentinela sai ANTES do grep: se o adb não responder, ela não
            // aparece e o Parse devolve "unknown" (fail-safe) em vez de "nunca registrou".
            var (_, outp, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
                $"echo {WhatsAppAccountState.AdbSentinel}; grep -ah -E "
                + $"'registration_jid|saved_user_before_logout|previously_logged_out_from_primary' "
                + $"/data/data/{Opts.WhatsAppPackage}/shared_prefs/*.xml 2>/dev/null");
            var text = outp ?? "";
            Volatile.Write(ref _prefsCachedAtTicks, Environment.TickCount64);
            Volatile.Write(ref _prefsCache, text);
            return text;
        }
        finally
        {
            _prefsGate.Release();
        }
    }

    public async Task<string> GetWhatsAppNumberAsync(CancellationToken ct)
    {
        // O número CANÔNICO da conta (registration_jid) — auto-preenche o Passo 2 e é a fonte da verdade
        // do reconcile do aquecimento no modo Emulador. Delega pro estado pra não haver DOIS parsers do
        // mesmo arquivo: divergirem significaria o dispatcher e a UI discordarem sobre qual chip está
        // logado, que é exatamente o tipo de furo anti-ban que já custou caro aqui.
        var st = await GetWhatsAppAccountStateAsync(ct);
        return st.State == "registered" ? st.Phone ?? "" : "";
    }

    // As chaves de logout são escritas pelo PRÓPRIO WhatsApp quando o servidor derruba a conta (medidas
    // em 2026-07-25 no chip 557191071879) e sobrevivem ao logout — é o que distingue "o servidor tirou"
    // de "deram pm clear aqui". A interpretação é FUNÇÃO PURA (WhatsAppAccountState.Parse) e tem teste;
    // aqui fica só o I/O.
    public async Task<WhatsAppAccountState> GetWhatsAppAccountStateAsync(CancellationToken ct) =>
        WhatsAppAccountState.Parse(await ReadWhatsAppPrefsAsync(ct));

    // ── Leitura do banco do WhatsApp DENTRO do aparelho (substitui o WAHA no "ouvir" e no "listar") ──
    // O emulador é o DONO da conta, então grupos e mensagens já estão no banco dele. Ler daqui elimina a
    // necessidade de vincular um aparelho conectado (companion) ao chip só pra conseguir importar grupos
    // ou receber mensagens — um vínculo a menos pendurado numa conta que precisa durar.
    //
    // Três decisões que valem explicação:
    //  • SNAPSHOT antes de ler: o banco está em modo WAL e o app escreve nele o tempo todo. Ler o arquivo
    //    vivo devolveria dados truncados ou erro de lock. Copiamos db + -wal + -shm (os TRÊS: sem o -wal
    //    as escritas recentes, que são justamente as mensagens novas, não apareceriam).
    //  • Saída JSON (`sqlite3 -json`): texto de mensagem contém `|`, aspas e quebras de linha. Qualquer
    //    parsing por delimitador quebraria no primeiro contato com uma mensagem real.
    //  • `message._id` como marco incremental: é crescente e estável. `timestamp` empata entre mensagens
    //    do mesmo segundo e `key_id` é opaco — nenhum dos dois serve pra "me dá o que veio depois".
    private const string DbSnapshotDir = "/data/local/tmp/mtrx-wadb";
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private string _dbFingerprint = "";

    /// <summary>Garante um snapshot ATUAL, copiando só quando o banco de fato mudou.</summary>
    /// <remarks>
    /// Copiar a cada leitura não escala: o msgstore cresce pra centenas de MB com uso real, e um poller
    /// de mensagens ficaria movendo isso continuamente DENTRO do Android emulado — disputando I/O com o
    /// próprio WhatsApp e com o adb que o disparo usa pra enviar. Um `stat` (mtime:tamanho do .db e do
    /// -wal) custa uma fração disso e diz se a cópia anterior ainda serve. O -wal é o que muda a cada
    /// mensagem nova, então ele é o sinal sensível.
    /// Single-flight: leituras simultâneas compartilham uma cópia em vez de disputarem o mesmo destino.
    /// </remarks>
    private async Task<bool> EnsureDbSnapshotAsync(CancellationToken ct)
    {
        var dbDir = $"/data/data/{Opts.WhatsAppPackage}/databases";
        var (statRc, statOut, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            $"su 0 stat -c %Y:%s {dbDir}/msgstore.db {dbDir}/msgstore.db-wal");
        var fingerprint = statRc == 0 ? (statOut ?? "").Replace("\r", "").Replace("\n", " ").Trim() : "";
        if (fingerprint.Length == 0)
        {
            return false; // adb mudo / sem root / app sem banco — o chamador devolve vazio
        }

        await _dbGate.WaitAsync(ct);
        try
        {
            if (fingerprint == _dbFingerprint)
            {
                return true; // nada mudou desde a última cópia
            }
            var (rc, _, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
                $"su 0 sh -c 'rm -rf {DbSnapshotDir}; mkdir -p {DbSnapshotDir}; cp {dbDir}/msgstore.db* {DbSnapshotDir}/'");
            if (rc != 0)
            {
                _dbFingerprint = ""; // não marque como válido o que não foi copiado
                return false;
            }
            _dbFingerprint = fingerprint;
            return true;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task<string> QueryWhatsAppDbAsync(string sql, CancellationToken ct)
    {
        if (!await EnsureDbSnapshotAsync(ct))
        {
            return "";
        }

        var (rc, outp, _) = await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            $"su 0 sqlite3 -json {DbSnapshotDir}/msgstore.db \"{sql}\"");
        if (rc != 0)
        {
            // ESTADO PRESO evitado: o fingerprint diz "a cópia anterior serve", mas se ela sumiu (limpeza
            // de /data/local/tmp, recreate do container, disco cheio) a consulta falharia a cada chamada
            // até o banco mudar sozinho — o que num aparelho parado pode levar horas. Invalidar aqui faz
            // a próxima chamada recopiar.
            _dbFingerprint = "";
            return "";
        }
        // O `adb shell` traduz LF em CRLF na saída. Aqui os \r só caem ENTRE os elementos do JSON (texto
        // com quebra de linha vem escapado pelo sqlite como \\n), então são espaço em branco inofensivo —
        // mas tirá-los deixa o payload limpo e imune a um parser menos tolerante no futuro.
        // sqlite não imprime NADA quando o resultado é vazio: ausência de linhas, não erro.
        return (outp ?? "").Replace("\r", "").Trim();
    }

    // Instância única: as opções são imutáveis aqui e o serializador guarda metadados de tipo em cache
    // por instância — criar uma nova a cada leitura jogaria esse cache fora e re-refletiria os records
    // toda vez (é o que o CA1869 aponta). Isto roda a cada poll de mensagens, então importa.
    private static readonly System.Text.Json.JsonSerializerOptions DbJson =
        new() { PropertyNameCaseInsensitive = true };

    private static List<T> ParseRows<T>(string json)
    {
        if (json.Length == 0 || json[0] != '[')
        {
            return [];
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<T>>(json, DbJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // Banco meio-copiado ou schema diferente numa versão futura do app: devolver vazio deixa o
            // chamador tratar como "não há nada agora" em vez de derrubar o disparo por causa de leitura.
            return [];
        }
    }

    public async Task<IReadOnlyList<PhoneGroup>> ListGroupsAsync(CancellationToken ct)
    {
        // Grupo = jid com server 'g.us'. O `subject` mora no chat, não no jid.
        const string Sql =
            "select j.user || '@' || j.server as jid, c.subject as subject, "
            + "coalesce(c.created_timestamp, 0) as createdTimestamp, "
            + "(select count(*) from group_participant_user gp where gp.group_jid_row_id = j._id) "
            + "as participantsCount "
            + "from chat c join jid j on j._id = c.jid_row_id "
            + "where j.server = 'g.us' order by c.sort_timestamp desc";
        return ParseRows<PhoneGroup>(await QueryWhatsAppDbAsync(Sql, ct));
    }

    // ÚNICO ponto onde entrada do usuário chega ao SQL: o `groupJid` vem da rota `/api/groups/{groupId}`.
    // Em vez de escapar aspas (fácil de errar em camadas shell→sqlite), aceita SÓ o formato canônico e
    // recusa o resto. Lista de permissão é mais segura que lista de proibição quando o valor tem forma
    // conhecida — e jid tem.
    private static readonly System.Text.RegularExpressions.Regex SafeJidRx =
        new(@"^[0-9][0-9\-]{0,31}@(g\.us|s\.whatsapp\.net|lid)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<PhoneGroupMember>> ListGroupParticipantsAsync(
        string groupJid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupJid) || !SafeJidRx.IsMatch(groupJid))
        {
            return [];
        }

        // O participante quase sempre é `@lid` (identificador opaco que o WhatsApp usa no lugar do
        // número). O próprio app mantém `jid_map` ligando lid → jid real — medido no aparelho em
        // 2026-07-25: 115 mapeamentos, ex. 84972371214521@lid → 553884108545. O `coalesce` cobre os dois
        // casos: participante já em `s.whatsapp.net` usa o próprio user; em `@lid`, usa o resolvido.
        // SEM essa resolução o import gravaria o lid como se fosse telefone — número inexistente, que é
        // o padrão de lista suja ligado a ban aqui.
        // `pending = 0`: convidado que nunca entrou não é membro e não deve virar contato.
        //
        // ⚠️ DESCARTA LID NÃO RESOLVIDO. Nem todo @lid tem entrada no `jid_map` — medido no aparelho:
        // entre três participantes, um voltou como `107752827420703`, que NÃO é telefone, é o próprio lid.
        // Sem o filtro abaixo, o `coalesce` entregaria isso como número e o import criaria contatos
        // inexistentes; disparar pra número que não existe é justamente o padrão de lista suja associado
        // a ban aqui. Preferimos PERDER um participante a inventar um número: o descarte é silencioso do
        // ponto de vista do contato, mas o envio pra número inválido é ruidoso do ponto de vista do chip.
        var sql =
            "select coalesce(pj.user, uj.user) as phone, "
            + "case when gp.rank > 0 then 1 else 0 end as isAdmin "
            + "from group_participant_user gp "
            + "join jid gj on gj._id = gp.group_jid_row_id "
            + "join jid uj on uj._id = gp.user_jid_row_id "
            + "left join jid_map m on m.lid_row_id = uj._id "
            + "left join jid pj on pj._id = m.jid_row_id "
            + $"where gj.user || '@' || gj.server = '{groupJid}' and coalesce(gp.pending, 0) = 0 "
            + "and (pj.user is not null or uj.server = 's.whatsapp.net')";
        return ParseRows<PhoneGroupMember>(await QueryWhatsAppDbAsync(sql, ct));
    }

    // Mesmos filtros da leitura incremental (from_me e a sentinela do chat_row_id) — se divergissem, o
    // marco poderia ser posicionado num id que a leitura nunca devolveria, e o poller ficaria parado num
    // ponto inalcançável.
    private sealed record MaxRow(long MaxRowId);

    public async Task<long> GetLastInboundRowIdAsync(CancellationToken ct)
    {
        const string Sql =
            "select coalesce(max(m._id), 0) as maxRowId from message m "
            + "where m.from_me = 0 and m.chat_row_id > 0";
        var rows = ParseRows<MaxRow>(await QueryWhatsAppDbAsync(Sql, ct));
        return rows.Count > 0 ? rows[0].MaxRowId : 0;
    }

    public async Task<IReadOnlyList<PhoneInboundMessage>> ReadInboundMessagesAsync(
        long afterRowId, int limit, CancellationToken ct)
    {
        // from_me=0 → só o que CHEGOU. Ordem crescente por _id pra o chamador poder avançar o marco de
        // forma segura mesmo se processar em lotes. Sem interpolação de texto: os dois parâmetros são
        // numéricos e passam por Math.Clamp, então não há caminho de injeção.
        //
        // ⚠️ O JOIN é INTERNO DE PROPÓSITO e o `chat_row_id > 0` é redundante com ele — os dois existem
        // pra filtrar a LINHA-SENTINELA que o WhatsApp cria no banco: `_id=1, chat_row_id=-1, from_me=0`
        // com message_type, timestamp e text_data todos NULL (verificado no aparelho em 2026-07-25).
        // Ela conta como "recebida" num `count(*)` ingênuo e viraria uma mensagem fantasma no Chat.
        // NÃO troque por `left join` "pra não perder nada": o que se perderia é exatamente o lixo.
        var take = Math.Clamp(limit, 1, 500);
        var after = Math.Max(0, afterRowId);
        // `sender_jid_row_id` só é preenchido em GRUPO (em 1:1 o remetente é a própria conversa), por isso
        // o coalesce: quem consome recebe sempre um remetente válido, sem ter que adivinhar.
        //
        // ⚠️ RESOLVE @lid → TELEFONE nos dois lados. Medido com mensagem REAL em 2026-07-25: o WhatsApp
        // guarda a conversa 1:1 como `91672436301905@lid`, não como o número. Sem resolver, todo opt-out e
        // toda marcação de "respondeu" seriam atribuídos a um identificador opaco — a pessoa de verdade
        // (5511921404487) nunca seria encontrada, e continuaria recebendo depois de pedir pra sair.
        //
        // DIFERENÇA PROPOSITAL para o import de participantes: lá, lid não resolvido é DESCARTADO (criar
        // contato com número inventado é dano permanente). Aqui ele CAI PRO BRUTO como último recurso —
        // descartar uma mensagem recebida perderia informação que já existe, e o não-casamento com um
        // contato é o mesmo comportamento que o sistema já tem para remetente desconhecido.
        // Importar CRIA; ouvir OBSERVA. Inventar dado é pior que observar algo sem par.
        var sql =
            "select m._id as rowId, "
            + "coalesce(cp.user, j.user) || '@' || coalesce(cp.server, j.server) as chatJid, "
            + "coalesce(sp.user, sj.user, cp.user, j.user) || '@' || "
            + "coalesce(sp.server, sj.server, cp.server, j.server) as senderJid, "
            + "coalesce(m.timestamp, 0) as timestamp, m.text_data as text, "
            + "coalesce(m.message_type, -1) as messageType "
            + "from message m join chat c on c._id = m.chat_row_id join jid j on j._id = c.jid_row_id "
            + "left join jid_map cm on cm.lid_row_id = j._id left join jid cp on cp._id = cm.jid_row_id "
            + "left join jid sj on sj._id = m.sender_jid_row_id "
            + "left join jid_map sm on sm.lid_row_id = sj._id left join jid sp on sp._id = sm.jid_row_id "
            + $"where m.from_me = 0 and m.chat_row_id > 0 and m._id > {after} order by m._id limit {take}";
        return ParseRows<PhoneInboundMessage>(await QueryWhatsAppDbAsync(sql, ct));
    }

    // Nome da imagem-ouro. Fixo de propósito: quem constrói (deploy/build-golden-image-a.sh) e quem
    // consome (deploy/emulator-watchdog.sh) usam a mesma string literal, e transformá-la em config daria
    // três lugares pra divergir num caminho destrutivo.
    private const string GoldenImage = "mtrx-android:golden";

    public async Task<bool> IsGoldenImageReadyAsync(CancellationToken ct)
    {
        var (rc, _, _) = await DockerCli.DockerAsync(ct, "image", "inspect", GoldenImage);
        return rc == 0;
    }

    public async Task<string> RequestCleanDeviceAsync(CancellationToken ct)
    {
        InvalidatePrefsCache();
        // Só GRAVA O PEDIDO (flag-volume). Quem executa é o emulator-watchdog no host — ver a nota do
        // contrato: recriar daqui por docker-run montaria o container errado. O watchdog consome o flag,
        // faz `mtrx-android:golden -> :live` e recria PELO COMPOSE, com rede/porta/entrypoint certos.
        var flag = $"{Opts.ContainerName}-clean-request";
        var (rc, _, err) = await DockerCli.DockerAsync(ct, "volume", "create", flag);
        if (rc != 0)
        {
            return $"não consegui registrar o pedido de limpeza ({flag}): {err}";
        }
        return "Pedido registrado. O watchdog vai recriar o aparelho a partir da imagem-ouro em até ~20s; "
            + "o boot leva ~2-3 min. Espere o selo \"Proxy do emulador: OK\" antes de registrar o chip.";
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
        // Invalida o cache dos prefs: sem isto a UI seguiria mostrando "registrado" por até 15s DEPOIS
        // do clique que zerou a conta — e o dispatcher leria o número velho no próximo ciclo.
        InvalidatePrefsCache();
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
        // Contatos também: sem READ_CONTACTS o WhatsApp não monta o espelho da agenda → o disparo pula
        // todos ("não tem WhatsApp") → 0 envios. Concedido ANTES do relaunch pra o sync já rodar com a
        // permissão; marca a flag pra o SaveContactAsync não repetir.
        if (await GrantContactsPermissionAsync(ct))
        {
            Interlocked.Exchange(ref _contactsGranted, 1);
        }
        // GET_ACCOUNTS: sem ela o WhatsApp NÃO ENXERGA a conta Google do aparelho e o registro TRAVA
        // para sempre na tela "procurando backup no Google Drive"
        // (`RestoreFromBackupActivity` + `gdrive_looking_for_backup_progress_bar`), sem erro no logcat
        // e sem botão de pular — só um spinner numa tela branca. MEDIDO em 2026-07-26: 1h+ travado,
        // e nem relógio corrigido, nem reboot, nem re-adicionar a conta destravaram, porque a conta
        // nunca foi o problema — o ACESSO a ela é que estava bloqueado.
        //
        // Só se manifesta quando EXISTE conta Google no aparelho, e a conta é justamente o que se
        // recomenda ter (é ela que faz os contatos sobreviverem à troca de chip). Ou seja: seguir a
        // recomendação era o que ativava a armadilha.
        await DockerCli.DockerAsync(ct, "exec", Opts.ContainerName, "adb", "shell",
            "pm", "grant", Opts.WhatsAppPackage, "android.permission.GET_ACCOUNTS");
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
