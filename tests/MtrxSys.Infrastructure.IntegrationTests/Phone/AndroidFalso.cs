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
            && cmd.Contains("raw_contacts", StringComparison.Ordinal))
        {
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
            if (DumpsFalhando > 0 && (!FalharDumpApenasAposEnvio || Entregues.Count > 0))
            {
                DumpsFalhando--;
                return (1, "", "ERROR: could not get idle state.");
            }
            _dumpGravado = RenderizarTela();
            return (0, "UI hierchary dumped to: /sdcard/mtrx_ui.xml", "");
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
        // O espelho que o WhatsApp publica pra quem é usuário da plataforma.
        var espelho = c.Telefone is not null && ContasExistentes.Contains(SoDigitos(c.Telefone))
            ? "Row: 1 mimetype=vnd.com.whatsapp.profile"
            : "";
        return (0, $"Row: 0 mimetype=vnd.android.cursor.item/phone_v2 data1={c.Telefone}\n{espelho}", "");
    }

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
        _dialogoSemConta = false;
        _conversaAberta = alvo;
        // O aviso reaparece a cada abertura da conversa até ser dispensado uma vez.
        if (ComAvisoTemporaria.Contains(alvo))
        {
            AvisoNaTela = true;
        }
        if (m.Groups[2].Success)
        {
            // Com `text=` o campo nasce com o texto do link, substituindo o rascunho.
            _rascunhos[alvo] = Uri.UnescapeDataString(m.Groups[2].Value.Replace("+", "%2B", StringComparison.Ordinal));
        }
        return (0, "Starting: Intent", "");
    }

    private (int, string, string) Tap(string cmd)
    {
        var m = Regex.Match(cmd, @"input tap (\d+) (\d+)");
        var x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (AvisoNaTela)
        {
            // Com o aviso na tela, o único toque que faz algo é o do botão dele. Qualquer outro cai no
            // vazio, que é como o Android se comporta com um modal por cima.
            if (x >= AvisoOkX1 && x <= AvisoOkX2 && y >= AvisoOkY1 && y <= AvisoOkY2)
            {
                AvisoNaTela = false;
                ComAvisoTemporaria.Remove(_conversaAberta ?? "");
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

    private (int, string, string) Back()
    {
        if (AvisoNaTela)
        {
            AvisoNaTela = false;
            ComAvisoTemporaria.Remove(_conversaAberta ?? "");
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
        _rascunhos[_conversaAberta] = _rascunhos.GetValueOrDefault(_conversaAberta, "") + trecho;
        return (0, "", "");
    }

    private string RenderizarTela()
    {
        var sb = new System.Text.StringBuilder("<hierarchy rotation=\"0\">");
        if (_dialogoSemConta)
        {
            sb.Append("<node text=\"O número não está no WhatsApp\" bounds=\"[100,700][900,900]\"/>");
        }
        if (AvisoNaTela)
        {
            // Modal por cima: enquanto ele está lá, o campo e o botão de enviar NÃO existem na árvore.
            // É exatamente isso que fazia o envio falhar com "a conversa não abriu".
            sb.Append("<node text=\"Mensagens temporárias\" clickable=\"false\" bounds=\"[100,900][900,1100]\"/>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<node text=\"OK\" clickable=\"true\" bounds=\"[{AvisoOkX1},{AvisoOkY1}][{AvisoOkX2},{AvisoOkY2}]\"/>");
            return sb.Append("</hierarchy>").ToString();
        }
        if (_conversaAberta is { } conversa)
        {
            var rascunho = _rascunhos.GetValueOrDefault(conversa, "");
            // ⚠️ Campo vazio devolve a DICA, não string vazia. É o comportamento medido nos dois
            // aparelhos, e a razão de o sinal confiável ser o botão de enviar.
            var mostrado = rascunho.Length == 0 ? "Mensagem" : Escapar(rascunho);
            sb.Append(CultureInfo.InvariantCulture,
                $"<node text=\"{mostrado}\" resource-id=\"com.whatsapp:id/entry\" bounds=\"[50,1800][880,1900]\"/>");
            if (rascunho.Length > 0)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"<node resource-id=\"com.whatsapp:id/send\" bounds=\"[{SendX1},{SendY1}][{SendX2},{SendY2}]\"/>");
            }
            foreach (var _ in Entregues.Where(e => e.Conversa == conversa))
            {
                sb.Append("<node resource-id=\"com.whatsapp:id/status\" content-desc=\"Entregue\" bounds=\"[820,1700][860,1740]\"/>");
            }
        }
        return sb.Append("</hierarchy>").ToString();
    }

    private static string Escapar(string s) =>
        System.Net.WebUtility.HtmlEncode(s).Replace("\n", "&#10;", StringComparison.Ordinal);

    private static string SoDigitos(string s) => new([.. s.Where(char.IsDigit)]);

    /// <summary>Desfaz as aspas simples que o driver põe pro shell do aparelho.</summary>
    private static string Desaspar(string s) =>
        s.Length >= 2 && s[0] == '\'' && s[^1] == '\''
            ? s[1..^1].Replace("'\\''", "'", StringComparison.Ordinal)
            : s;
}
