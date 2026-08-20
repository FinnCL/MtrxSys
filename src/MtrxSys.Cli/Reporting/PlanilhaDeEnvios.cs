using System.Globalization;
using ClosedXML.Excel;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Reporting;

namespace MtrxSys.Cli.Reporting;

/// <summary>Um contato que saiu da lista de envio, com o motivo.</summary>
/// <remarks>
/// 🔴 SAIU DA FILA, NÃO FOI APAGADO. O motivo e a data vão junto porque quem lê o relatório amanhã
/// precisa poder discordar: "estes 23 sumiram" sem o porquê é indistinguível de perda de dados, e a
/// primeira reação de quem vê isso é colar a lista de novo por inteiro, trazendo os mortos de volta.
/// </remarks>
public sealed record ContatoSuspenso(string Numero, string? Nome, FalhaCausa Causa, DateTimeOffset Quando);

/// <summary>Um número que só entregou na OUTRA forma do 9º dígito.</summary>
public sealed record CorrecaoDeNumero(string De, string Para);

/// <summary>O que só o lote sabe, e que o CSV não guarda.</summary>
/// <param name="Inicio">Tudo gravado daqui pra frente pertence a este lote.</param>
/// <param name="AgendaConfirmou">Quantos o espelho da agenda confirmou como usuários do WhatsApp.</param>
/// <param name="AgendaNaoConfirmou">Quantos ela não confirmou. Ver a nota em MostrarAgenda.</param>
/// <param name="Interrompido">
/// O lote não chegou ao fim (Ctrl+C, janela fechada, disjuntor). A planilha DIZ isso em vez de deixar
/// parecer que a lista acabou: com o lote cortado no meio, "3 enviadas" não significa que só havia 3.
/// </param>
public sealed record ContextoDoLote(
    DateTimeOffset Inicio,
    IReadOnlyList<CorrecaoDeNumero> Corrigidos,
    IReadOnlyList<ContatoSuspenso> Suspensos,
    int AgendaConfirmou,
    int AgendaNaoConfirmou,
    bool Interrompido);

/// <summary>Escreve o relatório do aparelho em .xlsx.</summary>
/// <remarks>
/// 🔴 SÓ DESENHA. Toda contagem vem do <see cref="RelatorioDeEnvios"/>, que mora no Core e tem teste;
/// aqui não se soma nada. A divisão existe porque o CLI não tem projeto de teste, e um "enviadas" que
/// engolisse o incerto não quebraria build nem tela: faria a planilha afirmar entregas que ninguém
/// confirmou, silenciosamente, todo dia.
///
/// <para>NUNCA é chamada sem try/catch do lado de fora. Um lote de horas não pode morrer porque o
/// disco encheu ou porque o arquivo anterior ficou aberto no Excel.</para>
/// </remarks>
public static class PlanilhaDeEnvios
{
    // Paleta do próprio Excel para bom/neutro/ruim: é a que o operador já reconhece de qualquer
    // planilha, então ninguém precisa aprender legenda nova.
    private static readonly XLColor FundoEntregue = XLColor.FromHtml("#C6EFCE");
    private static readonly XLColor TextoEntregue = XLColor.FromHtml("#006100");
    private static readonly XLColor FundoSaiu = XLColor.FromHtml("#E2EFDA");
    private static readonly XLColor TextoSaiu = XLColor.FromHtml("#375623");
    private static readonly XLColor FundoIncerto = XLColor.FromHtml("#FFEB9C");
    private static readonly XLColor TextoIncerto = XLColor.FromHtml("#9C6500");
    private static readonly XLColor FundoFalhou = XLColor.FromHtml("#FFC7CE");
    private static readonly XLColor TextoFalhou = XLColor.FromHtml("#9C0006");

