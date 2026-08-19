using System.Globalization;
using System.Text.RegularExpressions;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Um Android de mentira: agenda (contacts provider) e tela do WhatsApp como máquina de
/// estados, atrás do mesmo <see cref="IAdbRunner"/> que o aparelho de verdade.</summary>
/// <remarks>
/// <para>Serve pro teste afirmar sobre O QUE O APARELHO RECEBEU, e não sobre o que o método devolveu.
/// A diferença é o ponto: quase todos os defeitos achados nesta área eram exatamente um valor de
/// retorno dizendo uma coisa e o aparelho estando noutra.</para>
/// <para>Modela só o que o fluxo toca, e modela HONESTO nos pontos que importam: o deep link pode não
/// navegar mesmo devolvendo 0, o `uiautomator dump` pode falhar, o rascunho fica guardado por conversa
/// quando se sai dela, e a agenda tem outro escritor. Esses quatro são a origem dos bugs, não detalhe
/// de cenário.</para>
/// </remarks>
internal sealed class AndroidFalso : IAdbRunner
{
    // ── Agenda ───────────────────────────────────────────────────────────────────────────────────

    internal sealed record RawContact(int Id)
    {
        public string? Nome { get; set; }

        public string? Telefone { get; set; }
    }

    private readonly Dictionary<int, RawContact> _agenda = [];
    private int _proximoId;

    /// <summary>Roda logo depois de cada insert de raw contact, pra simular o sync do WhatsApp ou da
    /// conta Google escrevendo na MESMA janela em que estamos gravando.</summary>
    public Action? AoCriarRawContact { get; set; }

    public IReadOnlyDictionary<int, RawContact> Agenda => _agenda;

    /// <summary>Cria um contato "de outra pessoa", como o sync faria.</summary>
    public RawContact CriarContatoDeTerceiro(string nome, string telefone)
    {
        var c = new RawContact(++_proximoId) { Nome = nome, Telefone = telefone };
        _agenda[c.Id] = c;
        return c;
    }

    // ── WhatsApp ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Números que TÊM conta no WhatsApp. Só estes abrem conversa pelo deep link.</summary>
    public HashSet<string> ContasExistentes { get; } = new(StringComparer.Ordinal);

    /// <summary>O que de fato SAIU do aparelho: (conversa, texto). É a asserção que vale.</summary>
    public List<(string Conversa, string Texto)> Entregues { get; } = [];

    private string? _conversaAberta;
    private bool _dialogoSemConta;

    /// <summary>Conversas que abrem com o aviso de MENSAGENS TEMPORÁRIAS por cima.</summary>
    /// <remarks>
    /// O aviso esconde o campo e o botão de enviar enquanto está na tela, que é o que fazia o envio
    /// falhar com "a conversa não abriu". Some com o botão OK ou com BACK.
    /// </remarks>
    public HashSet<string> ComAvisoTemporaria { get; } = new(StringComparer.Ordinal);

    /// <summary>O aviso está na tela AGORA (foi aberto e ainda não dispensado).</summary>
    public bool AvisoNaTela { get; private set; }

