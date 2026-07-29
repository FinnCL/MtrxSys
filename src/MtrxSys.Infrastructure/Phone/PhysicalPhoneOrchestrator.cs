using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Engine de aparelho FÍSICO: dirige um celular real por `adb -s &lt;serial&gt;`.</summary>
/// <remarks>
/// <para>Por que existe: o emulador não passa na atestação (Play Integrity), sai por IP de datacenter
/// (daí o proxy IPRoyal) e vinha acumulando restrição a cada chip. Em 2026-07-29, no mesmo dia, um chip
/// do emulador com o contato salvo e sincronizado NÃO entregou, e um chip de horas de vida num Galaxy
/// A14 entregou a primeira mensagem da vida para um número que nem estava na agenda. Ver
/// docs/engine-physical.md.</para>
/// <para>O que este engine NÃO faz, e é de propósito: provisionar, resetar, imagem-ouro, trocar chip.
/// Não se cria nem se apaga um celular por software. Esses membros ficam no default "não suportado" da
/// interface, e a aba Celular precisa de variante que não os ofereça (Fase 4).</para>
/// <para>⚠️ SEM ROOT. Tudo que lê os bancos privados do WhatsApp (grupos, inbound, veredito primário de
/// existência) fica de fora — ver Fases 2 e 3 do doc.</para>
/// </remarks>
internal sealed class PhysicalPhoneOrchestrator : IPhoneOrchestrator, IDisposable
{
    private readonly PhoneOptions _opts;
    private readonly ILogger<PhysicalPhoneOrchestrator> _log;

    // Tipo CONCRETO de propósito: este engine só existe pro transporte direto, e o analisador reclama
    // (CA1859) de campo com tipo de interface que nunca varia. A abstração serve ao driver e ao leitor
    // de agenda, que recebem IAdbRunner e por isso podem ser testados com um transporte falso.
    private readonly DirectAdbRunner _adb;
    private readonly WhatsAppUiDriver _ui;
    private readonly WhatsAppContactsReader _contacts;

    // 0/1: já avisei que o aparelho sumiu? Sem isto, um cabo solto viraria uma linha de log por poll da
    // aba Celular — e aí o operador para de ler exatamente o aviso que importa.
    private int _unavailableWarned;

    public PhysicalPhoneOrchestrator(
        IOptions<PhoneOptions> opts, ILogger<PhysicalPhoneOrchestrator> log)
    {
        _opts = opts.Value;
        _log = log;
        _adb = new DirectAdbRunner(_opts.AdbSerial, _opts.AdbPath);
        _ui = new WhatsAppUiDriver(_adb, _opts);
        _contacts = new WhatsAppContactsReader(_adb);
    }

    public void Dispose() => _ui.Dispose();

    // ── Ciclo de vida (os 8 obrigatórios da interface) ───────────────────────────────────────────

    /// <summary>Estado do aparelho pelo `adb get-state`.</summary>
    /// <remarks>"unavailable" cobre os três jeitos de não ter aparelho (cabo fora, adb ausente, serial
    /// errado) — a aba degrada igual ao caso sem Docker, que é o comportamento que ela já sabe tratar.</remarks>
    public async Task<PhoneStatus> GetStatusAsync(CancellationToken ct)
    {
        var (rc, outp, err) = await _adb.RawAsync(ct, "get-state");
        var state = (outp ?? "").Trim();

        // "unauthorized" tem CAUSA E CONSERTO próprios (aceitar o diálogo RSA na tela), e o adb devolve
        // isso pelo stderr com código 1 — o mesmo formato de cabo solto. Sem separar, a mensagem manda
        // conferir o cabo quando o cabo está bom, que é o diagnóstico errado no lugar mais caro.
        if ((err ?? "").Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return new PhoneStatus("unauthorized", false, null);
        }
        if (rc != 0 || state.Length == 0)
        {
            // As causas medidas em 29/07: cabo só de carga, tela bloqueada com Android 15 + Bloqueador
            // automático da Samsung, e Depuração USB desligada. As três dão o mesmo silêncio aqui.
            if (Interlocked.Exchange(ref _unavailableWarned, 1) == 0)
            {
                _log.LogWarning(
                    "Aparelho físico {Serial} não responde ao adb. Verificar: cabo de DADOS (não só "
                    + "carga), Depuração USB ligada, tela desbloqueada e Bloqueador automático desligado.",
                    string.IsNullOrWhiteSpace(_opts.AdbSerial) ? "(sem serial)" : _opts.AdbSerial);
            }
            return new PhoneStatus("unavailable", false, null);
        }
        if (Interlocked.Exchange(ref _unavailableWarned, 0) == 1)
        {
            _log.LogInformation("Aparelho físico {Serial} voltou a responder.", _opts.AdbSerial);
        }
        var running = string.Equals(state, "device", StringComparison.OrdinalIgnoreCase);
        return new PhoneStatus(running ? "running" : state, running,
            running ? NullIfEmpty(_opts.ViewUrl) : null);
    }

    public async Task<bool> IsBootedAsync(CancellationToken ct)
    {
        var (rc, outp, _) = await _adb.ShellAsync("getprop sys.boot_completed", ct);
        return rc == 0 && outp.Trim() == "1";
    }

    /// <summary>No físico não há o que provisionar: o aparelho é o aparelho. Devolve o status atual.</summary>
    public Task<PhoneStatus> ProvisionAsync(CancellationToken ct) => GetStatusAsync(ct);