    // 🔴 A RESTRIÇÃO NÃO É UMA FALHA A MAIS. Ela não fala do contato daquela linha, fala do chip, e
    // enquanto durar NENHUMA mensagem sai para ninguém. Pintada igual às outras, ela vira uma linha
    // vermelha entre dezenas de linhas vermelhas — e é a única que muda a decisão do dia.
    private static readonly XLColor FundoRestrito = XLColor.FromHtml("#C00000");
    private static readonly XLColor TextoRestrito = XLColor.FromHtml("#FFFFFF");

    private static readonly XLColor FundoTitulo = XLColor.FromHtml("#1F3864");
    private static readonly XLColor TextoTitulo = XLColor.FromHtml("#FFFFFF");

    /// <summary>Cabeçalho e largura juntos, e não em dois vetores paralelos.</summary>
    /// <remarks>
    /// Dois vetores obrigariam quem acrescentar uma coluna a lembrar dos dois, e o preço de esquecer não
    /// é um aviso: é um <c>IndexOutOfRange</c> em tempo de execução, dentro do bloco que só roda no fim
    /// de um lote de horas. Um vetor só torna o erro impossível de cometer.
    /// </remarks>
    private static readonly (string Nome, double Largura)[] Colunas =
    [
        ("quando", 17), ("número", 16), ("nome", 22), ("resultado", 12), ("entrega", 11),
        ("grupo", 13), ("causa", 26), ("o que fazer", 46), ("erro", 52), ("variante", 9),
        ("abertura", 10), ("contradito", 11), ("texto", 60),
    ];

    /// <summary>Teto de linhas da aba Histórico.</summary>
    /// <remarks>
    /// 🔴 O CSV DE ENVIOS CRESCE PARA SEMPRE, e nada o limpa. Sem teto, a aba Histórico cresce junto e o
    /// custo cai no PIOR momento possível: no fim de um lote de horas, com tudo já enviado. Medido nesta
    /// máquina: 40 mil linhas levam ~5s e 180 MB, o que é aceitável; a questão é que o número não para
    /// aí, e o modo de falha lá na frente é o console morrer de memória depois de ter feito o trabalho.
    /// <para>50 mil linhas são mais de um ano de lotes diários de 100 mensagens. E o corte é ANUNCIADO
    /// na própria aba: planilha que corta em silêncio se lê como "é tudo que existe", que é pior do que
    /// não ter a aba. Os números do Resumo continuam vindo do histórico INTEIRO.</para>
    /// </remarks>
    private const int TetoDoHistorico = 50_000;

    /// <summary>Gera o arquivo e devolve o caminho.</summary>
    /// <param name="serial">Serial adb do aparelho. Só rotula: o recorte já vem em
    /// <paramref name="historico"/>.</param>
    /// <param name="historico">Tudo que o log daquele aparelho guarda, do mais antigo ao mais novo.</param>
    /// <param name="lote">null quando o relatório é pedido fora de um lote (comando `relatorio`).</param>
    /// <param name="suspensos">A quarentena do console no momento da geração: quem saiu da fila por o
    /// app afirmar que o número não tem conta. Vazia é estado normal.</param>
    public static string Gerar(
        string serial,
        IReadOnlyList<LinhaDeEnvio> historico,
        ContextoDoLote? lote,
        string pasta,
        DateTimeOffset agora,
        IReadOnlyList<(string Numero, string? Nome)>? suspensos = null)
    {
        ArgumentNullException.ThrowIfNull(historico);

        // O recorte do lote sai do carimbo de tempo, e não de uma marca gravada na linha, porque o CSV
        // é append-only e não tem noção de "lote": ele é um rio de tentativas. `>=` e não `>` porque o
        // início é capturado ANTES da primeira mensagem.
        var doLote = lote is null
            ? []
            : historico.Where(l => l.Quando >= lote.Inicio).ToList();

        using var wb = new XLWorkbook();

        if (lote is not null)
        {
            Tabela(wb.Worksheets.Add("Lote"), doLote);
        }
        Resumo(wb.Worksheets.Add("Resumo"), serial, historico, doLote, lote, agora);

        // As MAIS RECENTES, e não as primeiras: quem abre a aba quer ver o que vem acontecendo com o
        // chip agora. O Resumo continua contando o histórico inteiro, então o corte só afeta o detalhe.
        var recentes = historico.Count <= TetoDoHistorico
            ? historico
            : historico.Skip(historico.Count - TetoDoHistorico).ToList();
        Tabela(wb.Worksheets.Add("Histórico"), recentes, historico.Count - recentes.Count);

        // A quarentena vira ABA em vez de tela separada no console: ela é o outro lado do histórico
        // (quem NÃO foi tentado, e por quê), e ler as duas metades em lugares diferentes obrigava a
        // juntar de cabeça. Sempre presente, inclusive vazia: aba que aparece e some faz quem procura
        // concluir que o dado se perdeu.
        Suspensos(wb.Worksheets.Add("Suspensos"), suspensos ?? [], historico);

        // Com SEGUNDOS, e não só até o minuto: dois lotes curtos terminando no mesmo minuto cairiam no
        // mesmo nome, e o `SaveAs` sobrescreve calado. Perder o relatório do lote anterior sem avisar é
        // exatamente o tipo de coisa que só se descobre no dia em que ele fazia falta.
        var arquivo = Path.Combine(
            pasta,
            $"relatorio-{serial}-{agora.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.xlsx");
        wb.SaveAs(arquivo);
        return arquivo;
    }