    /// <summary>Conversas que abrem com o aviso DESENHADO POR CIMA do chat, sem tirá-lo da tela.</summary>
    /// <remarks>
    /// 🔴 A diferença pro <see cref="ComAvisoTemporaria"/> NÃO é cosmética, é a que faltava modelar. O
    /// `uiautomator dump` despeja a Activity INTEIRA, então um aviso desenhado sobre a conversa não
    /// apaga o `com.whatsapp:id/entry` da árvore: o driver continua achando o campo, conclui que a
    /// conversa abriu e digita contra um toque que morre no aviso. O sintoma chega ao console como
    /// "campo ficou com nada caracteres", que acusa o teclado.
    /// <para>Enquanto o falso só sabia esconder o campo, esse caminho inteiro ficava sem teste: os dois
    /// avisos parecem o mesmo defeito pro operador e são estados diferentes pro código. Relatado
    /// operando em 2026-08-18.</para>
    /// </remarks>
    public HashSet<string> ComAvisoSobreposto { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc cref="ComAvisoSobreposto"/>
    public bool AvisoSobrepostoNaTela { get; private set; }

    /// <summary>O dump lista a janela do aviso ANTES da janela da conversa.</summary>
    /// <remarks>
    /// 🔴 EXISTE PORQUE A ORDEM É PALPITE, E PALPITE FIXO NO FALSO VIRA TESTE QUE PROVA A SI MESMO. O
    /// `uiautomator dump` não despeja uma árvore só: despeja as JANELAS da tela, uma atrás da outra,
    /// como filhas de `&lt;hierarchy&gt;`. Um diálogo é outra janela, e quem decide a ordem entre elas é
    /// o `uiautomator`, que serializa a ATIVA primeiro — e a ativa é justamente o modal.
    ///
    /// <para>A primeira versão do falso emitia o aviso DEPOIS da conversa porque era o que a primeira
    /// versão do driver sabia ler. Os dois compartilhavam a mesma suposição, então o verde não dizia
    /// nada sobre o aparelho: dizia que o falso concordava com o driver. Com o padrão invertido para
    /// ANTES (a ordem provável de verdade) e um teste em cada valor, a leitura passa a não depender de
    /// ordem nenhuma, que é a única garantia que sobrevive a não ter o dump real em mãos.</para>
    /// </remarks>
    public bool AvisoAntesDaConversa { get; set; } = true;

    private const int AvisoOkX1 = 400;
    private const int AvisoOkY1 = 1200;
    private const int AvisoOkX2 = 600;
    private const int AvisoOkY2 = 1300;
    private readonly Dictionary<string, string> _rascunhos = new(StringComparer.Ordinal);

    /// <summary>Quantos deep links seguintes devem devolver 0 SEM navegar.</summary>
    /// <remarks>
    /// 🔴 Não é maldade do teste: `am start` devolver 0 significa que a intent foi despachada, não que o
    /// app trocou de tela. App ocupado, cold start e animação produzem exatamente isso. É a condição que
    /// deixava o toque em enviar cair na conversa ANTERIOR.
    /// </remarks>
    public int NavegacoesIgnoradas { get; set; }

    /// <summary>Quantos `uiautomator dump` seguintes devem falhar.</summary>
    public int DumpsFalhando { get; set; }

    /// <summary>Só falha os dumps DEPOIS que a primeira mensagem sair (pra mirar a janela real: a tela
    /// animando logo após o toque é quando o dump mais falha).</summary>
    public bool FalharDumpApenasAposEnvio { get; set; }

    public string? ConversaAberta => _conversaAberta;

    public string RascunhoDe(string conversa) => _rascunhos.GetValueOrDefault(conversa, "");

    /// <summary>Abre uma conversa e deixa texto no campo, sem enviar. Reproduz a sobra de uma tentativa
    /// anterior que foi abortada no meio.</summary>
    public void DeixarRascunhoAberto(string conversa, string texto)
    {
        _conversaAberta = conversa;
        _rascunhos[conversa] = texto;
    }

    // Coordenadas do botão de enviar. O driver toca com desvio de alguns pixels, então o teste também
    // confere, de graça, que o desvio não sai do botão.
    private const int SendX1 = 900, SendY1 = 1800, SendX2 = 1000, SendY2 = 1900;

    // ── Transporte ───────────────────────────────────────────────────────────────────────────────

    public List<string> Comandos { get; } = [];

    public bool SupportsRoot => false;

    public string Target => "android-falso";

    public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
        Task.FromResult((0, "", ""));

    public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct)
    {
        Comandos.Add(command);
        return Task.FromResult(Executar(command));
    }