    /// <summary>Acorda a tela. O `uiautomator` precisa de tela ligada, e Android 15 + Bloqueador
    /// automático da Samsung cortam dados por USB com o aparelho bloqueado (medido em 29/07).</summary>
    public async Task<PhoneStatus> StartAsync(CancellationToken ct)
    {
        await _adb.ShellAsync("input keyevent KEYCODE_WAKEUP", ct);
        return await GetStatusAsync(ct);
    }

    /// <summary>NO-OP. Não desliga o aparelho nem apaga a tela.</summary>
    /// <remarks>
    /// 🔴 Apagar a tela aqui era um LOCKOUT. Android 15 e o Bloqueador automático da Samsung cortam
    /// dados por USB com o aparelho bloqueado (medido em 2026-07-29: a conexão caiu exatamente assim,
    /// duas vezes). Sem adb não há como mandar KEYCODE_WAKEUP, então o comando que apaga a tela é o
    /// mesmo que destrói o canal usado pra desfazê-lo — o aparelho só volta se alguém encostar nele.
    /// <para>Num piloto em casa isso é chato; num aparelho remoto é uma viagem. Operação sem volta não
    /// pode ficar atrás de um botão de aba. Quem quiser mesmo desligar, desliga no aparelho.</para>
    /// </remarks>
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<string> GetLogsAsync(int tail, CancellationToken ct)
    {
        var n = Math.Clamp(tail, 1, 2000);
        var (_, outp, err) = await _adb.RawAsync(ct, "logcat", "-d", "-t", n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.IsNullOrWhiteSpace(outp) ? err : outp;
    }

    /// <summary>Não instala. Orienta a instalar pela Play Store.</summary>
    /// <remarks>Sideload é tecnicamente possível (`adb install`), mas o próprio
    /// deploy/build-golden-image-a.sh registra que instalação fora da loja já foi suspeita de disparar o
    /// aviso "Baixe o app oficial" — a ponto de o script usar `-i com.android.vending` pra fingir a
    /// loja. Num aparelho real, com conta Google, instalar pela loja é grátis e é o caminho natural.</remarks>
    public Task<string> InstallWhatsAppAsync(CancellationToken ct) =>
        Task.FromResult(
            "Instale o WhatsApp pela Play Store no próprio aparelho. Este engine não faz sideload de "
            + "propósito: instalação fora da loja é sinal desnecessário num aparelho que passa na "
            + "atestação. Ver docs/engine-physical.md.");

    /// <summary>Aplica o http_proxy global do Android. Funciona, mas NÃO é o modo de operação aqui.</summary>
    /// <remarks>O aparelho físico sai pela operadora (ou pelo WiFi de casa), ambos brasileiros — o
    /// proxy existe pro emulador, que sai por datacenter estrangeiro. Fica implementado porque é barato
    /// e pode servir a algum caso pontual, mas o normal é nunca chamar.</remarks>
    public async Task<string> SetProxyAsync(string? hostPort, CancellationToken ct)
    {
        var (value, error) = DockerCli.NormalizeProxyValue(hostPort);
        if (error is not null)
        {
            return error;
        }
        var (rc, outp, err) = await _adb.ShellAsync($"settings put global http_proxy {value}", ct);
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    // ── Sobrescritas que fazem sentido no físico ─────────────────────────────────────────────────

    /// <summary>true SEMPRE, e isso é uma decisão consciente, não um teste afrouxado.</summary>
    /// <remarks>
    /// 🔴 Este é um portão FAIL-CLOSED: a UI só libera registrar o chip quando ele é true, pra o número
    /// nunca sair pelo IP do datacenter. No emulador a pós-condição é "gost escutando + REDIRECT no nat
    /// OUTPUT". No físico esse risco NÃO EXISTE: o aparelho sai pela operadora ou pelo WiFi local, e não
    /// há datacenter no caminho. Devolver false aqui travaria o registro para sempre, por um perigo que
    /// não se aplica. NÃO "consertar" isto achando que é bug — ver docs/engine-physical.md.
    /// </remarks>
    public Task<bool> IsEgressProxyUpAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<WhatsAppSendResult> SendWhatsAppMessageAsync(string phoneE164, string text, CancellationToken ct) =>
        _ui.SendAsync(phoneE164, text, ct);

    public Task<string?> CheckTypingCapabilityAsync(string sampleText, CancellationToken ct) =>
        _ui.CheckTypingCapabilityAsync(sampleText, ct);

    public Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct) =>
        _contacts.SaveContactAsync(phoneE164, name, ct);

    /// <summary>Veredito de existência pelo espelho da agenda. NUNCA devolve false — ver o comentário
    /// em <see cref="WhatsAppContactsReader.IsOnWhatsAppAsync"/>.</summary>
    public Task<bool?> IsOnWhatsAppAsync(string phoneE164, CancellationToken ct) =>
        _contacts.IsOnWhatsAppAsync(phoneE164, ct);

    public async Task<string> OpenUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _) || url.Contains('\'', StringComparison.Ordinal))
        {
            return "URL inválida.";
        }
        var (rc, outp, err) = await _adb.ShellAsync($"am start -a android.intent.action.VIEW -d '{url}'", ct);
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    public async Task<string> SendKeyAsync(string key, CancellationToken ct)
    {
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
        var (rc, outp, err) = await _adb.ShellAsync($"input keyevent {code}", ct);
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    public async Task<string> SendTextAsync(string text, CancellationToken ct)
    {
        // Mesma sanitização do engine do emulador: só o que o `input text` aceita sem quebrar, e nada
        // que possa virar injeção no shell do aparelho.
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
        var (rc, outp, err) = await _adb.ShellAsync($"input text {safe}", ct);
        return rc == 0 ? "ok" : (string.IsNullOrWhiteSpace(err) ? outp : err);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