    /// <summary>A quarentena: quem saiu da fila sem ter recebido nada, com o que o log sabe sobre cada
    /// um.</summary>
    /// <remarks>
    /// 🔴 CRUZA COM O HISTÓRICO em vez de listar número e nome. "Este número foi suspenso" sozinho não
    /// deixa ninguém decidir se devolve ou descarta; "suspenso, 4 tentativas, a última em 12/08" deixa.
    /// O cruzamento é por número exato: o log guarda a forma que foi TENTADA, e é ela que interessa.
    /// </remarks>
    private static void Suspensos(
        IXLWorksheet ws,
        IReadOnlyList<(string Numero, string? Nome)> suspensos,
        IReadOnlyList<LinhaDeEnvio> historico)
    {
        (string Nome, double Largura)[] colunas =
        [
            ("número", 16), ("nome", 22), ("última tentativa", 17), ("tentativas no log", 16),
            ("por que saiu da fila", 70),
        ];
        for (var c = 0; c < colunas.Length; c++)
        {
            ws.Cell(1, c + 1).Value = colunas[c].Nome;
            ws.Column(c + 1).Width = colunas[c].Largura;
        }
        Pintar(ws.Range(1, 1, 1, colunas.Length), FundoTitulo, TextoTitulo, negrito: true);
        ws.Column(1).Style.NumberFormat.Format = "@"; // 13 dígitos viram notação científica sem isto
        ws.Column(3).Style.DateFormat.Format = "dd/MM/yyyy HH:mm:ss";

        // 🔴 UM ÍNDICE, e não uma varredura por suspenso. O `historico` chega com até 50 mil linhas (é o
        // teto da aba Histórico) e a quarentena pode ter dezenas de números: varrer a lista inteira por
        // contato é 50 mil comparações de string VEZES o número de suspensos, e isso roda no fim de um
        // lote de horas, junto com a geração do arquivo. O agrupamento é uma passada só, e a busca por
        // número passa a ser direta.
        var porNumero = historico.ToLookup(l => l.Numero, StringComparer.Ordinal);

        var r = 1;
        foreach (var (numero, nome) in suspensos)
        {
            var tentativas = porNumero[numero].ToList();
            r++;
            ws.Cell(r, 1).Value = numero;
            ws.Cell(r, 2).Value = nome ?? "";
            if (tentativas.Count > 0)
            {
                ws.Cell(r, 3).Value = tentativas[^1].Quando.DateTime;
            }
            ws.Cell(r, 4).Value = tentativas.Count;
            ws.Cell(r, 5).Value =
                "o WhatsApp afirmou que o número não tem conta, nas duas formas do 9º dígito";
            Pintar(ws.Range(r, 1, r, colunas.Length), FundoIncerto, TextoIncerto);
        }

        ws.SheetView.FreezeRows(1);
        if (r > 1)
        {
            ws.Range(1, 1, r, colunas.Length).SetAutoFilter();
        }
        else
        {
            // Aba vazia diz por que está vazia. Sem esta linha, ela se lê como "o relatório não trouxe
            // esse dado", que é a leitura errada de um estado que é bom.
            ws.Cell(2, 1).Value = "nenhum contato em quarentena neste aparelho.";
        }
    }