    private (int, string, string) Executar(string cmd)
    {
        if (cmd.Contains("phone_lookup", StringComparison.Ordinal))
        {
            return PhoneLookup(cmd);
        }
        if (cmd.Contains("content query", StringComparison.Ordinal)
            && Regex.Match(cmd, @"raw_contacts/(\d+)/data") is { Success: true } rd)
        {
            return DadosDoRawContact(rd.Groups[1].Value);
        }
        if (cmd.Contains("content query", StringComparison.Ordinal)
            && Regex.Match(cmd, @"raw_contacts.*--where contact_id=(\d+)") is { Success: true } rw)
        {
            return RawContactsDoContato(rw.Groups[1].Value);
        }
        if (cmd.Contains("content query", StringComparison.Ordinal)
            && cmd.Contains("raw_contacts", StringComparison.Ordinal))
        {
            // ⚠️ SEM os espelhos de propósito, e é uma fronteira consciente deste falso. No aparelho
            // eles aparecem aqui também, e é exatamente por isso que o SaveContactAsync trata salto de
            // id como corrida. Quem modela essa corrida é o `AoCriarRawContact`, que a põe na janela
            // exata; despejar espelhos aqui só empurraria todo teste de gravação para o ramo de recusa.
            var linhas = string.Join('\n', _agenda.Keys.Order().Select((id, i) => $"Row: {i} _id={id}"));
            return (0, linhas, "");
        }
        if (cmd.Contains("content query", StringComparison.Ordinal)
            && cmd.Contains("/contacts/", StringComparison.Ordinal))
        {
            return ContactData(cmd);
        }
        if (cmd.Contains("content insert", StringComparison.Ordinal)
            && cmd.Contains("raw_contacts", StringComparison.Ordinal))
        {
            _agenda[++_proximoId] = new RawContact(_proximoId);
            AoCriarRawContact?.Invoke();
            return (0, "", "");
        }
        if (cmd.Contains("content insert", StringComparison.Ordinal)
            && cmd.Contains("/data", StringComparison.Ordinal))
        {
            return InserirDado(cmd);
        }
        if (cmd.StartsWith("am start", StringComparison.Ordinal))
        {
            return DeepLink(cmd);
        }
        if (cmd.Contains("uiautomator dump", StringComparison.Ordinal))
        {
            // O driver manda `rm -f`, `dump` e `cat` NUMA LINHA SÓ (ver DumpUiAsync: cada ShellAsync é
            // um processo adb novo, e ler a tela é a operação mais repetida do envio). O fake modela os
            // três passos na ordem, inclusive o apagar — que antes não era modelado e é justamente o
            // que impede o `cat` de devolver a tela ANTERIOR quando o dump falha.
            if (cmd.Contains("rm -f", StringComparison.Ordinal))
            {
                _dumpGravado = null;
            }
            if (DumpsFalhando > 0 && (!FalharDumpApenasAposEnvio || Entregues.Count > 0))
            {
                DumpsFalhando--;
                // Dump falhou: o arquivo continua ausente, então o `cat` da mesma linha não devolve nada.
                return (1, "", "ERROR: could not get idle state.");
            }
            _dumpGravado = RenderizarTela();
            return cmd.Contains("cat ", StringComparison.Ordinal)
                ? (0, _dumpGravado, "")
                : (0, "UI hierchary dumped to: /sdcard/mtrx_ui.xml", "");
        }
        if (cmd.StartsWith("cat ", StringComparison.Ordinal))
        {
            return _dumpGravado is null ? (1, "", "No such file or directory") : (0, _dumpGravado, "");
        }
        if (cmd.StartsWith("input tap", StringComparison.Ordinal))
        {
            return Tap(cmd);
        }
        if (cmd.Contains("KEYCODE_BACK", StringComparison.Ordinal))
        {
            return Back();
        }
        if (cmd.StartsWith("ime list", StringComparison.Ordinal))
        {
            return (0, "com.android.inputmethod.latin/.LatinIME", ""); // sem o IME de broadcast
        }
        if (cmd.StartsWith("settings get", StringComparison.Ordinal))
        {
            return (0, "com.android.inputmethod.latin/.LatinIME", "");
        }
        if (cmd.StartsWith("input text", StringComparison.Ordinal))
        {
            return InputText(cmd);
        }
        if (cmd.Contains("keyevent 66", StringComparison.Ordinal))
        {
            return Digitar("\n");
        }
        if (cmd.Contains("keycombination", StringComparison.Ordinal)
            || cmd.Contains("keyevent 67", StringComparison.Ordinal)
            || cmd.Contains("ADB_CLEAR_TEXT", StringComparison.Ordinal))
        {
            if (_conversaAberta is not null)
            {
                _rascunhos[_conversaAberta] = "";
            }
            return (0, "", "");
        }
        return (0, "", "");
    }

