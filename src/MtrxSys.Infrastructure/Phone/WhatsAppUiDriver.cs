using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        // Normaliza a quebra de linha ANTES de qualquer medida: o \r do Windows não é digitável e
        // entraria na contagem de caracteres que confere o campo antes de enviar.
        text = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
                                     .Replace('\r', '\n');
        if (text.Length == 0)
        {
            return WhatsAppSendResult.Fail("texto vazio");
        }

        // Sem digitação humana: o deep link entrega a mensagem pronta no campo e só resta tocar enviar.
        var resultado = !_opts.HumanTyping
            ? await SendByDeepLinkAsync(digits, text, ct)
            : await SendByTypingAsync(digits, text, ct);

        // 🔴 FALHA DEIXA A TELA COMO ESTAVA, e "como estava" costuma ser um DIÁLOGO do WhatsApp — o
        // "o número não está no WhatsApp" que aparece quando o deep link aponta pra quem não tem conta.
        // Ele fica por cima de tudo, então o PRÓXIMO contato do lote abre atrás do diálogo e falha
        // igual: uma falha isolada virava uma sequência de falhas, e o operador via o robô "travado"
        // sem entender por quê. Relatado operando em 2026-08-05.
        // Só no caminho de falha: no sucesso a conversa aberta é estado legítimo e mexer nela seria
        // interferência à toa.
        if (!resultado.Sent)
        {
            await DispensarDialogoAsync(ct);
        }
        return resultado;
    }

    /// <summary>Por que o campo de envio não apareceu: lê a tela e distingue "o WhatsApp respondeu que
    /// este número não tem conta" de "a tela não respondeu".</summary>
    /// <remarks>
    /// 🔴 A mensagem antiga era "botão enviar não apareceu (o chat não abriu ou o texto não
    /// preencheu)" — um OU que juntava duas causas com consertos opostos: uma é qualidade da lista,
    /// a outra é o aparelho. O operador passou horas em 2026-08-05 sem conseguir distinguir, tentando
    /// o mesmo número várias vezes porque o erro não dizia que o próprio WhatsApp já tinha respondido.
    ///
    /// <para>A resposta está NA TELA: quando o número não tem conta, o app mostra um diálogo dizendo
    /// isso. Basta ler. Casar por TRECHO e em vários idiomas porque o texto exato muda com a versão e
    /// com o idioma do aparelho, e um casamento exato quebraria em silêncio na próxima atualização.</para>
    ///
    /// <para>Na dúvida devolve a mensagem genérica: afirmar a causa errada é pior que admitir que não
    /// se sabe — foi justamente o que custou as horas acima.</para>
    /// </remarks>
    /// <returns>O veredito e o XML que o produziu. O XML sai junto porque quem chama precisa da MESMA
    /// tela para tentar desbloqueá-la: dumpar de novo custaria outro processo `adb` e, pior, poderia
    /// ler uma tela DIFERENTE — o diagnóstico falaria de um aviso e a evidência guardada, de outro.</returns>
    private async Task<(WhatsAppSendResult Veredito, string? Xml)> DiagnosticarChatNaoAbertoAsync(
        CancellationToken ct)
    {
        var generico = WhatsAppSendResult.Fail(
            "botão enviar não apareceu (o chat não abriu ou o texto não preencheu).");
        var xml = await DumpUiAsync(ct);
        if (string.IsNullOrWhiteSpace(xml))
        {
            // Sem leitura de tela não dá pra afirmar que a culpa é do número, e o palpite errado aqui
            // faz o lote seguir em frente contra um aparelho travado. Na dúvida, é falha do aparelho.
            return (generico, null);
        }

        // Trechos estáveis do aviso, sem acento e em minúsculas, pra sobreviver a variação de texto.
        // ⚠️ SÓ pistas inequívocas. "convidar para o WhatsApp" foi tirado de propósito: essa palavra
        // aparece em outras telas do app, e um falso positivo aqui afirma a causa errada COM
        // CONFIANÇA — que é o defeito que este método existe pra corrigir. O diálogo de número sem
        // conta sempre traz alguma variação de "não está no WhatsApp"; isso basta.
        string[] pistas =
        [
            "nao esta no whatsapp", "não está no whatsapp",
            "nao existe no whatsapp", "não existe no whatsapp",
            "isn't on whatsapp", "is not on whatsapp", "not on whatsapp",
        ];

        // Aparelho DORMINDO é a causa mais provável num lote sem ninguém por perto: a tela apaga em
        // ~10 min e a pausa entre blocos é maior que isso. O driver acorda antes de cada envio, então
        // chegar aqui bloqueado significa cadeado COM SENHA, que nenhum comando resolve.
        if (TelaBloqueada(xml))
        {
            return (WhatsAppSendResult.Fail(
                "o aparelho está com a TELA BLOQUEADA e o desbloqueio automático não deu conta, o que "
                + "indica PIN, padrão ou biometria. Não é o número nem a conversa. No aparelho: tire o "
                + "bloqueio de tela e ligue \"Permanecer ativo\" nas Opções do desenvolvedor (ou rode "
                + "tools\\preparar-aparelho.cmd, que já faz isso)."), xml);
        }

        var tela = xml.ToLowerInvariant();
        return (pistas.Any(p => tela.Contains(p, StringComparison.Ordinal))
            ? WhatsAppSendResult.NoAccount(
                "o WhatsApp respondeu que ESTE NÚMERO não tem conta. Não é o aparelho nem a conexão: "
                + "é a forma do número ou o contato mesmo. Se o contato existe, confira o 9º dígito.")
            : generico, xml);
    }

    /// <summary>Tenta tirar da frente um aviso que está por cima da conversa, para a mensagem poder
    /// sair NESTE contato em vez de só no próximo.</summary>
    /// <remarks>
    /// 🔴 O CASO QUE MOTIVOU: conversa com MENSAGENS TEMPORÁRIAS ligadas abre uma tela informativa por
    /// cima. Enquanto ela está lá, nem o campo de mensagem nem o botão de enviar existem na árvore, e
    /// o envio falhava com "a conversa não abriu" — diagnóstico que aponta para o aparelho quando o
    /// aparelho está perfeito. Pior: o contato ia para o balde de falha de APARELHO, que é o contador
    /// que dispara o alerta de celular travado.
    ///
    /// <para>Já existia recuperação para isso, o <see cref="DispensarDialogoAsync"/>, mas ela roda
    /// DEPOIS do resultado: limpa a tela para o PRÓXIMO contato e perde o atual. Aqui a limpeza
    /// acontece antes de desistir, e o envio segue.</para>
    ///
    /// <para>Duas saídas, nessa ordem: um botão de rótulo neutro (ver
    /// <see cref="BotoesQueSoFecham"/>) e, se não houver, um BACK. O BACK é o plano B porque o aviso
    /// pode ser tela cheia sem botão reconhecível, e porque ele não confirma nada — no pior caso sai
    /// da conversa, e a conversa é reaberta por deep link no próximo contato de qualquer forma.</para>
    ///
    /// <para>⚠️ OS RÓTULOS NÃO FORAM MEDIDOS EM APARELHO. Vieram do que o app usa em avisos desse tipo,
    /// não de um dump real da tela de mensagens temporárias. É por isso que o caminho de desistência
    /// GUARDA A TELA (ver <see cref="DescreverTelaDesconhecidaAsync"/>) em vez de só devolver falha:
    /// esperar alguém estar presente no momento certo pra rodar o dump é esperar por uma coincidência,
    /// e o disparo roda justamente sem ninguém por perto.</para>
    /// </remarks>
    /// <param name="xml">A tela JÁ LIDA por quem chama. Não dumpa de novo: seria outro processo `adb` no
    /// caminho de falha e, pior, poderia ler uma tela diferente da que foi diagnosticada.</param>
    /// <returns>null quando reconheceu e fechou o aviso; caso contrário, o que dava pra apurar da tela,
    /// pra quem chama anexar à falha.</returns>
    private async Task<string?> TentarDesbloquearTelaAsync(string? xml, CancellationToken ct)
    {
        if (xml is not null)
        {
            foreach (var (rotulo, centro) in Clicaveis(xml))
            {
                if (centro is { } ponto && BotoesQueSoFecham.Contains(rotulo.ToLowerInvariant()))
                {
                    await TapAsync(ponto, ct);
                    await Task.Delay(600, ct);
                    return null;
                }
            }
        }

        // Nenhum rótulo conhecido: registra a tela ANTES do BACK, que é o que a faz sumir.
        var evidencia = xml is null ? null : DescreverTelaDesconhecida(xml);
        await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
        await Task.Delay(600, ct);
        return evidencia;
    }

    /// <summary>Os nós CLICÁVEIS da tela: o rótulo e, quando dá pra ler, o centro para tocar.</summary>
    /// <remarks>
    /// Uma varredura só, e não duas. Achar o botão que fecha um aviso e listar os candidatos quando
    /// nenhum serve são a MESMA pergunta feita para fins diferentes, e estavam escritas duas vezes:
    /// dois laços sobre <see cref="NoRx"/>, duas leituras de <c>clickable</c>, duas extrações de texto.
    /// Duplicar isso não custava só linhas — o caminho de falha varria a árvore inteira duas vezes, e as
    /// duas cópias podiam divergir na definição de "rótulo" no primeiro ajuste.
    /// <para>Lê <c>text</c> e, na falta dele, <c>content-desc</c>: botão só com ícone não tem texto, e é
    /// justamente o caso em que ninguém adivinha o rótulo olhando a tela de longe.</para>
    /// </remarks>
    private static IEnumerable<(string Rotulo, (int X, int Y)? Centro)> Clicaveis(string xml)
    {
        foreach (Match no in NoRx.Matches(xml))
        {
            if (!no.Value.Contains("clickable=\"true\"", StringComparison.Ordinal))
            {
                continue;
            }
            var texto = TextoRx.Match(no.Value) is { Success: true } t && t.Groups[1].Value.Trim().Length > 0
                ? t.Groups[1].Value.Trim()
                : DescRx.Match(no.Value) is { Success: true } d ? d.Groups[1].Value.Trim() : "";
            if (texto.Length == 0)
            {
                continue;
            }
            yield return (texto, CentroDe(no.Value));
        }
    }

    /// <summary>Centro do nó a partir do atributo <c>bounds</c>. null = não deu pra ler.</summary>
    private static (int X, int Y)? CentroDe(string no)
    {
        var b = BoundsRx.Match(no);
        return b.Success
            && int.TryParse(b.Groups[1].Value, out var x1)
            && int.TryParse(b.Groups[2].Value, out var y1)
            && int.TryParse(b.Groups[3].Value, out var x2)
            && int.TryParse(b.Groups[4].Value, out var y2)
                ? ((x1 + x2) / 2, (y1 + y2) / 2)
                : null;
    }

    /// <summary>Apura o que dava pra saber de uma tela que bloqueou o envio e não foi reconhecida:
    /// os rótulos clicáveis dela, e o XML inteiro guardado em disco.</summary>
    /// <remarks>
    /// 🔴 A PERGUNTA QUE ISTO RESPONDE é "qual botão fecha esse aviso?", e ela só tem resposta enquanto
    /// o aviso está na tela. O envio não pode parar e esperar: o lote continua, o contato seguinte abre
    /// outra conversa, e a tela que interessa é substituída em segundos. Ou se captura no instante, ou
    /// se perde.
    ///
    /// <para>Os rótulos vão na MENSAGEM DE ERRO de propósito, e não só no arquivo. O erro já é gravado
    /// no CSV do console (coluna `erro`) e no log do dispatcher, sem nada novo pra configurar — então a
    /// resposta chega a quem lê o resultado do lote de manhã, mesmo que a pasta de telas esteja num
    /// container que já morreu.</para>
    ///
    /// <para>⚠️ CAPTURA, NÃO APRENDE. O sistema NÃO passa a tocar no botão que descobriu: a lista de
    /// <see cref="BotoesQueSoFecham"/> continua fechada e editada à mão. Tocar em texto desconhecido é
    /// tocar às cegas numa tela que pode ter "Bloquear" ou "Denunciar", e o custo de errar é a conta.
    /// Automatizar o DIAGNÓSTICO é seguro; automatizar a DECISÃO não é.</para>
    /// </remarks>
    private string DescreverTelaDesconhecida(string xml)
    {
        var rotulos = RotulosClicaveis(xml);
        var arquivo = GuardarTela(xml);
        var lista = rotulos.Count == 0
            ? "nenhum com texto legível"
            : string.Join(" | ", rotulos.Take(12));
        return " Uma tela NÃO RECONHECIDA estava na frente da conversa e o aviso não pôde ser fechado "
            + $"pelo botão (só por BACK, que sai do chat). Botões visíveis: {lista}."
            + (arquivo is null ? "" : $" Tela salva em {arquivo}.");
    }

    /// <summary>Os rótulos clicáveis da tela, sem repetir e sem parágrafo. É a lista de candidatos a
    /// "botão que fecha isto" — o dado que falta pra decidir o que entra em
    /// <see cref="BotoesQueSoFecham"/>.</summary>
    /// <remarks>Corta texto longo porque um aviso inteiro às vezes é clicável, e o parágrafo dele não
    /// cabe numa coluna de CSV nem ajuda a escolher rótulo de botão.</remarks>
    private static List<string> RotulosClicaveis(string xml) =>
        [.. Clicaveis(xml)
            .Select(c => c.Rotulo.Length <= 40 ? c.Rotulo : c.Rotulo[..40] + "…")
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Grava a tela em disco. Devolve o caminho, ou null se não deu pra gravar.</summary>
    /// <remarks>
    /// Deduplica pelo CONTEÚDO: o mesmo aviso aparecendo em trinta contatos do lote grava um arquivo
    /// só. Sem isso a pasta enche de cópias idênticas e a tela NOVA, que é a que interessa, fica
    /// enterrada. E rotaciona, porque diagnóstico que enche o disco do operador vira outro problema.
    /// <para>Best-effort: pasta sem permissão ou disco cheio NÃO derruba o envio. Os rótulos já foram
    /// para a mensagem de erro, que é a parte que responde a pergunta.</para>
    /// </remarks>
    private string? GuardarTela(string xml)
    {
        try
        {
            var pasta = string.IsNullOrWhiteSpace(_opts.UiDumpDir)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MtrxSys", "telas")
                : _opts.UiDumpDir;
            Directory.CreateDirectory(pasta);

            var assinatura = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))[..8]
                .ToLowerInvariant();
            if (Directory.EnumerateFiles(pasta, $"*-{assinatura}.xml").FirstOrDefault() is { } jaTem)
            {
                return jaTem;
            }

            var caminho = Path.Combine(
                pasta,
                $"tela-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{assinatura}.xml");
            File.WriteAllText(caminho, xml, Encoding.UTF8);

            foreach (var velho in Directory.EnumerateFiles(pasta, "tela-*.xml")
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(Math.Max(1, _opts.UiDumpKeep)))
            {
                File.Delete(velho);
            }
            return caminho;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Devolve o aparelho a um estado neutro depois de uma falha, para o próximo contato do
    /// lote não herdar um diálogo aberto na tela.</summary>
    /// <remarks>
    /// Dois BACK e não um: o "não está no WhatsApp" às vezes empilha o diálogo sobre uma conversa
    /// meio aberta. O segundo BACK é inofensivo se o primeiro já resolveu — a tela inicial do WhatsApp
    /// (ou a Home) é destino aceitável, já que o próximo envio abre a conversa por deep link de
    /// qualquer forma.
    /// <para>Best-effort de propósito: um erro aqui NÃO pode mascarar o erro de envio, que é o que o
    /// operador precisa ler. Por isso engole a exceção e não altera o resultado.</para>
    /// </remarks>
    private async Task DispensarDialogoAsync(CancellationToken ct)
    {
#pragma warning disable CA1031 // recuperação best-effort: ver o remarks
        try
        {
            await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
            await Task.Delay(400, ct);
            await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // aparelho sumiu no meio da limpeza: o erro de envio ja explica o lote
        }
#pragma warning restore CA1031
    }

    /// <summary>Garante que a tela NÃO tem texto pendente no campo antes de abrir a próxima conversa.
    /// null = pode seguir; string = motivo pra não enviar.</summary>
    /// <remarks>
    /// 🔴 O toque em enviar é a única ação deste driver que produz efeito irreversível, e ele nunca
    /// confirmou EM QUAL conversa está. `am start` devolver 0 diz que a intent foi despachada, não que
    /// o WhatsApp navegou; o poll do `id/send` casa já no primeiro dump, que pode ainda mostrar a tela
    /// anterior; e o `id/send` existe sempre que o campo tem texto. Junte as três e o caminho do deep
    /// link podia tocar enviar numa conversa ALHEIA: o contato anterior recebia, e o console registrava
    /// sucesso pro contato atual.
    ///
    /// <para>A verificação vem ANTES e não depois de propósito. Conferir o texto do campo depois de
    /// abrir não resolve, porque dois contatos do mesmo lote costumam sortear o MESMO template: um
    /// rascunho do anterior é idêntico ao texto pretendido pro atual, e a comparação passaria. Já a
    /// pré-condição ataca a causa — sem botão de enviar na tela, o toque em conversa errada deixa de
    /// ter como acontecer, e um deep link que não navegou vira um poll que estoura, ou seja, uma falha
    /// limpa e legível.</para>
    ///
    /// <para>Dump ilegível NÃO barra o envio: nesse caso o poll seguinte falha sozinho, e travar o lote
    /// por uma leitura de tela que não veio seria trocar um risco raro por uma parada garantida.</para>
    /// </remarks>
    private async Task<string?> GarantirTelaSemRascunhoAsync(CancellationToken ct)
    {
        if (LerEstadoDoCampo(await DumpUiAsync(ct)) is not EstadoDoCampo.ComTexto)
        {
            return null;
        }
        // Os mesmos dois BACK da limpeza de falha: sair da conversa faz o WhatsApp guardar o texto como
        // RASCUNHO daquela conversa, em vez de deixá-lo na tela onde o próximo envio tropeça nele.
        await DispensarDialogoAsync(ct);
        await Task.Delay(600, ct);
        return LerEstadoDoCampo(await DumpUiAsync(ct)) is not EstadoDoCampo.ComTexto
            ? null
            : "a tela do aparelho está numa conversa com texto pendente no campo e não consegui sair "
              + "dela. Não enviei: daqui o toque em enviar iria para a conversa errada. Confira o "
              + "aparelho (alguém digitando nele?) e retome.";
    }

    private async Task<WhatsAppSendResult> SendByDeepLinkAsync(string digits, string text, CancellationToken ct)
    {
        var url = WhatsAppUi.DeepLink(digits, text);
        if (url.Contains('\'', StringComparison.Ordinal))
        {
            return WhatsAppSendResult.Fail("texto gerou aspa simples (não esperado no URL-encode).");
        }

        await _uiLock.WaitAsync(ct);
        var tocouEnviar = false;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, _opts.WhatsAppSendTimeoutSeconds)));
            var sct = sendCts.Token;

            // ANTES da leitura de tela, não depois: a pré-condição abaixo lê a árvore, e a árvore de um
            // aparelho dormindo é a do keyguard. Sem acordar primeiro, ela julgaria a tela errada.
            await AcordarAparelhoAsync(sct);

            if (await GarantirTelaSemRascunhoAsync(sct) is { } sujeira)
            {
                return WhatsAppSendResult.Fail(sujeira);
            }

            var (rc, outp, err) = await OpenChatAsync(url, sct);
            if (rc != 0)
            {
                return WhatsAppSendResult.Fail(string.IsNullOrWhiteSpace(err) ? outp : err);
            }
            var send = await PollNodeCenterAsync(SendNodeRx, _opts.WhatsAppOpenWaitMs, sct);
            if (send is null)
            {
                // 🔴 DIAGNOSTICA ANTES DE LIMPAR. O diálogo de "não está no WhatsApp" também tem um
                // botão OK, então dispensar primeiro apagaria a única prova de que a causa é o NÚMERO,
                // e a falha voltaria a ser classificada como problema de aparelho.
                var (diagnostico, tela) = await DiagnosticarChatNaoAbertoAsync(sct);
                if (diagnostico.NoWhatsAppAccount)
                {
                    return diagnostico;
                }

                var evidencia = await TentarDesbloquearTelaAsync(tela, sct);
                send = await PollNodeCenterAsync(
                    SendNodeRx, Math.Min(_opts.WhatsAppOpenWaitMs, 3000), sct);
                if (send is null)
                {
                    // Devolve o diagnóstico ORIGINAL: depois do BACK a tela mudou, e um segundo
                    // diagnóstico descreveria a tela que eu mesmo criei, não a que causou a falha.
                    // A evidência da tela desconhecida vem ANEXADA, não no lugar: ela descreve o que
                    // bloqueou, enquanto o diagnóstico diz o que o envio concluiu.
                    return evidencia is null
                        ? diagnostico
                        : diagnostico with { Error = diagnostico.Error + evidencia };
                }
            }

            // Daqui pra frente NADA é conclusivo. O toque é irreversível e não tem confirmação síncrona:
            // tudo o que vier depois só pode dizer "confirmei que saiu" ou "não confirmei", nunca
            // "garanto que não saiu". A marca existe pra o catch lá embaixo saber de que lado do toque
            // o tempo estourou.
            tocouEnviar = true;
            await TapAsync(send.Value, sct);

            return await PollEntryClearedAsync(_opts.WhatsAppSendWaitMs, sct) switch
            {
                EstadoDoCampo.Vazio => WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct)),
                EstadoDoCampo.ComTexto => WhatsAppSendResult.Fail(
                    "toquei enviar e o texto CONTINUA no campo: a mensagem não saiu."),
                _ => WhatsAppSendResult.Unconfirmed(
                    "toquei enviar mas não consegui ler a tela pra confirmar. Pode ter saído; não vou "
                    + "tentar de novo sozinho pra não mandar duas vezes."),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            const string motivo = "envio excedeu o tempo total (aparelho lento/travado?).";
            return tocouEnviar
                ? WhatsAppSendResult.Unconfirmed(motivo + " O toque em enviar JÁ tinha acontecido, então "
                    + "pode ter saído.")
                : WhatsAppSendResult.Fail(motivo + " Estourou antes de tocar enviar, então nada saiu.");
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
        var tocouEnviar = false;
        try
        {
            // Teto calculado: digitar é a única etapa cujo custo depende do TAMANHO da mensagem.
            var typingBudget = TimeSpan.FromMinutes(
                2.0 * text.Length / Math.Clamp(_opts.TypingCharsPerMinute, 60, 1200));
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(
                TimeSpan.FromSeconds(Math.Max(60, _opts.WhatsAppSendTimeoutSeconds)) + typingBudget);
            var sct = sendCts.Token;

            // Mesmo motivo do caminho do deep link: sem isto, o primeiro envio de cada bloco encontra o
            // aparelho dormindo e falha como se a conversa não tivesse aberto.
            await AcordarAparelhoAsync(sct);

            var (rc, outp, err) = await OpenChatAsync(WhatsAppUi.DeepLinkEmpty(digits), sct);
            if (rc != 0)
            {
                return WhatsAppSendResult.Fail(string.IsNullOrWhiteSpace(err) ? outp : err);
            }

            var entry = await PollNodeCenterAsync(EntryNodeRx, _opts.WhatsAppOpenWaitMs, sct);
            if (entry is null)
            {
                // 🔴 O MESMO TRATAMENTO DO OUTRO CAMINHO, e não é simetria decorativa: este aqui é o
                // que roda com digitação humana LIGADA, que é o default. Enquanto só o deep link
                // diagnosticava, um número sem conta chegava ao console como "a conversa não abriu",
                // ou seja, entrava no contador de falha de APARELHO e disparava o alerta de celular
                // travado com o celular perfeito.
                var (diagnostico, tela) = await DiagnosticarChatNaoAbertoAsync(sct);
                if (diagnostico.NoWhatsAppAccount)
                {
                    return diagnostico;
                }

                var evidencia = await TentarDesbloquearTelaAsync(tela, sct);
                entry = await PollNodeCenterAsync(
                    EntryNodeRx, Math.Min(_opts.WhatsAppOpenWaitMs, 3000), sct);
                if (entry is null)
                {
                    return WhatsAppSendResult.Fail(
                        "campo de mensagem não apareceu (a conversa não abriu)." + evidencia);
                }
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

                var (motivo, jaSaiuAlgo) = await TypeInChunksAsync(typing, text, sct);
                if (motivo is not null)
                {
                    await ClearEntryAsync(typing, sct);
                    // 🔴 "Digitação abortada" nem sempre significa "nada saiu". Com a opção "Tecla Enter
                    // para enviar" LIGADA, o Enter da primeira quebra de linha JÁ mandou a mensagem
                    // pela metade — o aborto impede o resto, não desfaz o que foi. Devolver isso como
                    // falha conclusiva fazia o console reabrir a conversa na outra forma do número e
                    // mandar de novo, somando uma segunda mensagem à metade que a pessoa já recebeu.
                    return jaSaiuAlgo
                        ? WhatsAppSendResult.Unconfirmed($"digitação abortada, campo limpo. {motivo}")
                        : WhatsAppSendResult.Fail($"digitação abortada, campo limpo. {motivo}");
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
                await RestoreImeAsync(previousIme);
            }

            await Task.Delay(Random.Shared.Next(700, 2200), sct);
            var send = await PollNodeCenterAsync(SendNodeRx, _opts.WhatsAppSendWaitMs, sct);
            if (send is null)
            {
                await ClearEntryAsync(typing, sct);
                return WhatsAppSendResult.Fail("botão enviar não apareceu mesmo com o campo preenchido.");
            }

            // Ver a mesma marca no caminho do deep link: depois do toque não existe mais conclusão
            // negativa, só confirmação ou ausência dela.
            tocouEnviar = true;
            await TapAsync(send.Value, sct);

            return await PollEntryClearedAsync(_opts.WhatsAppSendWaitMs, sct) switch
            {
                EstadoDoCampo.Vazio => WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct)),
                EstadoDoCampo.ComTexto => WhatsAppSendResult.Fail(
                    "toquei enviar e o texto CONTINUA no campo: a mensagem não saiu."),
                _ => WhatsAppSendResult.Unconfirmed(
                    "toquei enviar mas não consegui ler a tela pra confirmar. Pode ter saído; não vou "
                    + "tentar de novo sozinho pra não mandar duas vezes."),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var motivo = $"envio excedeu o teto (base {_opts.WhatsAppSendTimeoutSeconds}s + orçamento de "
                + $"digitação pra {text.Length} caracteres a {_opts.TypingCharsPerMinute} cpm).";
            return tocouEnviar
                ? WhatsAppSendResult.Unconfirmed(motivo + " O toque em enviar JÁ tinha acontecido, então "
                    + "pode ter saído.")
                : WhatsAppSendResult.Fail(motivo + " Estourou antes de tocar enviar, então nada saiu.");
        }
        finally
        {
            _uiLock.Release();
        }
    }

    /// <summary>Consegue digitar este texto? null = sim; string = motivo, pra avisar ANTES do lote.</summary>
    public async Task<string?> CheckTypingCapabilityAsync(string sampleText, CancellationToken ct)
    {
        if (!_opts.HumanTyping)
        {
            return null; // deep link entrega o texto pronto no campo: não há digitação pra falhar
        }
        if (await ResolveTypingChannelAsync(sampleText ?? string.Empty, ct) is not null)
        {
            return null;
        }
        return $"o texto tem acento e/ou emoji, e este aparelho não tem o teclado {_opts.TypingImePackage} "
            + "instalado. O `input text` do Android não digita esses caracteres, então TODOS os envios "
            + "vão falhar. Instale o IME no aparelho, ou use Phone__HumanTyping=false (o texto vai pronto "
            + "pelo deep link, sem simular digitação).";
    }

    private Task<(int Code, string Out, string Err)> OpenChatAsync(string url, CancellationToken ct) =>
        // Aspas simples: '&' e '#' da URL não podem ser interpretados pelo shell do aparelho.
        _adb.ShellAsync($"am start -a android.intent.action.VIEW -d '{url}'", ct);

    /// <summary>Acorda a tela e tira o cadeado simples da frente, antes de abrir a conversa.</summary>
    /// <remarks>
    /// 🔴 O CASO QUE MOTIVOU (2026-08-10): a tela do aparelho apaga sozinha em 10 min e a pausa entre
    /// blocos é de ~30. Ou seja, TODO primeiro envio depois de uma pausa encontrava o aparelho dormindo.
    /// O `am start` despacha a intent, mas a Activity nasce ATRÁS do keyguard: nem `id/entry` nem
    /// `id/send` entram na árvore, e o envio falhava como "a conversa não abriu" — o diagnóstico que
    /// culpa o número. Como a falha era do APARELHO, ela ainda alimentava o contador do disjuntor e o
    /// aviso de celular travado, com o celular perfeito.
    ///
    /// <para>A tentação era encurtar a pausa pra caber nos 10 min da tela. Seria consertar o sintoma
    /// pelo lado errado: a pausa é o que desmancha o padrão de máquina E o que segura o volume diário
    /// no platô de 120 (ver <c>PhoneConsoleCommand._bloco</c>). Acordar o aparelho custa um comando.</para>
    ///
    /// <para><c>KEYCODE_WAKEUP</c> e não <c>KEYCODE_POWER</c>: power é uma CHAVE (liga e desliga), então
    /// numa tela já acesa ele APAGARIA a tela — o caminho mais direto de transformar uma proteção em
    /// defeito. WAKEUP é idempotente: acende, e não faz nada se já estiver acesa.</para>
    ///
    /// <para><c>wm dismiss-keyguard</c> resolve o cadeado SEM senha (deslize). Com PIN ou biometria ele
    /// só traz o pedido de senha pra frente, e aí não há o que este driver possa fazer: o aparelho de
    /// disparo tem que estar sem bloqueio de tela. É por isso que a falha continua sendo diagnosticada
    /// (ver <see cref="TelaBloqueada"/>) em vez de silenciosamente tentada de novo.</para>
    ///
    /// <para>Best-effort: aparelho que não conhece um destes comandos segue o fluxo normal. Se ainda
    /// estiver dormindo, o poll falha e a captura de tela guarda a prova.</para>
    /// </remarks>
    private async Task AcordarAparelhoAsync(CancellationToken ct)
    {
        await _adb.ShellAsync("input keyevent KEYCODE_WAKEUP", ct);
        await _adb.ShellAsync("wm dismiss-keyguard", ct);
        // A animação de destravar leva um instante, e um dump tirado no meio dela lê a tela antiga.
        await Task.Delay(500, ct);
    }

    /// <summary>A tela lida é a de bloqueio?</summary>
    /// <remarks>
    /// ⚠️ HEURÍSTICA, e os marcadores NÃO foram medidos neste aparelho. São os resource-ids que o
    /// systemui usa no keyguard. Se errar, erra pro lado seguro: o envio já falhou de qualquer forma, e
    /// o que muda é só a frase do erro.
    /// <para>Vale a pena mesmo assim porque separa dois consertos opostos. "A conversa não abriu" manda
    /// conferir número e WhatsApp; "o aparelho está bloqueado" manda tirar o PIN e ligar o "manter tela
    /// ativa". Sem a distinção, o segundo se disfarça de primeiro por semanas.</para>
    /// <para>A tela capturada (ver <see cref="GuardarTela"/>) é o que permite confirmar ou corrigir
    /// estes marcadores sem precisar de alguém na frente do celular.</para>
    ///
    /// <para>🔴 EXIGE O PACOTE <c>com.android.systemui</c>, e isso não é zelo: casar a palavra
    /// "keyguard" ou "lock" solta no XML pega o PRÓPRIO WhatsApp, que tem cadeado de conversa e aviso
    /// de criptografia na tela de conversa. O falso positivo aqui não erra uma ação (o envio já falhou
    /// de qualquer forma), erra a INSTRUÇÃO: mandaria o operador tirar um bloqueio de tela que não
    /// existe enquanto a causa real seguisse intocada. Errar para o lado do diagnóstico genérico é
    /// barato, porque a tela capturada continua lá para ser lida.</para>
    /// </remarks>
    private static bool TelaBloqueada(string xml) =>
        xml.Contains("com.android.systemui:id/keyguard", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("com.android.systemui:id/lock_icon", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("com.android.systemui:id/lockscreen", StringComparison.OrdinalIgnoreCase);

    // ── Leitura de tela ──────────────────────────────────────────────────────────────────────────

    /// <summary>Árvore de acessibilidade da tela atual. null = não deu pra ler.</summary>
    /// <remarks>Não exige root, nem no aparelho físico — confirmado em 2026-07-29.</remarks>
    public async Task<string?> DumpUiAsync(CancellationToken ct)
    {
        // 🔴 APAGA ANTES DE DUMPAR. O caminho é fixo, então o arquivo sobrevive entre chamadas. Há
        // versões do `uiautomator` que falham ("could not get idle state"), imprimem o erro e ainda
        // saem com código 0 — e aí o `cat` devolveria o dump ANTERIOR, com a tela de antes passando
        // por tela de agora. Não medi este A14 fazendo isso, e provocar sob demanda é caro; apagar
        // antes torna a pergunta irrelevante, porque leitura obsoleta vira arquivo ausente, o `cat`
        // falha, e isto devolve null. Null aqui significa "não sei", que é tratado como incerteza em
        // todo mundo que lê a tela.
        // UM comando, não dois. Cada ShellAsync é um processo `adb` novo no PC, e ler a tela é a
        // operação mais repetida deste driver: os polls dumpam a cada 500ms, então um envio faz dezenas
        // de leituras. Juntar `dump` e `cat` numa linha corta METADE dos processos no caminho quente.
        //
        // O `rc` do dump deixa de ser consultado, e isso NÃO afrouxa a checagem de tela obsoleta que o
        // `rm -f` protege: sem o arquivo, um dump que falha faz o `cat` falhar, a saída vem vazia e este
        // método devolve null. É o mesmo desfecho de antes, por um caminho a menos.
        //
        // A saída do dump vai pro lixo porque ele imprime "UI hierchary dumped to: ..." em algumas
        // versões, e isso entraria concatenado ANTES do XML.
        var (_, xml, _) = await _adb.ShellAsync(
            $"rm -f {DumpPath}; uiautomator dump {DumpPath} >/dev/null 2>&1; cat {DumpPath}", ct);
        return string.IsNullOrWhiteSpace(xml) ? null : xml;
    }

    private static readonly Regex BoundsRx =
        new("bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"", RegexOptions.Compiled);

    private static readonly Regex NoRx = new("<node[^>]*>", RegexOptions.Compiled);

    private static readonly Regex TextoRx = new("text=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>content-desc do nó: o rótulo de acessibilidade, único texto de botão só com ícone.</summary>
    private static readonly Regex DescRx = new("content-desc=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>Rótulos de botão que apenas FECHAM um aviso, sem confirmar nada.</summary>
    /// <remarks>
    /// 🔴 LISTA FECHADA, e só palavra neutra. Tocar em botão pelo texto é tocar às cegas, e a tela pode
    /// ter "Bloquear", "Denunciar", "Sair do grupo". Um aviso mal dispensado é um aviso que volta; um
    /// botão errado tocado é irreversível e pode custar o contato ou a conta. Na dúvida, não toca:
    /// existe o BACK como saída, e ele não confirma nada.
    /// </remarks>
    private static readonly string[] BotoesQueSoFecham =
        ["ok", "continuar", "entendi", "fechar", "agora não", "agora nao", "got it", "continue", "close"];

    /// <summary>Nó do botão "enviar" e nó do campo de texto, os dois únicos que o driver procura.</summary>
    /// <remarks>
    /// Compilados e estáticos porque o poll roda a cada 500ms: montar o padrão por interpolação a cada
    /// volta reconstruía a expressão no caminho mais repetido do driver. Como são dois alvos fixos, não
    /// há por que aceitar resource-id em texto — e passar o padrão pronto tira de vez o risco de alguém
    /// montar um com caractere não escapado.
    /// <para>Casa o NÓ INTEIRO, e não o atributo isolado, porque o `text` vem ANTES do `resource-id` no
    /// dump: assim a ordem dos atributos deixa de importar e o texto sai de dentro do nó já encontrado.
    /// A aspa no fim do id não é enfeite — sem ela, <c>id/entry</c> casaria também um eventual
    /// <c>id/entry_qualquer_coisa</c>.</para>
    /// </remarks>
    private static readonly Regex SendNodeRx =
        new("<node[^>]*com\\.whatsapp:id/send\"[^>]*>", RegexOptions.Compiled);

    /// <inheritdoc cref="SendNodeRx"/>
    private static readonly Regex EntryNodeRx =
        new("<node[^>]*com\\.whatsapp:id/entry\"[^>]*>", RegexOptions.Compiled);

    /// <summary>Centro do nó procurado, com POLL até o timeout. null = não apareceu.</summary>
    /// <remarks>Poll e não sleep fixo: chat lento (cold start, rede ruim) abriria depois da espera e o
    /// envio seria abortado à toa.</remarks>
    private async Task<(int X, int Y)?> PollNodeCenterAsync(Regex no, int timeoutMs, CancellationToken ct)
    {
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            if (await DumpUiAsync(ct) is { } xml
                && no.Match(xml) is { Success: true } achado
                && CentroDe(achado.Value) is { } centro)
            {
                return centro;
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

    /// <summary>O que a tela respondeu sobre o campo de mensagem. TRÊS estados, e o terceiro é o que
    /// importa.</summary>
    /// <remarks>
    /// 🔴 Isto era um bool, e o bool não tinha onde pôr "não consegui ler". O
    /// <c>HasSendButton(string? xml)</c> resolvia o nulo devolvendo false, e false ali significava
    /// "campo vazio", ou seja, ENVIO CONFIRMADO. Dump falho virava prova de entrega: o contato saía da
    /// lista, o CSV gravava "sim" e a pessoa podia não ter recebido nada.
    ///
    /// <para>E o agravante estava no relógio. O primeiro poll roda LOGO DEPOIS do toque, com a tela
    /// animando, que é quando o `uiautomator dump` mais falha ("could not get idle state"). Como o
    /// primeiro sucesso encerrava o laço, o dump com maior chance de falhar era justamente aquele cuja
    /// falha virava sucesso.</para>
    ///
    /// <para>O <see cref="PollNodeCenterAsync"/>, no mesmo arquivo, sempre tratou o nulo certo
    /// (<c>if (xml is not null)</c>). Eram dois tratamentos opostos da mesma incerteza a poucas linhas
    /// um do outro, e o daqui nunca foi decidido: veio junto com o operador de nulo.</para>
    /// </remarks>
    private enum EstadoDoCampo
    {
        /// <summary>Botão de enviar na tela: tem texto no campo.</summary>
        ComTexto,

        /// <summary>Tela LIDA, e sem botão de enviar: o campo está vazio.</summary>
        Vazio,

        /// <summary>Não deu pra ler a tela. NÃO é vazio, é desconhecido.</summary>
        NaoLido,
    }

    private static EstadoDoCampo LerEstadoDoCampo(string? xml) =>
        xml is null ? EstadoDoCampo.NaoLido
        : xml.Contains("com.whatsapp:id/send\"", StringComparison.Ordinal) ? EstadoDoCampo.ComTexto
        : EstadoDoCampo.Vazio;

    /// <summary>Depois de tocar enviar: o campo esvaziou?</summary>
    /// <returns>
    /// <see cref="EstadoDoCampo.Vazio"/> = envio confirmado.
    /// <see cref="EstadoDoCampo.ComTexto"/> = o texto continua lá, então NÃO saiu (conclusivo).
    /// <see cref="EstadoDoCampo.NaoLido"/> = nunca consegui ler a tela; não dá pra afirmar nada.
    /// </returns>
    private async Task<EstadoDoCampo> PollEntryClearedAsync(int timeoutMs, CancellationToken ct)
    {
        // Só vira conclusão quando ALGUMA leitura deu certo. Sem isso, um poll inteiro de dumps falhos
        // devolveria a mesma coisa que um poll que viu o texto parado no campo, e são fatos diferentes:
        // um é "não saiu", o outro é "não sei".
        var conclusao = EstadoDoCampo.NaoLido;
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            var estado = LerEstadoDoCampo(await DumpUiAsync(ct));
            if (estado is EstadoDoCampo.Vazio)
            {
                return EstadoDoCampo.Vazio;
            }
            if (estado is EstadoDoCampo.ComTexto)
            {
                conclusao = EstadoDoCampo.ComTexto;
            }
            if (i < attempts)
            {
                await Task.Delay(500, ct);
            }
        }
        return conclusao;
    }

    private static string? EntryTextFrom(string? xml)
    {
        var node = xml is null ? null : EntryNodeRx.Match(xml);
        if (node is not { Success: true })
        {
            return null;
        }
        var t = TextoRx.Match(node.Value);
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
    // A QUEBRA DE LINHA é exceção: ela não passa como texto, mas passa como TECLA (KEYCODE_ENTER), e
    // é assim que SendChunkAsync a envia. Confirmado com o operador em 2026-07-30 que a opção
    // "Tecla Enter para enviar" do WhatsApp está DESLIGADA neste aparelho — com ela ligada, o Enter
    // mandaria a mensagem pela metade.
    private static bool IsTypeableByInputText(string text) =>
        text.All(c => c == '\n' || (c is >= ' ' and <= '~' && c != '\''));

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

    /// <summary>Devolve o teclado de antes. SEM token do envio, de propósito.</summary>
    /// <remarks>
    /// 🔴 Isto recebia o token do envio e rodava num `finally`. Quando o envio acabava por CANCELAMENTO
    /// (Ctrl+C do operador, ou o teto de tempo estourando), o token já estava cancelado, o
    /// <c>DockerCli.RunAsync</c> relançava, e o `ime set` de volta simplesmente não acontecia. Ou seja:
    /// funcionava em todos os caminhos MENOS naqueles em que era necessário.
    ///
    /// <para>O estrago é no aparelho, não no lote. O IME de digitação não desenha teclas (ver
    /// <see cref="SelectTypingImeAsync"/>), então o celular ficava sem teclado utilizável para quem o
    /// pegasse na mão, e "o teclado sumiu" não aponta pra cá. Ctrl+C é o jeito documentado de
    /// interromper um lote, então esse era o caminho comum, não o raro.</para>
    ///
    /// <para>Token próprio com teto curto, e engolindo exceção: é limpeza, e limpeza não pode nem
    /// pendurar o encerramento nem mascarar o erro que o operador precisa ler.</para>
    /// </remarks>
    private async Task RestoreImeAsync(string? previous)
    {
        if (string.IsNullOrWhiteSpace(previous))
        {
            return;
        }
#pragma warning disable CA1031 // limpeza best-effort: ver o remarks
        try
        {
            using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _adb.ShellAsync($"ime set {previous}", restoreCts.Token);
        }
        catch
        {
            // aparelho sumiu: o teclado fica pro operador arrumar, e o erro do envio é o que importa
        }
#pragma warning restore CA1031
    }

    /// <summary>Esvazia o campo. false = não consegui confirmar que ficou vazio.</summary>
    private async Task<bool> ClearEntryAsync(TypingChannel channel, CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            // 🔴 SÓ `Vazio` encerra. Antes, dump falho também encerrava (o bool não separava "li e o
            // campo está vazio" de "não li"), e aí a digitação começava por cima de um campo que podia
            // estar cheio. Não ler é motivo pra dar outra volta, nunca pra concluir.
            if (LerEstadoDoCampo(await DumpUiAsync(ct)) is EstadoDoCampo.Vazio)
            {
                return true; // sem botão de enviar = campo vazio
            }
            if (channel is TypingChannel.Ime)
            {
                await _adb.ShellAsync("am broadcast -a ADB_CLEAR_TEXT", ct);
            }
            else
            {
                // Sem IME não há comando de limpar. SELECIONAR TUDO e apagar resolve em um golpe,
                // independente do tamanho: CTRL+A (keycombination existe do Android 11 pra cima) e um
                // backspace, que apaga a seleção inteira.
                var (kc, _, _) = await _adb.ShellAsync("input keycombination 113 29", ct);
                if (kc == 0)
                {
                    await _adb.ShellAsync("input keyevent 67", ct);
                }
                else
                {
                    // 🔴 Fallback pra Android antigo. 120 por volta × 12 voltas = 1440 caracteres, com
                    // folga sobre o maior template (~1000). Antes eram 40 por volta = teto de 480, que
                    // NÃO limpava um template e deixava o envio abortado com o campo sujo — e o campo
                    // sujo faz a digitação seguinte entrar em cima da anterior.
                    await _adb.ShellAsync("input keyevent " + string.Join(" ", Enumerable.Repeat("67", 120)), ct);
                }
            }
            await Task.Delay(300, ct);
        }
        return false;
    }

    /// <summary>Digita o texto.</summary>
    /// <returns>
    /// <c>Motivo</c> null = digitou tudo. <c>PodeTerSaido</c> = alguma coisa JÁ foi para o destinatário
    /// antes da parada, então abortar não desfaz nada e reenviar somaria uma segunda mensagem.
    /// </returns>
    private async Task<(string? Motivo, bool PodeTerSaido)> TypeInChunksAsync(
        TypingChannel channel, string text, CancellationToken ct)
    {
        var perChar = 60_000.0 / Math.Clamp(_opts.TypingCharsPerMinute, 60, 1200);
        var quebraConferida = false;
        foreach (var chunk in SplitForTyping(text))
        {
            if (!await SendChunkAsync(channel, chunk, ct))
            {
                return ("o aparelho recusou um trecho da digitação.", false);
            }

            // 🔴 Confere a PRIMEIRA quebra de linha: se a opção "Tecla Enter para enviar" estiver
            // ligada no WhatsApp, o Enter manda a mensagem em vez de quebrar a linha. Sem esta
            // checagem, um texto de 4 linhas viraria 4 mensagens picadas, uma por Enter. Aqui o
            // estrago para na primeira: aborta antes de digitar o resto.
            if (chunk == "\n" && !quebraConferida)
            {
                quebraConferida = true;
                if (string.IsNullOrEmpty(await ReadEntryTextAsync(ct)))
                {
                    // PodeTerSaido = true: o Enter MANDOU o que já estava digitado. Parar aqui evita as
                    // outras três mensagens picadas, mas a primeira já chegou ao destinatário.
                    return ("o campo esvaziou na primeira quebra de linha: a opção \"Tecla Enter para "
                        + "enviar\" está LIGADA no WhatsApp deste aparelho, então o Enter mandou a "
                        + "mensagem em vez de quebrar a linha. O começo do texto JÁ FOI ENVIADO. "
                        + "Desligue a opção (WhatsApp → Configurações → Conversas), ou use templates de "
                        + "uma linha só.", true);
                }
            }
            var waitMs = (int)(chunk.Length * perChar * (0.75 + (Random.Shared.NextDouble() * 0.7)));
            // De vez em quando alguém para pra pensar. Sem isso a cadência é uniforme demais.
            if (Random.Shared.Next(100) < 12)
            {
                waitMs += Random.Shared.Next(700, 2400);
            }
            await Task.Delay(waitMs, ct);
        }
        return (null, false);
    }

    private async Task<bool> SendChunkAsync(TypingChannel channel, string chunk, CancellationToken ct)
    {
        // Quebra de linha vai como TECLA, nos dois canais: nem o `input text` nem o broadcast do IME
        // carregam o caractere 10. É o Enter do teclado, que no campo do WhatsApp quebra a linha
        // enquanto "Tecla Enter para enviar" estiver desligado.
        if (chunk == "\n")
        {
            var (ec, _, _) = await _adb.ShellAsync("input keyevent 66", ct);
            return ec == 0;
        }
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
                // A quebra sai SOZINHA no seu próprio trecho: SendChunkAsync a reconhece e manda a
                // tecla. Junto com o texto ela iria pro `input text`, que não a digita.
                if (nl > i)
                {
                    yield return text[i..nl];
                }
                yield return "\n";
                i = nl + 1;
                continue;
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
