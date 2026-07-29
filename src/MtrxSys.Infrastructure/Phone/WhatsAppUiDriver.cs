using System.Globalization;
using System.Text.RegularExpressions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Envia mensagem pela UI do WhatsApp de um aparelho Android, virtual ou físico.</summary>
/// <remarks>
/// <para>Espelha o caminho que roda em produção no <see cref="DockerCliPhoneOrchestrator"/>, com uma
/// diferença: o transporte entra por <see cref="IAdbRunner"/> em vez de `docker exec` fixo. Os
/// resource-ids, a ordem dos passos e os critérios de confirmação são os mesmos — foram medidos e cada
/// um custou um bug.</para>
/// <para>✅ VALIDADO no físico em 2026-07-29 (Galaxy A14, Android 15, WhatsApp 2.26.27.85, SEM root):
/// deep link abriu `com.whatsapp/.Conversation`, `uiautomator dump` funcionou, `id/entry` e `id/send`
/// existem com os mesmos nomes, o toque enviou e a entrega foi lida como "Entregue".</para>
/// <para>⚠️ DUPLICAÇÃO TEMPORÁRIA e consciente: o orquestrador do emulador segue com a cópia dele. A
/// desduplicação é passo separado — ver docs/engine-physical.md, Fase 1.</para>
/// </remarks>
internal sealed class WhatsAppUiDriver(IAdbRunner adb, PhoneOptions opts) : IDisposable
{
    private readonly IAdbRunner _adb = adb;
    private readonly PhoneOptions _opts = opts;

    // Um envio por vez por aparelho: `uiautomator dump` não roda concorrente e grava num arquivo fixo.
    private readonly SemaphoreSlim _uiLock = new(1, 1);

    // Caminho do dump. Fixo de propósito (o lock acima serializa), mas próprio do MtrxSys pra não
    // colidir com um dump que alguém tenha deixado em /sdcard/window_dump.xml.
    private const string DumpPath = "/sdcard/mtrx_ui.xml";

    public void Dispose() => _uiLock.Dispose();

    // ── Envio ────────────────────────────────────────────────────────────────────────────────────

    public async Task<WhatsAppSendResult> SendAsync(string phoneE164, string text, CancellationToken ct)
    {
        var digits = new string([.. (phoneE164 ?? string.Empty).Where(char.IsDigit)]);
        if (digits.Length < 8)
        {
            return WhatsAppSendResult.Fail("phone inválido");
        }
        text ??= string.Empty;
        if (text.Length == 0)
        {
            return WhatsAppSendResult.Fail("texto vazio");
        }

        // Sem digitação humana: o deep link entrega a mensagem pronta no campo e só resta tocar enviar.
        if (!_opts.HumanTyping)
        {
            return await SendByDeepLinkAsync(digits, text, ct);
        }
        return await SendByTypingAsync(digits, text, ct);
    }