    // O arquivo do dump é REAL aqui: `rm -f` antes de dumpar tem que se refletir em `cat` falhando,
    // senão o teste não conseguiria distinguir leitura obsoleta de leitura ausente.
    private string? _dumpGravado;

    // ── Agenda: implementação ────────────────────────────────────────────────────────────────────

    /// <summary>Casa pela SEQUÊNCIA DE DÍGITOS, com o "+" e a formatação normalizados fora dos dois
    /// lados.</summary>
    /// <remarks>
    /// 🔴 MEDIDO em 2026-08-06 no Galaxy A14 (SM_A145M, Android 15), contra um contato gravado como
    /// "+55DDD9XXXXXXXX" e com contact_id conhecido:
    /// <code>
    /// adb shell content query --uri content://com.android.contacts/phone_lookup/&lt;valor&gt; --projection contact_id
    ///   %2B + digitos                    -> ACHOU (mesmo id)
    ///   digitos crus                     -> ACHOU (mesmo id)
    ///   últimos 7 dígitos                -> não achou
    ///   forma irmã (com/sem o 9º dígito) -> não achou
    /// </code>
    /// Comparar dígitos cobre os quatro resultados de uma vez: o "+" some na normalização, os últimos 7
    /// não são a sequência inteira, e a forma irmã é outra sequência.
    /// <para>Este falso ANTES casava por string exata, seguindo o que o código afirmava ter medido. A
    /// medição derrubou a afirmação, e um falso otimista faria os testes validarem uma ficção — por isso
    /// ele mudou junto.</para>
    /// </remarks>
    private (int, string, string) PhoneLookup(string cmd)
    {
        var m = Regex.Match(cmd, @"phone_lookup/(\S+)");
        var procurado = SoDigitos(Uri.UnescapeDataString(m.Groups[1].Value));
        var achado = _agenda.Values.FirstOrDefault(
            c => c.Telefone is not null && SoDigitos(c.Telefone) == procurado);
        return achado is null
            ? (0, "No result found.", "")
            : (0, $"Row: 0 contact_id={achado.Id.ToString(CultureInfo.InvariantCulture)}", "");
    }

    /// <summary>Faz a leitura das linhas de dados do contato falhar, para exercitar o "não sei" de quem
    /// precisa decidir COMO o número está gravado.</summary>
    public bool FalharLeituraDeDados { get; set; }

    /// <summary>As linhas do CONTATO AGREGADO. Nunca traz o espelho do WhatsApp.</summary>
    /// <remarks>
    /// 🔴 ESTE FALSO ERA OTIMISTA AQUI, e o custo foi um lote inteiro segurado no aparelho de verdade
    /// enquanto os testes passavam. Ele devolvia o espelho por este URI; a One UI NÃO devolve.
    ///
    /// <para>MEDIDO em 2026-08-12 no Galaxy A14 (SM_A145M, Android 15), contato 845 = +5511980370241:
    /// <c>contacts/845/data</c> traz só `name` e `phone_v2`, enquanto <c>raw_contacts --where
    /// contact_id=845</c> mostra a conta `com.whatsapp` presente. A Samsung filtra a view de dados por
    /// uma coluna própria, <c>sec_supports_uploading</c>, e as linhas de conta local somem.</para>
    ///
    /// <para>Mesma lição do <see cref="PhoneLookup"/>: um falso mais generoso que o aparelho faz o teste
    /// validar uma ficção, e o defeito só aparece em produção — com o console jurando que 27 contatos
    /// bons não tinham WhatsApp.</para>
    /// </remarks>
    private (int, string, string) ContactData(string cmd)
    {
        if (FalharLeituraDeDados)
        {
            return (1, "", "Error while accessing provider");
        }
        var m = Regex.Match(cmd, @"/contacts/(\d+)/data");
        if (!int.TryParse(m.Groups[1].Value, out var id) || !_agenda.TryGetValue(id, out var c))
        {
            return (0, "No result found.", "");
        }
        return (0, $"Row: 0 mimetype=vnd.android.cursor.item/phone_v2 data1={c.Telefone}", "");
    }