    /// <summary>Uma linha por tentativa, pintada pelo desfecho.</summary>
    /// <param name="omitidas">Linhas mais antigas que ficaram de fora. Anunciadas na primeira célula.</param>
    private static void Tabela(IXLWorksheet ws, IReadOnlyList<LinhaDeEnvio> linhas, int omitidas = 0)
    {
        for (var c = 0; c < Colunas.Length; c++)
        {
            ws.Cell(1, c + 1).Value = Colunas[c].Nome;
            ws.Column(c + 1).Width = Colunas[c].Largura;
        }
        Pintar(ws.Range(1, 1, 1, Colunas.Length), FundoTitulo, TextoTitulo, negrito: true);

        // Texto, não número: um celular tem 13 dígitos e o Excel o transformaria em notação científica,
        // fazendo a planilha exibir um número que não existe.
        ws.Column(2).Style.NumberFormat.Format = "@";
        // Na COLUNA e não célula a célula: com dezenas de milhares de linhas, atribuir estilo por célula
        // multiplica o trabalho por nada, já que o formato é o mesmo em todas.
        //
        // 🔴 COM SEGUNDOS. O ritmo normal entre envios é de 150 a 360s, então minuto bastaria — mas
        // depois de uma FALHA a espera cai para 8 a 21s (ver FalhaEsperaMin no console), e aí várias
        // linhas caem no mesmo minuto. Sem os segundos, a sequência de uma rajada de falhas fica
        // impossível de reconstruir, que é justamente o momento em que alguém vai querer reconstruí-la.
        // O CSV sempre guardou a precisão inteira; era só a exibição que a jogava fora.
        ws.Column(1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm:ss";

        var r = 1;
        foreach (var l in linhas)
        {
            r++;
            ws.Cell(r, 1).Value = l.Quando.DateTime;
            ws.Cell(r, 2).Value = l.Numero;
            ws.Cell(r, 3).Value = l.Nome ?? "";
            ws.Cell(r, 4).Value = Rotulo(l.Resultado);
            ws.Cell(r, 5).Value = l.Entrega ?? "";
            var saiu = l.Resultado is ResultadoDoEnvio.Enviado;
            ws.Cell(r, 6).Value = saiu
                ? ""
                : DiagnosticoDeFalha.RotuloDoGrupo(DiagnosticoDeFalha.Grupo(l.Causa));
            ws.Cell(r, 7).Value = CausaDe(l.Causa, saiu);
            ws.Cell(r, 8).Value = AcaoDe(l.Causa, saiu);
            ws.Cell(r, 9).Value = l.Erro ?? "";
            ws.Cell(r, 10).Value = l.Variante;
            ws.Cell(r, 11).Value = l.Abertura;
            ws.Cell(r, 12).Value = l.Contradito ? "sim" : "";
            ws.Cell(r, 13).Value = l.Texto;

            var (fundo, texto) = Cores(l);
            Pintar(ws.Range(r, 1, r, Colunas.Length), fundo, texto);
        }

        // Congelar e filtrar só fazem sentido com corpo: numa tabela vazia o autofiltro do ClosedXML
        // mira um intervalo que não existe.
        ws.SheetView.FreezeRows(1);
        if (r > 1)
        {
            ws.Range(1, 1, r, Colunas.Length).SetAutoFilter();
        }

        // 🔴 O CORTE VAI NO CABEÇALHO, onde é impossível não ver. Escondê-lo no rodapé de 50 mil linhas
        // seria o mesmo que não dizer, e planilha que corta em silêncio afirma ser o total.
        if (omitidas > 0)
        {
            ws.Cell(1, 1).Value = $"quando  (⚠ {omitidas} linha(s) mais antigas ficaram de fora)";
            ws.Cell(1, 1).GetComment().AddText(
                $"A aba mostra as {linhas.Count} tentativas mais recentes. O log completo continua no CSV, "
                + "e os números do Resumo contam o histórico inteiro.");
        }
    }

    /// <summary>
    /// 🔴 CINCO CORES, E NÃO DUAS. Verde e vermelho só respondem "saiu?", e há dois casos em que essa
    /// pergunta não tem resposta binária.
    /// <para>O <see cref="ResultadoDoEnvio.Incerto"/> é amarelo porque o toque em enviar JÁ aconteceu e
    /// ninguém leu a tela: verde faria riscar alguém que talvez não recebeu, vermelho faria mandar de
    /// novo pra quem talvez já recebeu. É a mesma doutrina do IsOnWhatsAppAsync, que devolve true ou
    /// null e nunca false: quando errar é caro, "não sei" precisa ter cor própria.</para>
    /// <para>A conta restringida é vinho porque não fala do contato: ela manda parar o lote inteiro.</para>
    /// </summary>
    private static (XLColor Fundo, XLColor Texto) Cores(LinhaDeEnvio l) => l switch
    {
        { Causa: FalhaCausa.ContaRestringida } => (FundoRestrito, TextoRestrito),
        { Resultado: ResultadoDoEnvio.Incerto } => (FundoIncerto, TextoIncerto),
        { Resultado: ResultadoDoEnvio.NaoSaiu } => (FundoFalhou, TextoFalhou),
        { EntregaConfirmada: true } => (FundoEntregue, TextoEntregue),
        _ => (FundoSaiu, TextoSaiu),
    };

    private static string Rotulo(ResultadoDoEnvio r) => r switch
    {
        ResultadoDoEnvio.Enviado => "enviado",
        ResultadoDoEnvio.Incerto => "não confirmado",
        _ => "não saiu",
    };

    // 🔴 `Nenhuma` NUMA LINHA QUE NÃO SAIU É OUTRA COISA. No sucesso ela quer dizer "não há causa", e a
    // célula fica vazia com razão. Numa falha ela só pode ter vindo de um log gravado ANTES de a coluna
    // de causa existir — e aí a célula vazia mente duas vezes: parece que o sistema não soube explicar
    // aquela falha, e some com a única informação útil, que é a idade do registro.
    private const string NaoClassificado = "não classificado (log anterior à coluna de causa)";

    private static string CausaDe(FalhaCausa c, bool saiu) =>
        saiu ? "" : c is FalhaCausa.Nenhuma ? NaoClassificado : DiagnosticoDeFalha.Rotulo(c);

    private static string AcaoDe(FalhaCausa c, bool saiu) =>
        saiu
            ? ""
            : c is FalhaCausa.Nenhuma
                ? "veja a coluna de erro: é a única pista que este registro guardou."
                : DiagnosticoDeFalha.OQueFazer(c);

    private static void Resumo(
        IXLWorksheet ws,
        string serial,
        IReadOnlyList<LinhaDeEnvio> historico,
        IReadOnlyList<LinhaDeEnvio> doLote,
        ContextoDoLote? lote,
        DateTimeOffset agora)
    {
        // 46 e não 34: o rótulo mais longo ("contradições (o app negou, a agenda discordou)") tem 45
        // caracteres, e com a coluna 2 preenchida o Excel CORTA o que passa da largura em vez de
        // transbordar. A régua é o maior rótulo, não um número redondo.
        ws.Column(1).Width = 46;
        ws.Column(2).Width = 16;
        ws.Column(3).Width = 14;
        ws.Column(4).Width = 60;

        // 🔴 UMA CONTAGEM SÓ DO HISTÓRICO, reaproveitada. Antes o recorte e o histórico eram contados
        // separadamente, e sem lote os dois eram a MESMA lista: o arquivo inteiro era percorrido duas
        // vezes para produzir dois resultados idênticos.
        var todos = RelatorioDeEnvios.Resumir(historico);
        var resumo = lote is null ? todos : RelatorioDeEnvios.Resumir(doLote);

        var p = new Cursor(ws);
        p.Titulo("RELATÓRIO DE ENVIOS");
        p.Par("aparelho", serial);
        p.Par("gerado em", agora.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        p.Par("recorte", lote is null
            ? "histórico completo (relatório pedido fora de um lote)"
            : $"lote iniciado {lote.Inicio.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}");
        if (lote is { Interrompido: true })
        {
            // 🔴 DITO EM VOZ ALTA. Num lote cortado no meio, "3 enviadas de 3 tentativas" parece um lote
            // pequeno e perfeito, quando na verdade sobraram 80 contatos que nunca foram tentados. É a
            // mesma diferença entre "acabou" e "parou", e a planilha não tem outro jeito de mostrá-la.
            p.Par("⚠ lote INTERROMPIDO", "não chegou ao fim",
                "Ctrl+C ou erro inesperado. os contatos que faltam continuam na lista, "
                + "e o resumo abaixo só cobre o que deu tempo de acontecer.");
        }
        p.Pular();

        p.Titulo(lote is null ? "TODO O HISTÓRICO" : "ESTE LOTE");
        p.Par("tentativas (conversas abertas)", resumo.Tentativas);
        p.Par("enviadas", resumo.Enviadas);
        p.Par("entregas já confirmadas na tela", resumo.EntregasConfirmadas);
        p.Par("não confirmadas (pode ter saído)", resumo.Incertas);
        p.Par("número sem conta", resumo.SemConta);
        p.Par("outras falhas", resumo.OutrasFalhas);
        p.Par("cota gasta (enviadas + não confirmadas)", resumo.CotaGasta);
        p.Pular();

        if (resumo.PorCausa.Count > 0)
        {
            p.Titulo("POR QUE NÃO SAIU");
            p.Cabecalho("causa", "quantidade", "fatia", "o que fazer");
            foreach (var c in resumo.PorCausa)
            {
                p.Linha(CausaDe(c.Causa, saiu: false), c.Quantidade, c.Fracao, AcaoDe(c.Causa, saiu: false));
                ws.Cell(p.Ultima, 3).Style.NumberFormat.Format = "0%";
            }
            p.Pular();
        }

        p.Titulo("SINAIS DO CHIP");
        // 🔴 A CONTRADIÇÃO É O SINAL PRECOCE. O app nega o número e a agenda do PRÓPRIO aparelho diz
        // que ele É usuário: duas fontes independentes discordando sobre o mesmo fato. Uma é ruído;
        // em série, é a conta que parou de resolver, e não a lista que ficou ruim.
        p.Par("contradições (o app negou, a agenda discordou)", resumo.Contradicoes,
            resumo.Contradicoes == 0
                ? "nenhuma. o app e a agenda concordam."
                : "em série, isto é sinal de restrição do chip, não de lista ruim.");
        p.Par("entrega confirmada", $"{resumo.EntregasConfirmadas} de {resumo.Enviadas}",
            "é PISO, não taxa: a leitura acontece segundos após o envio, e o resto pode ter chegado depois.");
        if (lote is not null)
        {
            var olhados = lote.AgendaConfirmou + lote.AgendaNaoConfirmou;
            p.Par("agenda confirmou WhatsApp", $"{lote.AgendaConfirmou} de {olhados}",
                "compare com quem de fato entregou: é assim que se descobre se vale ligar o `segurar`.");
        }
        p.Pular();

        if (lote is { Corrigidos.Count: > 0 })
        {
            p.Titulo("CORRIGIR NA ORIGEM");
            p.Cabecalho("estava na lista como", "só entregou como", "", "");
            foreach (var c in lote.Corrigidos)
            {
                p.Linha(c.De, c.Para, "", "");
            }
            p.Pular();
        }

        if (lote is { Suspensos.Count: > 0 })
        {
            p.Titulo("SAÍRAM DA LISTA");
            p.Cabecalho("número", "nome", "quando", "motivo");
            foreach (var s in lote.Suspensos)
            {
                p.Linha(
                    s.Numero, s.Nome ?? "",
                    s.Quando.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture),
                    DiagnosticoDeFalha.OQueFazer(s.Causa));
                Pintar(ws.Range(p.Ultima, 1, p.Ultima, LarguraDoResumo), FundoFalhou, TextoFalhou);
            }
            p.Pular();
        }

        if (todos.PorDia.Count > 0)
        {
            p.Titulo("HISTÓRICO POR DIA");
            p.Cabecalho("dia", "saíram", "não saíram", "entregas confirmadas");
            foreach (var d in todos.PorDia)
            {
                p.Linha(d.Dia, d.Enviadas, d.NaoSairam, d.EntregasConfirmadas);
            }
        }
    }

    /// <summary>Quantas colunas a aba Resumo usa. As quatro seções dela têm a mesma largura.</summary>
    private const int LarguraDoResumo = 4;

    /// <summary>A linha corrente da aba Resumo.</summary>
    /// <remarks>
    /// 🔴 UM OBJETO NO LUGAR DE UM <c>ref int</c> QUE ANDAVA SOZINHO. Metade dos ajudantes recebia a
    /// linha por <c>ref</c> e avançava por dentro, a outra metade recebia por valor e obrigava o chamador
    /// a lembrar do <c>r++</c>. Duas convenções no mesmo método é o tipo de coisa que ninguém percebe até
    /// alguém esquecer o incremento e uma seção sobrescrever a de cima.
    /// </remarks>
    private sealed class Cursor(IXLWorksheet ws)
    {
        private int _r = 1;

        /// <summary>A última linha escrita. Serve pra pintar ou formatar o que acabou de sair.</summary>
        public int Ultima => _r - 1;

        public void Pular() => _r++;

        public void Titulo(string texto)
        {
            ws.Cell(_r, 1).Value = texto;
            Pintar(ws.Range(_r, 1, _r, LarguraDoResumo), FundoTitulo, TextoTitulo, negrito: true);
            _r++;
        }

        public void Par(string rotulo, XLCellValue valor, string? nota = null)
        {
            ws.Cell(_r, 1).Value = rotulo;
            ws.Cell(_r, 2).Value = valor;
            ws.Cell(_r, 2).Style.Font.Bold = true;
            if (nota is not null)
            {
                ws.Cell(_r, LarguraDoResumo).Value = nota;
            }
            _r++;
        }

        public void Cabecalho(string a, string b, string c, string d)
        {
            Linha(a, b, c, d);
            ws.Range(Ultima, 1, Ultima, LarguraDoResumo).Style.Font.Bold = true;
        }

        public void Linha(XLCellValue a, XLCellValue b, XLCellValue c, XLCellValue d)
        {
            ws.Cell(_r, 1).Value = a;
            ws.Cell(_r, 2).Value = b;
            ws.Cell(_r, 3).Value = c;
            ws.Cell(_r, 4).Value = d;
            _r++;
        }
    }

    private static void Pintar(IXLRange faixa, XLColor fundo, XLColor texto, bool negrito = false)
    {
        faixa.Style.Fill.BackgroundColor = fundo;
        faixa.Style.Font.FontColor = texto;
        if (negrito)
        {
            faixa.Style.Font.Bold = true;
        }
    }

}