    private async Task<WhatsAppSendResult> SendByDeepLinkAsync(string digits, string text, CancellationToken ct)
    {
        var url = WhatsAppUi.DeepLink(digits, text);
        if (url.Contains('\'', StringComparison.Ordinal))
        {
            return WhatsAppSendResult.Fail("texto gerou aspa simples (não esperado no URL-encode).");
        }

        await _uiLock.WaitAsync(ct);
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, _opts.WhatsAppSendTimeoutSeconds)));
            var sct = sendCts.Token;

            var (rc, outp, err) = await OpenChatAsync(url, sct);
            if (rc != 0)
            {
                return WhatsAppSendResult.Fail(string.IsNullOrWhiteSpace(err) ? outp : err);
            }
            var send = await PollNodeCenterAsync("com.whatsapp:id/send", _opts.WhatsAppOpenWaitMs, sct);
            if (send is null)
            {
                return WhatsAppSendResult.Fail("botão enviar não apareceu (o chat não abriu ou o texto não preencheu).");
            }
            await TapAsync(send.Value, sct);
            if (!await PollEntryClearedAsync(_opts.WhatsAppSendWaitMs, sct))
            {
                return WhatsAppSendResult.Fail("toquei enviar mas o campo não esvaziou — envio não confirmado.");
            }
            return WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WhatsAppSendResult.Fail("envio excedeu o tempo total (aparelho lento/travado?).");
        }
        finally
        {
            _uiLock.Release();
        }
    }

    private async Task<WhatsAppSendResult> SendByTypingAsync(string digits, string text, CancellationToken ct)
    {
        if (await ResolveTypingChannelAsync(text, ct) is not { } typing)
        {
            // FALHA em vez de cair no deep link. Voltar ao caminho antigo em silêncio devolveria o
            // comportamento que se quis remover, e o log diria "enviado" do mesmo jeito.
            return WhatsAppSendResult.Fail(
                $"digitação humana exigida mas indisponível: nem o teclado {_opts.TypingImePackage} está "
                + "instalado/ativo, nem o texto é simples o bastante pro `input text` (acento e emoji "
                + "quebram). Instale o IME no aparelho ou desligue Phone__HumanTyping.");
        }

        await _uiLock.WaitAsync(ct);
        try
        {
            // Teto calculado: digitar é a única etapa cujo custo depende do TAMANHO da mensagem.
            var typingBudget = TimeSpan.FromMinutes(
                2.0 * text.Length / Math.Clamp(_opts.TypingCharsPerMinute, 60, 1200));
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(
                TimeSpan.FromSeconds(Math.Max(60, _opts.WhatsAppSendTimeoutSeconds)) + typingBudget);
            var sct = sendCts.Token;

            var (rc, outp, err) = await OpenChatAsync(WhatsAppUi.DeepLinkEmpty(digits), sct);
            if (rc != 0)
            {
                return WhatsAppSendResult.Fail(string.IsNullOrWhiteSpace(err) ? outp : err);
            }

            var entry = await PollNodeCenterAsync("com.whatsapp:id/entry", _opts.WhatsAppOpenWaitMs, sct);
            if (entry is null)
            {
                return WhatsAppSendResult.Fail("campo de mensagem não apareceu (a conversa não abriu).");
            }
            await TapAsync(entry.Value, sct);

            var previousIme = typing is TypingChannel.Ime ? await SelectTypingImeAsync(sct) : null;
            try
            {
                // Campo LIMPO antes de começar: o envio reabre a MESMA conversa, então sobra de uma
                // tentativa anterior entraria embaralhada com a nova (medido em 2026-07-27).
                if (!await ClearEntryAsync(typing, sct))
                {
                    return WhatsAppSendResult.Fail("o campo de mensagem já tinha texto e não consegui limpar.");
                }
                await Task.Delay(Random.Shared.Next(900, 2600), sct);

                if (!await TypeInChunksAsync(typing, text, sct))
                {
                    await ClearEntryAsync(typing, sct);
                    return WhatsAppSendResult.Fail("a digitação falhou no meio; campo limpo e envio abortado.");
                }

                // Confere o TAMANHO antes de enviar: o IME pode engolir trecho em silêncio, e mensagem
                // truncada é pior que mensagem nenhuma.
                var typed = await ReadEntryTextAsync(sct);
                if (typed is null || Math.Abs(typed.Length - text.Length) > Math.Max(4, text.Length / 20))
                {
                    var achou = typed?.Length.ToString(CultureInfo.InvariantCulture) ?? "nada";
                    await ClearEntryAsync(typing, sct);
                    return WhatsAppSendResult.Fail(
                        $"campo ficou com {achou} caracteres, esperava ~{text.Length}; "
                        + "envio abortado pra não mandar truncado.");
                }
            }
            finally
            {
                await RestoreImeAsync(previousIme, sct);
            }

            await Task.Delay(Random.Shared.Next(700, 2200), sct);
            var send = await PollNodeCenterAsync("com.whatsapp:id/send", _opts.WhatsAppSendWaitMs, sct);
            if (send is null)
            {
                await ClearEntryAsync(typing, sct);
                return WhatsAppSendResult.Fail("botão enviar não apareceu mesmo com o campo preenchido.");
            }
            await TapAsync(send.Value, sct);

            if (!await PollEntryClearedAsync(_opts.WhatsAppSendWaitMs, sct))
            {
                return WhatsAppSendResult.Fail("toquei enviar mas o campo não esvaziou — envio não confirmado.");
            }
            return WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return WhatsAppSendResult.Fail(
                $"envio excedeu o teto (base {_opts.WhatsAppSendTimeoutSeconds}s + orçamento de digitação "
                + $"pra {text.Length} caracteres a {_opts.TypingCharsPerMinute} cpm).");
        }
        finally
        {
            _uiLock.Release();
        }
    }

    private Task<(int Code, string Out, string Err)> OpenChatAsync(string url, CancellationToken ct) =>
        // Aspas simples: '&' e '#' da URL não podem ser interpretados pelo shell do aparelho.
        _adb.ShellAsync($"am start -a android.intent.action.VIEW -d '{url}'", ct);

    // ── Leitura de tela ──────────────────────────────────────────────────────────────────────────

    /// <summary>Árvore de acessibilidade da tela atual. null = não deu pra ler.</summary>
    /// <remarks>Não exige root, nem no aparelho físico — confirmado em 2026-07-29.</remarks>
    public async Task<string?> DumpUiAsync(CancellationToken ct)
    {
        var (rc, _, _) = await _adb.ShellAsync($"uiautomator dump {DumpPath}", ct);
        if (rc != 0)
        {
            return null;
        }
        var (cc, xml, _) = await _adb.ShellAsync($"cat {DumpPath}", ct);
        return cc == 0 && !string.IsNullOrWhiteSpace(xml) ? xml : null;
    }

    private static readonly Regex BoundsRx =
        new("bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"", RegexOptions.Compiled);

    /// <summary>Centro do nó com este resource-id, com POLL até o timeout. null = não apareceu.</summary>
    /// <remarks>Poll e não sleep fixo: chat lento (cold start, rede ruim) abriria depois da espera e o
    /// envio seria abortado à toa.</remarks>
    private async Task<(int X, int Y)?> PollNodeCenterAsync(string resourceId, int timeoutMs, CancellationToken ct)
    {
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            var xml = await DumpUiAsync(ct);
            if (xml is not null)
            {
                var node = Regex.Match(xml, $"<node[^>]*{Regex.Escape(resourceId)}\"[^>]*>");
                if (node.Success)
                {
                    var b = BoundsRx.Match(node.Value);
                    if (b.Success
                        && int.TryParse(b.Groups[1].Value, out var x1)
                        && int.TryParse(b.Groups[2].Value, out var y1)
                        && int.TryParse(b.Groups[3].Value, out var x2)
                        && int.TryParse(b.Groups[4].Value, out var y2))
                    {
                        return ((x1 + x2) / 2, (y1 + y2) / 2);
                    }
                }
            }
            if (i < attempts)
            {
                await Task.Delay(500, ct);
            }
        }
        return null;
    }

    // ⚠️ CAMPO VAZIO NÃO É text="". O uiautomator devolve a DICA quando não há texto — medido nos DOIS
    // aparelhos: vazio sai como text="Mensagem". Comparar com string vazia nunca dá verdadeiro, e quem
    // dependesse disso ("já limpou", "enviou") ficaria preso pra sempre.
    //
    // O sinal confiável é o BOTÃO DE ENVIAR: o WhatsApp mostra o microfone com o campo vazio e troca
    // pelo botão de enviar quando tem texto. Não depende de idioma nem do texto da dica.
    private static bool HasSendButton(string? xml) =>
        xml is not null && xml.Contains("com.whatsapp:id/send\"", StringComparison.Ordinal);

    private async Task<bool> PollEntryClearedAsync(int timeoutMs, CancellationToken ct)
    {
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            if (!HasSendButton(await DumpUiAsync(ct)))
            {
                return true;
            }
            if (i < attempts)
            {
                await Task.Delay(500, ct);
            }
        }
        return false;
    }

    // O `text` do nó vem ANTES do `resource-id` no dump. Por isso a busca é pelo NÓ inteiro e o
    // atributo sai de dentro dele — assim a ordem dos atributos deixa de importar.
    private static readonly Regex EntryNodeRx =
        new("<node[^>]*com\\.whatsapp:id/entry[^>]*>", RegexOptions.Compiled);

    private static readonly Regex TextAttrRx = new("text=\"([^\"]*)\"", RegexOptions.Compiled);

    private static string? EntryTextFrom(string? xml)
    {
        var node = xml is null ? null : EntryNodeRx.Match(xml);
        if (node is not { Success: true })
        {
            return null;
        }
        var t = TextAttrRx.Match(node.Value);
        // O dump escapa XML (&#10; etc). Desescapa pra o COMPRIMENTO bater com o texto original.
        return t.Success ? System.Net.WebUtility.HtmlDecode(t.Groups[1].Value) : "";
    }

    private async Task<string?> ReadEntryTextAsync(CancellationToken ct) =>
        EntryTextFrom(await DumpUiAsync(ct));

    private static readonly Regex StatusRx =
        new("resource-id=\"com.whatsapp:id/status\"[^>]*?content-desc=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>Entrega NORMALIZADA (locale-independente): sent | delivered | read | null.</summary>
    public async Task<string?> ReadLastMessageStatusAsync(CancellationToken ct)
    {
        var xml = await DumpUiAsync(ct);
        if (xml is null)
        {
            return null;
        }
        var raw = StatusRx.Matches(xml).Cast<Match>().LastOrDefault()?.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var r = raw.ToLowerInvariant();
        if (r.Contains("entreg", StringComparison.Ordinal) || r.Contains("deliver", StringComparison.Ordinal)) return "delivered";
        if (r.Contains("lida", StringComparison.Ordinal) || r.Contains("lido", StringComparison.Ordinal) || r.Contains("read", StringComparison.Ordinal)) return "read";
        if (r.Contains("enviad", StringComparison.Ordinal) || r.Contains("sent", StringComparison.Ordinal)) return "sent";
        return null; // desconhecido → não inventa entrega
    }

    // ── Toque e digitação ────────────────────────────────────────────────────────────────────────

    private async Task TapAsync((int X, int Y) p, CancellationToken ct)
    {
        // Desvio de alguns pixels: toque humano não acerta o centro geométrico duas vezes seguidas.
        var x = p.X + Random.Shared.Next(-6, 7);
        var y = p.Y + Random.Shared.Next(-4, 5);
        await _adb.ShellAsync($"input tap {x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)}", ct);
    }

    /// <summary>Por onde o texto entra no aparelho. null = não há caminho capaz de digitar ESTE texto.</summary>
    private enum TypingChannel
    {
        /// <summary>IME que aceita Unicode por broadcast (acento e emoji).</summary>
        Ime,

        /// <summary>`input text` do Android: só ASCII imprimível, e sem aspa simples.</summary>
        InputText,
    }

    // `input text` NÃO digita acento nem emoji — devolve NullPointerException (medido, Android 14).
    private static bool IsTypeableByInputText(string text) =>
        text.All(c => c is >= ' ' and <= '~' && c != '\'');

    private async Task<TypingChannel?> ResolveTypingChannelAsync(string text, CancellationToken ct)
    {
        if (await IsTypingImeReadyAsync(ct))
        {
            return TypingChannel.Ime;
        }
        return IsTypeableByInputText(text) ? TypingChannel.InputText : null;
    }

    private int _typingImeReady;

    /// <summary>O IME de digitação está instalado e habilitado?</summary>
    /// <remarks>No aparelho físico NÃO se instala nada por aqui: instalar APK à revelia num celular de
    /// uso real é invasivo, e o `input text` já cobre o caso do piloto (mensagem curta em ASCII, tipo
    /// "oi"). Se precisar de acento/emoji, instale o IME no aparelho e ele será detectado aqui.</remarks>
    private async Task<bool> IsTypingImeReadyAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _typingImeReady) == 1)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(_opts.TypingImePackage))
        {
            return false;
        }
        var (lc, list, _) = await _adb.ShellAsync("ime list -s", ct);
        if (lc != 0 || !(list ?? "").Contains(_opts.TypingImePackage, StringComparison.Ordinal))
        {
            return false;
        }
        Interlocked.Exchange(ref _typingImeReady, 1);
        return true;
    }

    /// <summary>Seleciona o teclado de digitação e devolve o que estava antes, pra ser restaurado.</summary>
    /// <remarks>Deixar este IME como padrão tira o teclado DA TELA (ele não desenha teclas, só recebe
    /// texto por broadcast). Quem for mexer no aparelho à mão ficaria sem conseguir digitar, e o sintoma
    /// ("o teclado sumiu") não apontaria pra cá. Por isso só em volta da digitação.</remarks>
    private async Task<string?> SelectTypingImeAsync(CancellationToken ct)
    {
        var (rc, current, _) = await _adb.ShellAsync("settings get secure default_input_method", ct);
        var previous = rc == 0 ? (current ?? "").Replace("\r", "").Replace("\n", "").Trim() : "";
        await _adb.ShellAsync($"ime set {_opts.TypingImeComponent}", ct);
        return string.IsNullOrWhiteSpace(previous) || previous == "null" ? null : previous;
    }

    private async Task RestoreImeAsync(string? previous, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(previous))
        {
            await _adb.ShellAsync($"ime set {previous}", ct);
        }
    }

    /// <summary>Esvazia o campo. false = não consegui confirmar que ficou vazio.</summary>
    private async Task<bool> ClearEntryAsync(TypingChannel channel, CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            if (!HasSendButton(await DumpUiAsync(ct)))
            {
                return true; // sem botão de enviar = campo vazio
            }
            if (channel is TypingChannel.Ime)
            {
                await _adb.ShellAsync("am broadcast -a ADB_CLEAR_TEXT", ct);
            }
            else
            {
                // Sem IME não há comando de limpar: apaga em lote com o teclado virtual.
                await _adb.ShellAsync("input keyevent " + string.Join(" ", Enumerable.Repeat("67", 40)), ct);
            }
            await Task.Delay(300, ct);
        }
        return false;
    }

    private async Task<bool> TypeInChunksAsync(TypingChannel channel, string text, CancellationToken ct)
    {
        var perChar = 60_000.0 / Math.Clamp(_opts.TypingCharsPerMinute, 60, 1200);
        foreach (var chunk in SplitForTyping(text))
        {
            if (!await SendChunkAsync(channel, chunk, ct))
            {
                return false;
            }
            var waitMs = (int)(chunk.Length * perChar * (0.75 + (Random.Shared.NextDouble() * 0.7)));
            // De vez em quando alguém para pra pensar. Sem isso a cadência é uniforme demais.
            if (Random.Shared.Next(100) < 12)
            {
                waitMs += Random.Shared.Next(700, 2400);
            }
            await Task.Delay(waitMs, ct);
        }
        return true;
    }

    private async Task<bool> SendChunkAsync(TypingChannel channel, string chunk, CancellationToken ct)
    {
        if (channel is TypingChannel.Ime)
        {
            var (rc, _, _) = await _adb.ShellAsync($"am broadcast -a ADB_INPUT_TEXT --es msg {ShellQuote(chunk)}", ct);
            return rc == 0;
        }
        // `input text` não recebe espaço: o adb junta os argumentos, então o espaço vira separador.
        // %s é o marcador que o próprio `input` traduz de volta.
        var (tc, _, _) = await _adb.ShellAsync($"input text {ShellQuote(chunk.Replace(" ", "%s", StringComparison.Ordinal))}", ct);
        return tc == 0;
    }

    /// <summary>Aspas simples pro shell DO APARELHO (o adb junta os argumentos e o shell de lá reinterpreta).</summary>
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // Trechos de 12 a 28 caracteres, cortando no espaço seguinte pra não partir palavra. Quebra de linha
    // vira fim de trecho: é onde uma pessoa naturalmente pausa.
    private static IEnumerable<string> SplitForTyping(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var target = Math.Min(text.Length, i + Random.Shared.Next(12, 29));
            var nl = text.IndexOf('\n', i);
            if (nl >= 0 && nl < target)
            {
                target = nl + 1;
            }
            else
            {
                while (target < text.Length && text[target] != ' ')
                {
                    target++;
                }
                if (target < text.Length)
                {
                    target++; // leva o espaço junto
                }
            }
            yield return text[i..target];
            i = target;
        }
    }
}