    // Ids sintéticos do espelho, derivados do contato pra serem estáveis entre as duas consultas.
    private static int EspelhoDe(int contato) => 10_000 + contato;

    private static int PerfilDe(int contato) => 20_000 + contato;

    /// <summary>Os raw contacts agregados num contato: o nosso e, se o número for usuário, o espelho que
    /// o sync adapter do WhatsApp publica na conta <c>com.whatsapp</c>.</summary>
    private (int, string, string) RawContactsDoContato(string contato)
    {
        if (!int.TryParse(contato, out var id) || !_agenda.TryGetValue(id, out var c))
        {
            return (0, "No result found.", "");
        }
        var linhas = $"Row: 0 _id={id}, contact_id={id}, account_type=vnd.sec.contact.phone, account_name=";
        if (c.Telefone is not null && ContasExistentes.Contains(SoDigitos(c.Telefone)))
        {
            linhas += $"\nRow: 1 _id={EspelhoDe(id)}, contact_id={id}, "
                + "account_type=com.whatsapp, account_name=WhatsApp";
        }
        return (0, linhas, "");
    }

    /// <summary>As linhas de dados de um raw contact. Aqui o filtro da One UI não se aplica, então é por
    /// onde o espelho aparece.</summary>
    private (int, string, string) DadosDoRawContact(string raw)
    {
        if (!int.TryParse(raw, out var rid))
        {
            return (0, "No result found.", "");
        }
        var contato = rid - 10_000;
        if (!_agenda.TryGetValue(contato, out var c) || c.Telefone is null
            || !ContasExistentes.Contains(SoDigitos(c.Telefone)))
        {
            return (0, "No result found.", "");
        }
        // 🔴 DUAS ARMADILHAS DO APARELHO DE VERDADE, as duas modeladas de propósito.
        //
        // 1) `raw_contact_id=` vem ANTES do `_id=` no mesmo registro. Um padrão solto casa o "_id=" de
        //    dentro dele e devolve o id do raw contact no lugar do da linha de dados.
        //
        // 2) O REGISTRO QUEBRA NO MEIO. O `hash_id` traz um '\n' dentro do valor, e o `_id` cai na
        //    continuação — a parte que tem o `mimetype` NÃO tem o id. Medido em 2026-08-12 no Galaxy
        //    A14: `raw_contacts/907/data` devolve 10 linhas para 5 registros. Sem isto aqui, um leitor
        //    que parta por linha passa no teste e falha no aparelho, que foi exatamente o que aconteceu.
        var digitos = SoDigitos(c.Telefone);
        return (0,
            $"Row: 0 mimetype=vnd.android.cursor.item/name, data1=C{digitos}, raw_contact_id={rid}, "
            + $"hash_id=fW9tZQ==\n, _id={rid}, data15=NULL\n"
            + "Row: 1 mimetype=vnd.android.cursor.item/vnd.com.whatsapp.profile, "
            + $"data1={digitos}@s.whatsapp.net, raw_contact_id={rid}, "
            + $"hash_id=TEjjgsTHLn0=\n, _id={PerfilDe(contato)}, data15=NULL", "");
    }

    /// <summary>O `_id` da linha de perfil que o leitor deve devolver como URI da conversa.</summary>
    public static int PerfilDataId(int contato) => PerfilDe(contato);

    private (int, string, string) InserirDado(string cmd)
    {
        var rid = Regex.Match(cmd, @"raw_contact_id:i:(\d+)");
        if (!int.TryParse(rid.Groups[1].Value, out var id) || !_agenda.TryGetValue(id, out var c))
        {
            return (1, "", $"Unknown raw_contact_id {rid.Groups[1].Value}");
        }
        var valor = Desaspar(Regex.Match(cmd, @"data1:s:(.*)$").Groups[1].Value.Trim());
        if (cmd.Contains("item/name", StringComparison.Ordinal))
        {
            c.Nome = valor;
        }
        else
        {
            c.Telefone = valor;
        }
        return (0, "", "");
    }

    // ── WhatsApp: implementação ──────────────────────────────────────────────────────────────────

    private (int, string, string) DeepLink(string cmd)
    {
        // O `am start` responde 0 mesmo quando o app não troca de tela. Ver NavegacoesIgnoradas.
        if (NavegacoesIgnoradas > 0)
        {
            NavegacoesIgnoradas--;
            return (0, "Starting: Intent", "");
        }

        // NÍVEL 1 da cascata: abre pelo registro do contato na agenda, sem número pra resolver. Modelar
        // isto é o que permite testar a abertura que o console usa PRIMEIRO; enquanto o falso só
        // conhecia o `whatsapp://send`, esse nível caía sempre no nível seguinte e nunca era exercido.
        if (Regex.Match(cmd, @"content://com\.android\.contacts/data/(\d+)") is { Success: true } perfil)
        {
            var dataId = int.Parse(perfil.Groups[1].Value, CultureInfo.InvariantCulture);
            var dono = _agenda.Values.FirstOrDefault(c => PerfilDe(c.Id) == dataId);
            if (dono?.Telefone is null || !ContasExistentes.Contains(SoDigitos(dono.Telefone)))
            {
                return (1, "", "Error: Activity not started");
            }
            AbrirConversa(SoDigitos(dono.Telefone));
            return (0, "Starting: Intent", "");
        }

        var m = Regex.Match(cmd, @"whatsapp://send\?phone=(\d+)(?:&text=([^']*))?");
        if (!m.Success)
        {
            return (1, "", "Error: Activity not started");
        }
        var alvo = m.Groups[1].Value;
        if (!ContasExistentes.Contains(alvo))
        {
            // Diálogo por CIMA do que estiver na tela — é como o app se comporta, e foi o que fazia o
            // contato seguinte do lote "abrir atrás do diálogo".
            _dialogoSemConta = true;
            return (0, "Starting: Intent", "");
        }
        AbrirConversa(alvo);
        if (m.Groups[2].Success)
        {
            // Com `text=` o campo nasce com o texto do link, substituindo o rascunho.
            _rascunhos[alvo] = Uri.UnescapeDataString(m.Groups[2].Value.Replace("+", "%2B", StringComparison.Ordinal));
        }
        return (0, "Starting: Intent", "");
    }

    /// <summary>Põe a conversa na tela, com os avisos que ela ainda deve mostrar.</summary>
    /// <remarks>Os dois níveis de abertura (registro do contato e deep link por número) chegam AQUI: o
    /// aparelho não distingue por onde a conversa foi aberta, e o falso também não pode distinguir sem
    /// virar uma ficção conveniente.</remarks>
    private void AbrirConversa(string alvo)
    {
        _dialogoSemConta = false;
        _conversaAberta = alvo;
        // O aviso reaparece a cada abertura da conversa até ser dispensado uma vez.
        if (ComAvisoTemporaria.Contains(alvo))
        {
            AvisoNaTela = true;
        }
        if (ComAvisoSobreposto.Contains(alvo))
        {
            AvisoSobrepostoNaTela = true;
        }
    }

    private (int, string, string) Tap(string cmd)
    {
        var m = Regex.Match(cmd, @"input tap (\d+) (\d+)");
        var x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (AvisoNaTela || AvisoSobrepostoNaTela)
        {
            // Com o aviso na tela, o único toque que faz algo é o do botão dele. Qualquer outro cai no
            // vazio, que é como o Android se comporta com um modal por cima. Vale para os DOIS avisos:
            // o que esconde a conversa e o que só a cobre. Do lado de cá do vidro eles são iguais.
            if (x >= AvisoOkX1 && x <= AvisoOkX2 && y >= AvisoOkY1 && y <= AvisoOkY2)
            {
                DispensarAviso();
            }
            return (0, "", "");
        }
        var noBotao = x >= SendX1 && x <= SendX2 && y >= SendY1 && y <= SendY2;
        if (noBotao && _conversaAberta is { } conversa && _rascunhos.GetValueOrDefault(conversa, "").Length > 0)
        {
            Entregues.Add((conversa, _rascunhos[conversa]));
            _rascunhos[conversa] = "";
        }
        return (0, "", "");
    }

    /// <summary>Tira o aviso da frente e não deixa ele voltar nesta conversa, como o app faz depois de
    /// mostrá-lo uma vez.</summary>
    private void DispensarAviso()
    {
        AvisoNaTela = false;
        AvisoSobrepostoNaTela = false;
        ComAvisoTemporaria.Remove(_conversaAberta ?? "");
        ComAvisoSobreposto.Remove(_conversaAberta ?? "");
    }

    private (int, string, string) Back()
    {
        if (AvisoNaTela || AvisoSobrepostoNaTela)
        {
            DispensarAviso();
        }
        else if (_dialogoSemConta)
        {
            _dialogoSemConta = false;
        }
        else if (_conversaAberta is not null)
        {
            // Sair da conversa GUARDA o texto como rascunho dela; ele não fica na tela.
            _conversaAberta = null;
        }
        return (0, "", "");
    }

    private (int, string, string) InputText(string cmd)
    {
        var bruto = Desaspar(cmd["input text".Length..].Trim());
        return Digitar(bruto.Replace("%s", " ", StringComparison.Ordinal));
    }

    private (int, string, string) Digitar(string trecho)
    {
        if (_conversaAberta is null)
        {
            return (1, "", "java.lang.NullPointerException");
        }
        if (AvisoNaTela || AvisoSobrepostoNaTela)
        {
            // 🔴 Com um aviso por cima, o campo de mensagem NÃO tem o foco: o texto não entra em lugar
            // nenhum e o `input text` devolve 0 do mesmo jeito. Modelar isso é o que faz o teste
            // enxergar o defeito. Um falso que aceitasse a digitação atrás do aviso validaria uma
            // ficção, que é o mesmo erro já corrigido no PhoneLookup e no ContactData.
            return (0, "", "");
        }
        _rascunhos[_conversaAberta] = _rascunhos.GetValueOrDefault(_conversaAberta, "") + trecho;
        return (0, "", "");
    }

    /// <summary>A tela como o `uiautomator dump` a entrega: as JANELAS, uma atrás da outra.</summary>
    /// <remarks>Ver <see cref="DumpFalso"/> para por que a forma importa.</remarks>
    private string RenderizarTela()
    {
        var sb = new System.Text.StringBuilder("<hierarchy rotation=\"0\">");
        if (_dialogoSemConta)
        {
            sb.Append(DumpFalso.Janela("<node text=\"O número não está no WhatsApp\" "
                + "package=\"com.whatsapp\" bounds=\"[100,700][900,900]\"/>"));
        }
        if (AvisoNaTela)
        {
            // Modal por cima: enquanto ele está lá, o campo e o botão de enviar NÃO existem na árvore.
            // É exatamente isso que fazia o envio falhar com "a conversa não abriu".
            // Sem `class` de botão de propósito: aviso em tela cheia às vezes fecha por um TextView
            // clicável, e é esse caso que o TentarDesbloquearTelaAsync existe pra cobrir.
            sb.Append(DumpFalso.Janela(
                "<node text=\"Mensagens temporárias\" package=\"com.whatsapp\" clickable=\"false\" "
                + "bounds=\"[100,900][900,1100]\"/>"
                + string.Create(CultureInfo.InvariantCulture,
                    $"<node text=\"OK\" package=\"com.whatsapp\" clickable=\"true\" "
                    + $"bounds=\"[{AvisoOkX1},{AvisoOkY1}][{AvisoOkX2},{AvisoOkY2}]\"/>")));
            return sb.Append("</hierarchy>").ToString();
        }
        if (AvisoSobrepostoNaTela && AvisoAntesDaConversa)
        {
            sb.Append(JanelaDoAviso());
        }
        if (_conversaAberta is { } conversa)
        {
            sb.Append(JanelaDaConversa(conversa));
        }
        if (AvisoSobrepostoNaTela && !AvisoAntesDaConversa)
        {
            sb.Append(JanelaDoAviso());
        }
        return sb.Append("</hierarchy>").ToString();
    }

    private string JanelaDaConversa(string conversa)
    {
        var sb = new System.Text.StringBuilder();
        var rascunho = _rascunhos.GetValueOrDefault(conversa, "");
        sb.Append(DumpFalso.CampoDeMensagem(rascunho.Length == 0 ? "Mensagem" : Escapar(rascunho)));
        if (rascunho.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<node resource-id=\"com.whatsapp:id/send\" package=\"com.whatsapp\" "
                + $"bounds=\"[{SendX1},{SendY1}][{SendX2},{SendY2}]\"/>");
        }
        foreach (var _ in Entregues.Where(e => e.Conversa == conversa))
        {
            sb.Append("<node resource-id=\"com.whatsapp:id/status\" package=\"com.whatsapp\" "
                + "content-desc=\"Entregue\" bounds=\"[820,1700][860,1740]\"/>");
        }
        return DumpFalso.Janela(sb.ToString());
    }

    /// <summary>A janela do aviso que cobre a conversa sem apagá-la da árvore.</summary>
    /// <remarks>
    /// O botão sai com `class` de botão porque é o que o dump traz, e é o que separa um CONTROLE de uma
    /// bolha de mensagem clicável com o mesmo texto.
    /// <para>O nó que come o toque de fora vem junto: todo modal tem um (o `touch_outside` do bottom
    /// sheet, o decor do diálogo). Ele não é mais o que PROVA o aviso pro driver, que agora decide pela
    /// janela, mas continua aqui porque está no dump de verdade e sumir com ele seria simplificar o
    /// falso na direção do código, que é como se fabrica um teste que não pega nada.</para>
    /// </remarks>
    private static string JanelaDoAviso() =>
        DumpFalso.Janela(
            "<node resource-id=\"android:id/touch_outside\" package=\"com.whatsapp\" "
            + "bounds=\"[0,0][1080,2400]\"/>"
            + "<node text=\"Mensagens temporárias\" package=\"com.whatsapp\" clickable=\"false\" "
            + "bounds=\"[100,900][900,1100]\"/>"
            + string.Create(CultureInfo.InvariantCulture,
                $"<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
                + $"clickable=\"true\" bounds=\"[{AvisoOkX1},{AvisoOkY1}][{AvisoOkX2},{AvisoOkY2}]\"/>"));

    private static string Escapar(string s) =>
        System.Net.WebUtility.HtmlEncode(s).Replace("\n", "&#10;", StringComparison.Ordinal);

    private static string SoDigitos(string s) => new([.. s.Where(char.IsDigit)]);

    /// <summary>Desfaz as aspas simples que o driver põe pro shell do aparelho.</summary>
    private static string Desaspar(string s) =>
        s.Length >= 2 && s[0] == '\'' && s[^1] == '\''
            ? s[1..^1].Replace("'\\''", "'", StringComparison.Ordinal)
            : s;
}
