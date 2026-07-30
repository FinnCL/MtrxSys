using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

/// <summary>Console INTERATIVO do aparelho: cola a lista de contatos, cola as variantes de texto,
/// confere a prévia e dispara o lote — tudo numa sessão só, sem repetir variável de ambiente nem
/// caminho de executável a cada envio.</summary>
/// <remarks>
/// <para>É o mesmo motor do <see cref="PhoneSendCommand"/> (o <see cref="IPhoneOrchestrator"/> do DI,
/// que respeita o <c>Phone__Engine</c>): a diferença é o envoltório. O comando de uma linha serve pra
/// provar mecânica; este serve pra operar uma lista.</para>
/// <para>⚠️ Continua sendo BANCADA, não o <c>DispatchEngine</c>: aqui não há fila, curva de aquecimento,
/// opt-out, dedup entre execuções nem auditoria no banco. O que existe é teto por lote (visível e
/// ajustável), pré-voo antes da primeira mensagem e log em CSV de tudo que saiu.</para>
/// <para>UM console por aparelho. As variáveis de ambiente são por processo e o
/// <c>DirectAdbRunner</c> sempre passa <c>-s &lt;serial&gt;</c>, então duas janelas com seriais
/// diferentes operam dois celulares em paralelo sem se enxergar. Duas janelas no MESMO serial, não:
/// o <c>uiautomator dump</c> grava num arquivo fixo dentro do aparelho e uma leria a tela da outra.</para>
/// </remarks>
internal sealed class PhoneConsoleCommand(
    IPhoneOrchestrator phone,
    IOptions<PhoneOptions> options,
    CancellationTokenProvider cancellation) : AsyncCommand<PhoneConsoleCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--teto <N>")]
        [Description("Máximo de mensagens por lote (default 30). Ajustável dentro do console.")]
        public int Teto { get; init; } = 30;
    }

    /// <param name="Numero">Só dígitos, já validado em 12 ou 13 (55 + DDD + número).</param>
    /// <param name="Nome">Opcional; alimenta o token <c>{nome}</c> nos textos.</param>
    private sealed record Contato(string Numero, string? Nome);

    /// <summary>O que sobrevive ao fechar a janela. Uma lista de 80 contatos colada à mão é cara de
    /// refazer, e fechar console por engano é rotina.</summary>
    private sealed record Estado
    {
        public List<string> Contatos { get; init; } = [];
        public List<string> Textos { get; init; } = [];
        public int MinDelay { get; init; } = 150;
        public int MaxDelay { get; init; } = 360;
        public int Teto { get; init; }
        public bool Agenda { get; init; } = true;
        public bool DigitacaoHumana { get; init; } = true;
    }

    private const string TokenNome = "{nome}";

    private readonly List<Contato> _contatos = [];
    private readonly List<string> _textos = [];
    private int _min = 150;
    private int _max = 360;
    private int _teto = 30;

    /// <summary>Grava o contato na agenda do aparelho antes de enviar. LIGADO por padrão por decisão
    /// do operador (2026-07-30). Sessão salva com o valor desligado continua desligada: escolha
    /// explícita ganha do default.</summary>
    private bool _agenda = true;

    /// <summary>Digitar caractere a caractere (ligada) ou entregar o texto pronto pelo deep link
    /// (desligada). Espelha <see cref="PhoneOptions.HumanTyping"/>, que o driver lê a cada envio.</summary>
    /// <remarks>
    /// Vira botão do console porque a escolha é de OPERAÇÃO, não de instalação: ligada, só sai ASCII
    /// (o `input text` não digita acento nem emoji); desligada, sai qualquer caractere, mas o campo
    /// nasce preenchido e o destinatário nunca vê "digitando…".
    /// </remarks>
    private bool _digitacaoHumana = true;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var ct = cancellation.Token;
        _teto = Math.Max(1, s.Teto);

        var opts = options.Value;
        var serial = string.IsNullOrWhiteSpace(opts.AdbSerial) ? "(sem serial)" : opts.AdbSerial;
        _digitacaoHumana = opts.HumanTyping;

        AnsiConsole.Write(new Rule($"[bold]mtrx phone console[/]  ·  engine [bold]{opts.Engine.EscapeMarkup()}[/]  ·  aparelho [bold]{serial.EscapeMarkup()}[/]").LeftJustified());

        // A trava vem ANTES de qualquer coisa: dois consoles no mesmo serial leriam a tela um do
        // outro (o `uiautomator dump` grava num arquivo FIXO dentro do aparelho). O lock do
        // orquestrador é por processo e não enxerga a outra janela.
        using var trava = Travar(serial);
        if (trava is null)
        {
            // Dizer QUEM segura não é detalhe: sem isso, um console que morreu sem fechar a janela
            // (ou um processo pendurado) vira "não abre e não sei por quê".
            AnsiConsole.MarkupLine(
                $"[red]o aparelho {serial.EscapeMarkup()} já está aberto em outro console.[/] "
                + $"({QuemSegura(serial)})");
            AnsiConsole.MarkupLine(
                "duas janelas no mesmo celular disputam a leitura de tela e uma lê a conversa da outra.");
            AnsiConsole.MarkupLine(
                "feche a outra janela. se não houver nenhuma, encerre o [bold]mtrx.exe[/] pendurado "
                + "pelo Gerenciador de Tarefas.");
            return 1;
        }

        if (!await AparelhoPronto(ct))
        {
            return 1;
        }

        Carregar(serial);

        // O que a sessão gravou manda no que o driver faz: PhoneOptions é singleton e o driver relê
        // HumanTyping a cada envio, então escrever aqui basta.
        opts.HumanTyping = _digitacaoHumana;

        // A ajuda abre SOZINHA. Um console cujo primeiro passo é adivinhar o nome de um comando é um
        // console que só serve pra quem escreveu. O custo é uma tela a mais; o ganho é não precisar
        // de ninguém do lado explicando.
        Ajuda();

        var sair = false;
        while (!sair)
        {
            Menu(serial);
            AnsiConsole.Markup("\n[bold blue]mtrx>[/] ");
            var linha = Console.ReadLine();
            if (linha is null)
            {
                break; // stdin fechado (pipe acabou): sai limpo em vez de girar em vazio
            }

            var partes = linha.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0)
            {
                continue;
            }

            try
            {
                switch (partes[0].ToLowerInvariant())
                {
                    // ── pelo menu (número): pergunta o valor, não exige a sintaxe ────────────────
                    case "1":
                        IntervaloInterativo();
                        Salvar(serial);
                        break;
                    case "2":
                        TetoInterativo();
                        Salvar(serial);
                        break;
                    case "4":
                        switch (PerguntarModo(_contatos.Count, "contato(s)"))
                        {
                            case ModoLista.Editar: EditarContato(); break;
                            case ModoLista.Cancelar: AnsiConsole.MarkupLine("[grey]cancelado.[/]"); break;
                            case var m: LerContatos(somar: m == ModoLista.Acrescentar); break;
                        }
                        Salvar(serial);
                        break;
                    case "5":
                        switch (PerguntarModo(_textos.Count, "template(s)"))
                        {
                            case ModoLista.Editar: EditarTexto(); break;
                            case ModoLista.Cancelar: AnsiConsole.MarkupLine("[grey]cancelado.[/]"); break;
                            case var m: LerTextos(somar: m == ModoLista.Acrescentar); break;
                        }
                        Salvar(serial);
                        break;
                    case "6":
                        Ver();
                        break;
                    case "7":
                        Previa();
                        break;
                    case "8":
                        await EnviarAsync(serial, ct);
                        break;
                    case "0":
                        sair = true;
                        break;

                    // ── por extenso, e os que valem nas duas formas ──────────────────────────────
                    case "9" or "ajuda" or "?" or "help":
                        Ajuda();
                        break;
                    case "comandos":
                        Comandos();
                        break;
                    case "status":
                        await AparelhoPronto(ct);
                        break;
                    case "contatos":
                        LerContatos(somar: partes is [_, "+", ..]);
                        Salvar(serial);
                        break;
                    case "textos":
                        LerTextos(somar: partes is [_, "+", ..]);
                        Salvar(serial);
                        break;
                    case "ver":
                        Ver();
                        break;
                    case "previa":
                        Previa();
                        break;
                    case "enviar":
                        await EnviarAsync(serial, ct);
                        break;
                    case "intervalo":
                        Intervalo(partes);
                        Salvar(serial);
                        break;
                    case "teto":
                        Teto(partes);
                        Salvar(serial);
                        break;
                    case "3" or "agenda":
                        _agenda = !_agenda;
                        AnsiConsole.MarkupLine($"gravar na agenda antes de enviar: [bold]{(_agenda ? "ligado" : "desligado")}[/]");
                        Salvar(serial);
                        break;
                    case "acentos" or "semacento":
                        TirarAcentos();
                        Salvar(serial);
                        break;
                    case "d" or "digitacao" or "digitação":
                        _digitacaoHumana = !_digitacaoHumana;
                        options.Value.HumanTyping = _digitacaoHumana;
                        AnsiConsole.MarkupLine(_digitacaoHumana
                            ? "[bold]digitação humana LIGADA:[/] digita caractere a caractere, o destinatário vê "
                              + "\"digitando…\". [yellow]só ASCII: acento e emoji são barrados no pré-voo.[/]"
                            : "[bold]digitação humana DESLIGADA:[/] o texto vai pronto pelo deep link, então "
                              + "[green]aceita acento, emoji e quebra de linha[/]. "
                              + "[yellow]em troca não há digitação, e o destinatário não vê \"digitando…\".[/]");
                        Salvar(serial);
                        break;
                    case "x" or "remover" or "excluir":
                        Remover(partes);
                        Salvar(serial);
                        break;
                    case "limpar":
                        Limpar(partes);
                        Salvar(serial);
                        break;
                    case "sair" or "exit" or "quit":
                        sair = true;
                        break;
                    default:
                        AnsiConsole.MarkupLine($"[red]comando desconhecido:[/] {partes[0].EscapeMarkup()}. digite [bold]ajuda[/].");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C cancela o token do processo inteiro (Program.cs), então não dá pra voltar ao
                // prompt: o próximo envio nasceria cancelado. Sai avisando, com o estado salvo.
                AnsiConsole.MarkupLine("\n[yellow]interrompido. o que já saiu está no log; a lista foi salva.[/]");
                Salvar(serial);
                return 1;
            }
        }

        Salvar(serial);
        return 0;
    }

    // ── Aparelho ─────────────────────────────────────────────────────────────────────────────────

    private async Task<bool> AparelhoPronto(CancellationToken ct)
    {
        var status = await phone.GetStatusAsync(ct);
        AnsiConsole.MarkupLine($"aparelho: [bold]{status.State.EscapeMarkup()}[/] (running={status.Running})");
        if (!status.Running)
        {
            AnsiConsole.MarkupLine(status.State switch
            {
                "unauthorized" =>
                    "[red]aparelho não autorizado.[/] aceite \"Permitir depuração USB?\" na tela do celular "
                    + "e marque \"Sempre permitir deste computador\".",
                _ =>
                    "[red]aparelho indisponível.[/] confira: cabo de DADOS (não só carga), Depuração USB "
                    + "ligada, tela desbloqueada, Phone__AdbSerial e Phone__AdbPath.",
            });
            return false;
        }
        if (!await phone.IsBootedAsync(ct))
        {
            AnsiConsole.MarkupLine("[red]Android não respondeu ao getprop.[/]");
            return false;
        }
        return true;
    }

    // ── Entrada de dados ─────────────────────────────────────────────────────────────────────────

    /// <summary>Fim do bloco colado. Linha vazia é o padrão, mas "." e "fim" também valem: com UM
    /// número a digitar, "dê Enter duas vezes" não é óbvio, e a pessoa fica presa achando que o
    /// console travou.</summary>
    private static bool FimDoBloco(string? linha) =>
        linha is null || linha.Trim() is "" or "." or "fim";

    /// <summary>Lê UMA variante, que pode ter várias linhas. Linha vazia fecha a variante; "fim" (ou o
    /// fim da entrada) encerra tudo.</summary>
    /// <returns>Texto null = a pessoa não digitou nada, ou seja, encerrou a lista.</returns>
    private static (string? Texto, bool Fim) LerVariante()
    {
        var linhas = new List<string>();
        while (true)
        {
            var l = Console.ReadLine();
            if (l is null || l.Trim() is "fim" or ".")
            {
                return (Juntar(linhas), true);
            }
            if (l.Trim().Length == 0)
            {
                return (Juntar(linhas), false);
            }
            linhas.Add(l.TrimEnd());
        }

        static string? Juntar(List<string> linhas) =>
            linhas.Count == 0 ? null : string.Join('\n', linhas);
    }

    /// <summary>Uma linha colada vira contato, ou o motivo da recusa. Aceita ; , ou tab como
    /// separador: o que vem de planilha varia, e obrigar um formato só transforma "colar a lista" em
    /// "limpar a lista antes de colar".</summary>
    private static (Contato? Contato, string? Erro) ParseContato(string linha)
    {
        var campos = linha.Split([';', ',', '\t'], 2);
        var parteNumero = campos[0];
        var parteNome = campos.Length > 1 ? campos[1] : null;

        if (parteNome is null)
        {
            // 🔴 Sem separador explícito, corta na primeira LETRA. Antes só ; , e tab valiam, então
            // "5571993836443 Fulano" perdia o nome EM SILÊNCIO (medido 2026-07-30, com o operador
            // achando que o nome não era digitado). Número formatado — "55 71 99383-6443", "(71)
            // 99383-6443" — não tem letra nenhuma, então continua inteiro.
            var i = 0;
            while (i < linha.Length && !char.IsLetter(linha[i]))
            {
                i++;
            }
            if (i < linha.Length)
            {
                parteNumero = linha[..i];
                parteNome = linha[i..];
            }
        }

        var numero = new string([.. parteNumero.Where(char.IsDigit)]);
        var nome = parteNome?.Trim();

        // 12-13 = 55 + DDD + número (legado sem o 9º dígito dá 12). Fora disso é DDD faltando ou
        // dígito sobrando — o mesmo erro que quase mandou "55921404487" em 2026-07-29.
        return numero.Length is < 12 or > 13
            ? (null, $"{numero.Length} dígitos, esperado 12 ou 13")
            : (new Contato(numero, string.IsNullOrWhiteSpace(nome) ? null : nome), null);
    }

    private void LerContatos(bool somar)
    {
        if (!somar)
        {
            _contatos.Clear();
        }
        AnsiConsole.MarkupLine("[grey]um por linha:[/] [bold]numero[/] [grey]ou[/] [bold]numero;nome[/] [grey](espaço, vírgula e tab também separam)[/]");
        AnsiConsole.MarkupLine("[grey]para terminar:[/] [bold]Enter numa linha vazia[/] [grey](ou seja, Enter duas vezes no fim). também vale digitar[/] fim[grey].[/]");

        var jaTem = _contatos.Select(c => c.Numero).ToHashSet(StringComparer.Ordinal);
        var aceitos = 0;
        var repetidos = 0;
        var rejeitados = new List<string>();

        while (true)
        {
            var l = Console.ReadLine();
            if (FimDoBloco(l))
            {
                break;
            }

            var (contato, erro) = ParseContato(l!);
            if (contato is null)
            {
                rejeitados.Add($"{l!.Trim()} → {erro}");
                continue;
            }
            if (!jaTem.Add(contato.Numero))
            {
                repetidos++;
                continue;
            }
            _contatos.Add(contato);
            aceitos++;
        }

        AnsiConsole.MarkupLine($"[green]{aceitos}[/] contato(s) aceito(s); lista agora tem [bold]{_contatos.Count}[/].");
        if (repetidos > 0)
        {
            AnsiConsole.MarkupLine($"[grey]{repetidos} repetido(s) descartado(s).[/]");
        }
        MostrarAlguns(rejeitados.Count, 10, i => $"[red]rejeitado:[/] {rejeitados[i].EscapeMarkup()}", " rejeitado(s)");
    }

    private void LerTextos(bool somar)
    {
        if (!somar)
        {
            _textos.Clear();
        }
        AnsiConsole.MarkupLine($"[grey]use[/] [bold]{TokenNome}[/] [grey]onde entra o nome do contato. cada template pode ter VÁRIAS linhas.[/]");
        AnsiConsole.MarkupLine("[grey]uma[/] [bold]linha vazia[/] [grey]fecha o template. outra linha vazia (ou[/] fim[grey]) encerra.[/]");

        var antes = _textos.Count;
        while (true)
        {
            // O cabeçalho numerado antes de CADA bloco é o que transforma "linhas separadas por
            // espaço em branco" em "template 1, template 2": a numeração some se ela só existir na
            // cabeça de quem digita.
            AnsiConsole.MarkupLine($"\n[blue]── template {_textos.Count + 1} ──[/]");
            var (texto, fim) = LerVariante();
            if (texto is not null)
            {
                _textos.Add(texto);
                var linhas = texto.Count(c => c == '\n') + 1;
                AnsiConsole.MarkupLine($"[green]template {_textos.Count} gravado[/] [grey]({linhas} linha(s))[/]");
            }
            if (fim || texto is null)
            {
                break;
            }
        }

        AnsiConsole.MarkupLine(
            $"[green]{_textos.Count - antes}[/] template(s) neste bloco; total [bold]{_textos.Count}[/].");
        AvisarNaoDigitaveis();
    }

    /// <summary>Aponta o que o `input text` do Android não digita, no momento em que o texto é colado.
    /// O veredito final é do engine (<see cref="IPhoneOrchestrator.CheckTypingCapabilityAsync"/>), mas
    /// descobrir na hora de colar é muito mais barato que descobrir no pré-voo do lote.</summary>
    private void AvisarNaoDigitaveis()
    {
        var suspeitas = _textos
            .Select((t, i) => (Indice: i + 1, Chars: NaoAscii(t)))
            .Where(x => x.Chars.Length > 0)
            .ToList();
        if (suspeitas.Count == 0)
        {
            return;
        }
        foreach (var (indice, chars) in suspeitas)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]template {indice}[/] tem caractere fora do ASCII: [bold]{chars.EscapeMarkup()}[/]");
        }
        if (!_digitacaoHumana)
        {
            AnsiConsole.MarkupLine("[grey]sem problema: a digitação humana está desligada, o texto vai pronto.[/]");
            return;
        }

        // Oferece a conversão em vez de só reclamar. Escrever português é escrever com acento, e
        // mandar a pessoa reescrever à mão a cada colagem é atrito garantido, toda vez.
        AnsiConsole.Markup("[grey]tirar os acentos automaticamente? (s/n):[/] ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant().StartsWith('s') != true)
        {
            AnsiConsole.MarkupLine("[yellow]mantido. o pré-voo vai barrar o lote enquanto a digitação humana estiver ligada.[/]");
            return;
        }
        TirarAcentos();
    }

    private void TirarAcentos()
    {
        for (var i = 0; i < _textos.Count; i++)
        {
            _textos[i] = SemAcento(_textos[i]);
        }
        var sobrou = _textos.Select((t, i) => (Indice: i + 1, Chars: NaoAscii(t))).Where(x => x.Chars.Length > 0).ToList();
        AnsiConsole.MarkupLine("[green]acentos removidos.[/]");
        MostrarTextos();
        foreach (var (indice, chars) in sobrou)
        {
            // Emoji e símbolo não têm versão sem acento: não há o que converter, só o que apagar —
            // e apagar caractere do texto de alguém sem avisar seria pior que barrar.
            AnsiConsole.MarkupLine(
                $"[red]template {indice}[/] ainda tem [bold]{chars.EscapeMarkup()}[/], que não é acento "
                + "(emoji ou símbolo). tire à mão, ou desligue a digitação humana com [bold]d[/].");
        }
    }

    private const string ComAcento = "áàâãäéèêëíìîïóòôõöúùûüçñýÁÀÂÃÄÉÈÊËÍÌÎÏÓÒÔÕÖÚÙÛÜÇÑÝ";
    private const string SemAcentoTabela = "aaaaaeeeeiiiiooooouuuucnyAAAAAEEEEIIIIOOOOOUUUUCNY";

    /// <summary>"Promoção" → "Promocao", por tabela.</summary>
    /// <remarks>
    /// 🔴 O caminho idiomático (<c>Normalize(FormD)</c> + descartar NonSpacingMark) NÃO funciona aqui:
    /// o <c>Directory.Build.props</c> liga <c>InvariantGlobalization</c> na solução inteira, e nesse
    /// modo o Normalize é NO-OP silencioso — devolve o texto igual, sem erro nenhum. Medido em
    /// 2026-07-30: a remoção "funcionava" e o acento continuava lá.
    /// </remarks>
    private static string SemAcento(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
        {
            var i = ComAcento.IndexOf(c, StringComparison.Ordinal);
            sb.Append(i >= 0 ? SemAcentoTabela[i] : c);
        }
        return sb.ToString();
    }

    // '\n' fica de fora: a quebra de linha não é digitada como texto, vai como tecla Enter.
    private static string NaoAscii(string texto) =>
        new([.. texto.Where(c => c != '\n' && (c > '~' || c < ' ')).Distinct()]);

    // ── Visualização ─────────────────────────────────────────────────────────────────────────────

    private void Ver()
    {
        if (_contatos.Count == 0 && _textos.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]nada carregado. use[/] [bold]contatos[/] [grey]e[/] [bold]textos[/].");
            return;
        }

        MostrarTextos();

        if (_contatos.Count > 0)
        {
            var semNome = _contatos.Count(c => c.Nome is null);
            AnsiConsole.MarkupLine($"[bold]{_contatos.Count}[/] contato(s), {semNome} sem nome.");
            MostrarAlguns(_contatos.Count, 15, i => $"  [blue]{i + 1}[/] {DescreverContato(i)}");
        }

        AnsiConsole.MarkupLine(
            $"[grey]intervalo {_min}-{_max}s · teto {_teto} por lote · agenda {(_agenda ? "ligada" : "desligada")}[/]");
    }

    /// <summary>Os templates INTEIROS, numerados. É o que permite escolher um deles: o resumo do menu
    /// corta, e dois templates longos que só diferem no fim ficam iguais na tela.</summary>
    private void MostrarTextos()
    {
        if (_textos.Count == 0)
        {
            return;
        }
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("#");
        t.AddColumn("linhas");
        t.AddColumn("template");
        for (var i = 0; i < _textos.Count; i++)
        {
            t.AddRow(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                (_textos[i].Count(c => c == '\n') + 1).ToString(CultureInfo.InvariantCulture),
                _textos[i].EscapeMarkup());
        }
        AnsiConsole.Write(t);
    }

    private void Previa()
    {
        if (!TemMaterial())
        {
            return;
        }
        if (ProblemaDeNome() is { } problema)
        {
            AnsiConsole.MarkupLine($"[red]{problema}[/]");
            return;
        }
        AnsiConsole.MarkupLine("[grey]simulação — o sorteio é refeito no envio, então a distribuição real será outra.[/]");
        var semNome = _contatos.Count(c => c.Nome is null);
        if (semNome > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{semNome} contato(s) sem nome: recebem só templates que não usam {TokenNome}.[/]");
        }
        MostrarPlano(Sortear());
    }

    /// <summary>Sorteia uma variante por contato. Sorteio, e não rodízio, porque rodízio cria uma
    /// regularidade (contato 1 = variante A, contato 4 = variante A…) que é justamente o padrão que
    /// variar o texto tenta desfazer.</summary>
    private List<(Contato Contato, int Variante, string Texto)> Sortear()
    {
        // Contato SEM nome sorteia só entre os templates que não usam {nome}. Antes, um único contato
        // sem nome barrava o lote inteiro; mas o problema nunca foi do lote, era da combinação
        // contato-sem-nome com template-que-pede-nome. Restringir o sorteio resolve na origem.
        var todos = Enumerable.Range(0, _textos.Count).ToList();
        var neutros = todos.Where(i => !_textos[i].Contains(TokenNome, StringComparison.OrdinalIgnoreCase)).ToList();

        return [.. _contatos.Select(c =>
        {
            var pool = c.Nome is null ? neutros : todos;
            var i = pool[Random.Shared.Next(pool.Count)];
            return (c, i + 1, _textos[i].Replace(TokenNome, c.Nome ?? "", StringComparison.OrdinalIgnoreCase));
        })];
    }

    /// <summary>null = dá pra sortear. string = motivo, no único caso impossível: existe contato sem
    /// nome e NENHUM template dispensa o {nome}.</summary>
    private string? ProblemaDeNome()
    {
        var semNome = _contatos.Count(c => c.Nome is null);
        if (semNome == 0 || _textos.Any(t => !t.Contains(TokenNome, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return $"{semNome} contato(s) sem nome, e TODOS os {_textos.Count} template(s) usam {TokenNome}. "
            + "acrescente um template sem o token, ou complete os nomes colando numero;nome.";
    }

    private static void MostrarPlano(List<(Contato Contato, int Variante, string Texto)> plano)
    {
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("número");
        t.AddColumn("nome");
        t.AddColumn("tpl");
        t.AddColumn("texto");
        foreach (var (c, v, texto) in plano.Take(20))
        {
            t.AddRow(c.Numero, (c.Nome ?? "-").EscapeMarkup(), v.ToString(CultureInfo.InvariantCulture), texto.EscapeMarkup());
        }
        AnsiConsole.Write(t);
        if (plano.Count > 20)
        {
            AnsiConsole.MarkupLine($"[grey]… e mais {plano.Count - 20} contato(s).[/]");
        }
    }

    private bool TemMaterial()
    {
        if (_contatos.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]sem contatos.[/] use [bold]contatos[/] e cole a lista.");
            return false;
        }
        if (_textos.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]sem textos.[/] use [bold]textos[/] e cole ao menos um template.");
            return false;
        }
        return true;
    }

    // ── Envio ────────────────────────────────────────────────────────────────────────────────────

    private async Task EnviarAsync(string serial, CancellationToken ct)
    {
        if (!TemMaterial())
        {
            return;
        }
        if (_contatos.Count > _teto)
        {
            AnsiConsole.MarkupLine(
                $"[red]{_contatos.Count} contatos passam do teto de {_teto} por lote.[/] "
                + $"suba com [bold]teto {_contatos.Count}[/] se for consciente, ou reduza a lista.");
            return;
        }

        if (ProblemaDeNome() is { } problema)
        {
            AnsiConsole.MarkupLine($"[red]{problema}[/]");
            return;
        }

        if (!await AparelhoPronto(ct))
        {
            return;
        }

        var plano = Sortear();

        // Pré-voo da DIGITAÇÃO com o texto JÁ resolvido: o nome do contato pode trazer o acento que a
        // variante não tinha. Uma sonda com tudo concatenado dá o veredito do engine numa chamada só.
        var sonda = string.Concat(plano.Select(p => p.Texto).Distinct());
        if (await phone.CheckTypingCapabilityAsync(sonda, ct) is { } motivo)
        {
            AnsiConsole.MarkupLine($"[red]não dá pra digitar este lote:[/] {motivo.EscapeMarkup()}");
            var culpados = NaoAscii(sonda);
            if (culpados.Length > 0)
            {
                AnsiConsole.MarkupLine($"[red]caracteres problemáticos no lote:[/] [bold]{culpados.EscapeMarkup()}[/]");
            }
            return;
        }

        MostrarPlano(plano);
        AvisarRepeticao(plano, serial);

        var estimativa = TimeSpan.FromSeconds((plano.Count - 1) * ((_min + _max) / 2.0));
        AnsiConsole.MarkupLine(
            $"[bold]{plano.Count}[/] mensagem(ns), intervalo {_min}-{_max}s, "
            + $"~[bold]{Duracao(estimativa)}[/] só de espera (o envio em si soma mais).");
        AnsiConsole.Markup("[bold]confirmar? digite[/] sim [bold]:[/] ");

        if (!string.Equals(Console.ReadLine()?.Trim(), "sim", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]cancelado, nada foi enviado.[/]");
            return;
        }

        var log = AbrirLog(serial);
        var (enviados, falhas) = await DispararAsync(plano, log, serial, ct);

        AnsiConsole.MarkupLine(
            falhas == 0
                ? $"[green]lote concluído: {enviados} enviada(s), sem falhas.[/]"
                : $"[yellow]lote concluído: {enviados} enviada(s), {falhas} falha(s).[/]");
        AnsiConsole.MarkupLine($"[grey]log: {log.EscapeMarkup()}[/]");
    }

    /// <summary>O laço de disparo. Separado do <see cref="EnviarAsync"/> porque lá tudo é decisão
    /// (pode? vale a pena? confirma?) e aqui tudo é execução — e porque um laço com efeito
    /// irreversível merece um <c>finally</c> visível em vez de ficar no meio de 140 linhas.</summary>
    private async Task<(int Enviados, int Falhas)> DispararAsync(
        List<(Contato Contato, int Variante, string Texto)> plano,
        string log,
        string serial,
        CancellationToken ct)
    {
        var enviados = 0;
        var falhas = 0;
        var entregues = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            for (var i = 0; i < plano.Count; i++)
            {
                var (contato, variante, texto) = plano[i];

                if (_agenda)
                {
                    var saved = await phone.SaveContactAsync(contato.Numero, contato.Nome, ct);
                    AnsiConsole.MarkupLine($"[grey]agenda[/] {contato.Numero}: {saved.EscapeMarkup()}");
                }

                var r = await phone.SendWhatsAppMessageAsync(contato.Numero, texto, ct);
                if (r.Sent)
                {
                    enviados++;
                    entregues.Add(contato.Numero);
                    AnsiConsole.MarkupLine(
                        $"[green]({i + 1}/{plano.Count}) enviado[/] {contato.Numero} tpl {variante} "
                        + $"(entrega: {r.DeliveryStatus ?? "?"})");
                }
                else
                {
                    falhas++;
                    AnsiConsole.MarkupLine(
                        $"[red]({i + 1}/{plano.Count}) falhou[/] {contato.Numero}: {(r.Error ?? "").EscapeMarkup()}");
                }
                Registrar(log, serial, contato, variante, texto, r);

                if (i < plano.Count - 1)
                {
                    var espera = Random.Shared.Next(_min, _max + 1);
                    AnsiConsole.MarkupLine($"[grey]aguardando {espera}s antes do próximo…  (Ctrl+C interrompe)[/]");
                    await Task.Delay(TimeSpan.FromSeconds(espera), ct);
                }
            }
        }
        finally
        {
            // 🔴 Quem RECEBEU sai da lista, e no FINALLY: com Ctrl+C no meio do lote a exceção pulava
            // a limpeza, e os já entregues voltavam na próxima abertura prontos pra receber de novo —
            // justamente a proteção que este trecho existe pra dar. Quem FALHOU fica, porque falha é
            // o que se quer tentar de novo.
            _contatos.RemoveAll(c => entregues.Contains(c.Numero));
            Salvar(serial);
            if (entregues.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]{entregues.Count} contato(s) que receberam saíram da lista. "
                    + $"restam {_contatos.Count} (os que falharam continuam, para tentar de novo).[/]");
            }
        }

        return (enviados, falhas);
    }

    /// <summary>Avisa quem desta lista já recebeu deste aparelho. O log é a única memória entre
    /// execuções. Avisa e deixa decidir, em vez de pular calado: reenviar às vezes é intencional.</summary>
    private void AvisarRepeticao(List<(Contato Contato, int Variante, string Texto)> plano, string serial)
    {
        var jaReceberam = NumerosJaEnviados(serial);
        var repetidos = plano.Where(p => jaReceberam.Contains(p.Contato.Numero)).ToList();
        if (repetidos.Count == 0)
        {
            return;
        }
        AnsiConsole.MarkupLine(
            $"[yellow]atenção: {repetidos.Count} número(s) desta lista JÁ receberam mensagem deste aparelho antes.[/]");
        MostrarAlguns(repetidos.Count, 5,
            i => $"  [yellow]{repetidos[i].Contato.Numero}[/] {(repetidos[i].Contato.Nome ?? "").EscapeMarkup()}");
        AnsiConsole.MarkupLine("[yellow]tire com[/] x [yellow]se não quiser mandar de novo.[/]");
    }

    private static string Duracao(TimeSpan t) =>
        t.TotalMinutes < 1 ? "menos de 1min"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}min"
        : $"{(int)t.TotalHours}h {t.Minutes}min";

    // ── Ajustes ──────────────────────────────────────────────────────────────────────────────────

    private void Intervalo(string[] partes)
    {
        if (partes.Length < 3
            || !int.TryParse(partes[1], out var min)
            || !int.TryParse(partes[2], out var max)
            || min < 0 || max < min)
        {
            AnsiConsole.MarkupLine("[red]uso:[/] intervalo <min> <max>  (segundos, max >= min)");
            return;
        }
        (_min, _max) = (min, max);
        AnsiConsole.MarkupLine($"intervalo entre envios: [bold]{_min}-{_max}s[/].");
        if (max < 60)
        {
            AnsiConsole.MarkupLine("[yellow]intervalo curto num chip novo é o gatilho de ban que este projeto tenta evitar.[/]");
        }
    }

    private void Teto(string[] partes)
    {
        if (partes.Length < 2 || !int.TryParse(partes[1], out var n) || n < 1)
        {
            AnsiConsole.MarkupLine($"[red]uso:[/] teto <n>   (atual: {_teto})");
            return;
        }
        _teto = n;
        AnsiConsole.MarkupLine($"teto por lote: [bold]{_teto}[/].");
    }

    private void Limpar(string[] partes)
    {
        var alvo = partes.Length > 1 ? partes[1].ToLowerInvariant() : "tudo";
        if (alvo is "contatos" or "tudo")
        {
            _contatos.Clear();
        }
        if (alvo is "textos" or "tudo")
        {
            _textos.Clear();
        }
        AnsiConsole.MarkupLine($"limpo: [bold]{alvo.EscapeMarkup()}[/].");
    }

    /// <summary>O menu, com o VALOR ATUAL de cada ajuste ao lado. Redesenhado antes de cada escolha,
    /// para que toda alteração apareça gravada na hora seguinte em que você olha.</summary>
    /// <remarks>
    /// A lista numerada da ajuda parecia um menu sem ser, e a primeira pessoa a usar o console
    /// perguntou se digitava "1" para configurar o passo 1. Em vez de avisar que não era menu, virou
    /// menu. Os comandos por extenso continuam valendo: quem já sabe não quer navegar.
    /// </remarks>
    private void Menu(string serial)
    {
        var t = new Table().Border(TableBorder.Rounded).HideHeaders();
        t.AddColumn(new TableColumn("n").NoWrap());
        t.AddColumn(new TableColumn("o que é"));
        t.AddColumn(new TableColumn("agora"));

        t.AddRow("[bold]1[/]", "ritmo entre mensagens", $"[bold]{_min}-{_max}s[/]");
        t.AddRow("[bold]2[/]", "teto por lote", $"[bold]{_teto}[/]");
        t.AddRow("[bold]3[/]", "gravar na agenda", _agenda ? "[bold]ligado[/]" : "[grey]desligado[/]");
        t.AddRow("[bold]d[/]", "digitação humana",
            _digitacaoHumana
                ? "[bold]ligada[/] [grey](só ASCII)[/]"
                : "[grey]desligada[/] [green](aceita acento)[/]");
        t.AddRow("[bold]4[/]", "contatos",
            _contatos.Count == 0 ? "[grey]vazio[/]" : $"[bold]{_contatos.Count}[/] na lista");
        t.AddRow("[bold]5[/]", "templates",
            _textos.Count == 0 ? "[grey]vazio[/]" : $"[bold]{_textos.Count}[/] template(s)");
        t.AddEmptyRow();
        t.AddRow("[bold]6[/]", "ver", "[grey]confere o que está carregado[/]");
        t.AddRow("[bold]7[/]", "previa", "[grey]quem recebe qual texto[/]");
        t.AddRow("[bold]8[/]", "enviar", "[grey]dispara o lote (pergunta antes)[/]");
        t.AddEmptyRow();
        t.AddRow("[bold]9[/]", "ajuda", "[grey]o passo a passo explicado[/]");
        t.AddRow("[bold]0[/]", "sair", "[grey]fecha (tudo fica salvo)[/]");

        AnsiConsole.Write(new Rule($"[bold]menu[/]  ·  aparelho [bold]{serial.EscapeMarkup()}[/]").LeftJustified());
        AnsiConsole.Write(t);

        // O CONTEÚDO gravado, não só a contagem: "2 variante(s)" não deixa ninguém reconhecer o que
        // escreveu, e é justamente o texto que precisa ser relido antes de disparar. Truncado para o
        // menu não virar uma parede quando a lista tiver 80 contatos.
        // A contagem de linhas ao lado é o que distingue no resumo dois templates que só diferem no
        // fim — o corte em 62 caracteres os deixaria idênticos na tela.
        Gravados("templates", _textos.Count, 6,
            i => $"[blue]{i + 1}[/] [grey]({_textos[i].Count(c => c == '\n') + 1}L)[/] {Resumir(_textos[i]).EscapeMarkup()}");
        Gravados("contatos", _contatos.Count, 5, i => $"[blue]{i + 1}[/] {DescreverContato(i)}");

        AnsiConsole.MarkupLine(
            "[grey]digite o número, [/]x[grey] para excluir um item, ou o comando ([/]comandos[grey] lista todos).[/]");
    }

    /// <summary>Exclusão de UM item. Sem isto, um dígito errado num contato obriga a recolar a lista
    /// inteira, e é aí que a pessoa apaga o certo junto com o errado.</summary>
    private void Remover(string[] partes)
    {
        var alvo = partes.Length > 1 ? partes[1].ToLowerInvariant() : "";
        if (alvo.Length == 0 || (alvo[0] is not ('c' or 't')))
        {
            AnsiConsole.Markup("[grey]excluir o quê? [/][bold]c[/][grey]ontato ou [/][bold]t[/][grey]exto:[/] ");
            alvo = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        }
        if (alvo.Length == 0 || (alvo[0] is not ('c' or 't')))
        {
            AnsiConsole.MarkupLine("[grey]cancelado.[/]");
            return;
        }

        var ehTexto = alvo[0] == 't';
        var chave = partes.Length > 2 ? partes[2] : null;
        if (chave is null)
        {
            if (ehTexto)
            {
                MostrarTextos();
            }
            AnsiConsole.Markup(ehTexto
                ? "[grey]qual template? (o número mostrado acima):[/] "
                : "[grey]qual contato? (o número da linha no menu, ou o telefone inteiro):[/] ");
            chave = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(chave))
        {
            AnsiConsole.MarkupLine("[grey]cancelado.[/]");
            return;
        }

        if (ehTexto)
        {
            if (!int.TryParse(chave, out var n) || n < 1 || n > _textos.Count)
            {
                AnsiConsole.MarkupLine($"[red]não há template {chave.EscapeMarkup()}.[/] o menu mostra a numeração.");
                return;
            }
            AnsiConsole.MarkupLine($"[green]removido[/] o template {n}: {_textos[n - 1].EscapeMarkup()}");
            _textos.RemoveAt(n - 1);
            return;
        }

        // 12 dígitos ou mais = é o telefone; menos que isso = é o índice da linha. Sem essa regra,
        // "3" seria ambíguo entre a terceira linha e um telefone impossível.
        var digitos = new string([.. chave.Where(char.IsDigit)]);
        var indice = digitos.Length >= 12
            ? _contatos.FindIndex(c => c.Numero == digitos)
            : (int.TryParse(chave, out var n2) ? n2 - 1 : -1);

        if (indice < 0 || indice >= _contatos.Count)
        {
            AnsiConsole.MarkupLine($"[red]não achei o contato {chave.EscapeMarkup()}.[/] o menu mostra a numeração.");
            return;
        }
        AnsiConsole.MarkupLine(
            $"[green]removido[/] {_contatos[indice].Numero} {(_contatos[indice].Nome ?? "").EscapeMarkup()}");
        _contatos.RemoveAt(indice);
    }

    /// <summary>Mostra os primeiros e resume o resto. Estava copiado em quatro lugares (menu,
    /// rejeitados, repetidos, contatos) antes de virar um método.</summary>
    private static void MostrarAlguns(int total, int teto, Func<int, string> linha, string sufixo = "")
    {
        for (var i = 0; i < Math.Min(total, teto); i++)
        {
            AnsiConsole.MarkupLine(linha(i));
        }
        if (total > teto)
        {
            AnsiConsole.MarkupLine($"  [grey]… e mais {total - teto}{sufixo}[/]");
        }
    }

    private static void Gravados(string titulo, int total, int teto, Func<int, string> linha)
    {
        if (total == 0)
        {
            return;
        }
        AnsiConsole.MarkupLine($"[grey]{titulo} gravados:[/]");
        MostrarAlguns(total, teto, i => $"  {linha(i)}", " (veja todos com ver)");
    }

    /// <summary>Uma linha só, para caber no menu. A quebra vira ⏎ visível: sem isso, uma mensagem de
    /// 3 linhas apareceria colada e ninguém veria onde ela quebra.</summary>
    private string DescreverContato(int i) =>
        $"{_contatos[i].Numero} {(_contatos[i].Nome is null ? "[grey](sem nome)[/]" : _contatos[i].Nome!.EscapeMarkup())}";

    private static string Resumir(string texto)
    {
        var plano = texto.Replace("\n", " ⏎ ", StringComparison.Ordinal);
        return plano.Length <= 62 ? plano : string.Concat(plano.AsSpan(0, 61), "…");
    }

    // ── Ações do menu: perguntam o valor em vez de exigir a sintaxe do comando ────────────────────

    private void IntervaloInterativo()
    {
        AnsiConsole.Markup($"[grey]intervalo atual {_min}-{_max}s. novos valores \"min max\" (Enter mantém):[/] ");
        var l = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(l))
        {
            AnsiConsole.MarkupLine("[grey]mantido.[/]");
            return;
        }
        Intervalo(["intervalo", .. l.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
    }

    private void TetoInterativo()
    {
        AnsiConsole.Markup($"[grey]teto atual {_teto}. novo teto (Enter mantém):[/] ");
        var l = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(l))
        {
            AnsiConsole.MarkupLine("[grey]mantido.[/]");
            return;
        }
        Teto(["teto", l.Trim()]);
    }

    private enum ModoLista { Substituir, Acrescentar, Editar, Cancelar }

    /// <summary>Pergunta o que fazer com uma lista que já tem conteúdo.</summary>
    /// <remarks>
    /// Opções NUMERADAS, não letras: "s" foi lido como "substituir aquele texto pelo novo" quando
    /// significava "apagar todos e recomeçar", e a diferença entre as duas leituras é destrutiva.
    /// O padrão (Enter) é CORRIGIR, que é a intenção mais comum e a única não destrutiva.
    /// </remarks>
    private static ModoLista PerguntarModo(int quantosJaTem, string oQue)
    {
        if (quantosJaTem == 0)
        {
            return ModoLista.Substituir;
        }
        AnsiConsole.MarkupLine($"[grey]já há {quantosJaTem} {oQue}. o que você quer fazer?[/]");
        AnsiConsole.MarkupLine("  [bold]1[/] [grey]corrigir (mostra o texto atual para você reescrever)[/]");
        AnsiConsole.MarkupLine("  [bold]2[/] [grey]acrescentar (mantém o que já existe e soma novos)[/]");
        AnsiConsole.MarkupLine("  [bold]3[/] [red]apagar tudo[/] [grey]e colar de novo[/]");
        AnsiConsole.Markup("[grey]escolha (Enter = corrigir):[/] ");

        var r = Console.ReadLine()?.Trim() ?? "";
        return r.Length == 0
            ? ModoLista.Editar
            : r[0] switch
            {
                '1' => ModoLista.Editar,
                '2' => ModoLista.Acrescentar,
                '3' => ModoLista.Substituir,
                _ => ModoLista.Cancelar,
            };
    }

    private void EditarTexto()
    {
        // Com UM template não há o que perguntar: perguntar "qual?" quando só existe um é a
        // burocracia que faz a pessoa achar que o programa não está prestando atenção.
        var n = 1;
        if (_textos.Count > 1)
        {
            // Lista INTEIRA antes de perguntar. O resumo do menu corta em 62 caracteres, e dois
            // templates que só diferem no fim aparecem idênticos lá (medido 2026-07-30).
            MostrarTextos();
            AnsiConsole.Markup("[grey]qual template? (o número mostrado acima):[/] ");
            if (!int.TryParse(Console.ReadLine()?.Trim(), out n) || n < 1 || n > _textos.Count)
            {
                AnsiConsole.MarkupLine("[red]não há esse template.[/] o menu mostra a numeração.");
                return;
            }
        }
        AnsiConsole.MarkupLine($"[grey]atual:[/]\n{_textos[n - 1].EscapeMarkup()}");
        AnsiConsole.MarkupLine("[grey]novo texto (pode ter várias linhas; linha vazia termina, Enter direto cancela):[/]");
        var (novo, _) = LerVariante();
        if (novo is null)
        {
            AnsiConsole.MarkupLine("[grey]cancelado, nada mudou.[/]");
            return;
        }
        _textos[n - 1] = novo;
        AnsiConsole.MarkupLine($"[green]template {n} atualizado.[/]");
        AvisarNaoDigitaveis();
    }

    private void EditarContato()
    {
        var n = 1;
        if (_contatos.Count > 1)
        {
            AnsiConsole.Markup("[grey]qual contato? (o número da linha no menu):[/] ");
            if (!int.TryParse(Console.ReadLine()?.Trim(), out n) || n < 1 || n > _contatos.Count)
            {
                AnsiConsole.MarkupLine("[red]não há essa linha.[/] o menu mostra a numeração.");
                return;
            }
        }
        AnsiConsole.MarkupLine($"[grey]atual:[/] {_contatos[n - 1].Numero} {(_contatos[n - 1].Nome ?? "").EscapeMarkup()}");
        AnsiConsole.Markup("[grey]novo (numero ou numero;nome, Enter cancela):[/] ");
        var linha = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(linha))
        {
            AnsiConsole.MarkupLine("[grey]cancelado, nada mudou.[/]");
            return;
        }
        var (contato, erro) = ParseContato(linha);
        if (contato is null)
        {
            AnsiConsole.MarkupLine($"[red]{erro}[/] nada mudou.");
            return;
        }
        if (_contatos.Any(c => c.Numero == contato.Numero && c != _contatos[n - 1]))
        {
            AnsiConsole.MarkupLine("[red]esse número já está na lista.[/] nada mudou.");
            return;
        }
        _contatos[n - 1] = contato;
        AnsiConsole.MarkupLine($"[green]linha {n} atualizada.[/]");
    }

    /// <summary>O passo a passo inteiro, na ordem de uso. Aparece sozinha ao abrir e volta com
    /// "ajuda" (ou 9).</summary>
    private static void Ajuda()
    {
        AnsiConsole.Write(new Rule("[bold]como usar, do começo ao fim[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]cada passo aceita o NÚMERO do menu ou o comando por extenso.[/]");

        // Texto curto NÃO é economia de espaço: passando de ~60 caracteres o Spectre quebra a linha e
        // a continuação volta pra coluna zero, perdendo o alinhamento que faz a tabela ser legível.
        // Texto curto NÃO é economia de espaço: passando de ~60 caracteres o Spectre quebra a linha e
        // a continuação volta pra coluna zero, perdendo o alinhamento que faz a tabela ser legível.
        Passo(1, "ritmo entre as mensagens",
            ("digite", "1   ou   intervalo 240 600"),
            ("resulta", "espera de 4 a 10 min entre mensagens, sorteada"),
            ("use", "240 600 com chip novo. o default é 150 360"));

        Passo(2, "limite por disparo",
            ("digite", "2   ou   teto 5"),
            ("resulta", "lista maior que 5 é recusada ANTES de começar"),
            ("use", "baixo: colagem errada vira recusa, não rajada"));

        Passo(3, "gravar na agenda",
            ("digite", "3   ou   agenda"),
            ("resulta", "alterna ligado e desligado a cada vez"),
            ("padrão", "LIGADO: grava o contato no celular antes de enviar"),
            ("já salvo", "detecta e não grava de novo, nem sobrescreve"));

        Passo(4, "colar os contatos",
            ("digite", "4   ou   contatos"),
            // Exemplo com número obviamente falso e nome de espaço reservado: a ajuda é impressa em
            // toda abertura, e número real de alguém não tem por que virar cartaz permanente.
            ("cole", "5511999999999;Nome"),
            ("feche", "Enter numa linha vazia (Enter 2x no fim), ou: fim"),
            ("formato", "numero   ou   numero;nome"),
            ("o nome", "só é preciso se você usar {nome} no texto"));

        Passo(5, "colar os textos",
            ("digite", "5   ou   textos"),
            ("cole", "Ola {nome}, tudo bem?"),
            ("+linhas", "continue digitando: a mensagem pode ter várias linhas"),
            ("separa", "linha vazia fecha o template e começa o próximo"),
            ("encerra", "outra linha vazia, ou digite: fim"),
            ("acento", "ele oferece tirar sozinho; ou tecle d pra aceitar acento"),
            ("sorteio", "cada contato recebe UM template sorteado"),
            ("sem nome", "contato sem nome só sorteia templates sem {nome}"));

        Passo(6, "errou algo? conserte só aquilo",
            ("corrigir", "entre de novo no 4 ou no 5 e escolha 1 (é o padrão)"),
            ("resulta", "mostra o texto atual e você reescreve por cima"),
            ("apagar", "x   (pergunta se é contato ou texto, e qual)"),
            ("atalho", "x contato 3   ·   x texto 2   ·   x contato 5511999999999"));

        Passo(7, "conferir antes",
            ("6  ver", "contatos, templates e a configuração atual"),
            ("7  previa", "quem recebe qual texto, sem tocar no aparelho"));

        Passo(8, "disparar",
            ("digite", "8   ou   enviar"),
            ("resulta", "confere o aparelho, mostra o plano e PERGUNTA"),
            ("atenção", "nada sai enquanto você não digitar: sim"),
            ("depois", "quem RECEBEU sai da lista; quem falhou fica"));

        // Uma linha, não quatro. O menu logo abaixo já mostra o estado salvo, então repetir "fica
        // salvo" em prosa é ruído que a pessoa aprende a pular, e junto com ele o resto.
        AnsiConsole.MarkupLine(
            "\n[grey]colar: botão direito do mouse  ·  Ctrl+C encerra  ·[/] [bold]comandos[/][grey]: lista seca[/]");
    }

    private static void Passo(int numero, string titulo, params (string Rotulo, string Texto)[] linhas)
    {
        AnsiConsole.MarkupLine($"\n [bold]{numero}. {titulo}[/]");
        foreach (var (rotulo, texto) in linhas)
        {
            AnsiConsole.MarkupLine($"    [blue]{rotulo,-9}[/] {texto.EscapeMarkup()}");
        }
    }

    private static void Comandos()
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumn("comando").AddColumn("o que faz");
        t.AddRow("contatos [grey]| contatos +[/]", "cola a lista (substitui | soma). formato: numero ou numero;nome");
        t.AddRow("textos [grey]| textos +[/]", $"cola os templates (substitui | soma). {TokenNome} vira o nome do contato");
        t.AddRow("ver", "mostra a lista, os templates e os ajustes atuais");
        t.AddRow("previa", "simula quem receberia qual template, sem tocar no aparelho");
        t.AddRow("enviar", "pré-voo, plano, confirmação e disparo do lote");
        t.AddRow("status", "reconsulta o aparelho pelo adb");
        t.AddRow("intervalo <min> <max>", "segundos entre um envio e o próximo (default 150 360)");
        t.AddRow("teto <n>", "máximo de mensagens por lote (default 30)");
        t.AddRow("agenda", "liga/desliga gravar o contato na agenda antes de enviar");
        t.AddRow("x [grey][[contato|texto]] [[n]][/]", "exclui UM item (pergunta se você não disser qual)");
        t.AddRow("limpar [grey][[contatos|textos|tudo]][/]", "esvazia o que você pedir");
        t.AddRow("ajuda", "o passo a passo explicado, o mesmo que aparece ao abrir");
        t.AddRow("sair", "fecha o console (a lista fica salva)");
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("[grey]Ctrl+C interrompe o lote em andamento e encerra o console.[/]");
    }

    // ── Persistência e log ───────────────────────────────────────────────────────────────────────

    private static string Pasta
    {
        get
        {
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MtrxSys", "phone-console");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private static string Higienizar(string serial) =>
        new([.. serial.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);

    /// <summary>Reserva o aparelho para ESTE processo. null = já tem outro console nele.</summary>
    /// <remarks>
    /// A exclusão é o próprio HANDLE do arquivo, não o conteúdo: enquanto o stream estiver aberto,
    /// ninguém mais consegue abri-lo para escrita. Isso resolve de graça o problema que um arquivo de
    /// PID tem — console morto no tranco (crash, janela fechada no X, PC desligado) deixaria a trava
    /// presa para sempre, porque não há quem apague. O Windows fecha o handle junto com o processo.
    /// <para><c>FileShare.Read</c> em vez de <c>None</c> para o atalho conseguir LER quem é o dono e
    /// mostrar "em uso" na lista, sem conseguir tomar a vaga.</para>
    /// </remarks>
    private static FileStream? Travar(string serial)
    {
        try
        {
            var fs = new FileStream(
                Path.Combine(Pasta, $"{Higienizar(serial)}.lock"),
                FileMode.Create, FileAccess.Write, FileShare.Read);
            var dono = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId};desde={DateTimeOffset.Now:O}\n");
            fs.Write(dono, 0, dono.Length);
            fs.Flush();
            return fs;
        }
        catch (IOException)
        {
            return null; // outro console segura o handle
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Lê o dono da trava. O arquivo é aberto com FileShare.Read justamente para isto.</summary>
    private static string QuemSegura(string serial)
    {
        try
        {
            var caminho = Path.Combine(Pasta, $"{Higienizar(serial)}.lock");
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var conteudo = sr.ReadToEnd().Trim();
            return string.IsNullOrEmpty(conteudo) ? "dono desconhecido" : conteudo.EscapeMarkup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "dono desconhecido";
        }
    }

    private void Carregar(string serial)
    {
        var caminho = Path.Combine(Pasta, $"{Higienizar(serial)}.json");
        if (!File.Exists(caminho))
        {
            return;
        }
        try
        {
            var e = JsonSerializer.Deserialize<Estado>(File.ReadAllText(caminho));
            if (e is null)
            {
                return;
            }
            foreach (var linha in e.Contatos)
            {
                var campos = linha.Split(';', 2);
                _contatos.Add(new Contato(campos[0], campos.Length > 1 && campos[1].Length > 0 ? campos[1] : null));
            }
            _textos.AddRange(e.Textos);
            (_min, _max, _agenda, _digitacaoHumana) = (e.MinDelay, e.MaxDelay, e.Agenda, e.DigitacaoHumana);
            if (e.Teto > 0)
            {
                // Zero = sessão gravada antes do teto existir. Nesse caso vale o default do --teto,
                // não um teto zerado que recusaria qualquer lista.
                _teto = e.Teto;
            }
            if (_contatos.Count > 0 || _textos.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]sessão anterior restaurada: {_contatos.Count} contato(s), {_textos.Count} template(s).[/]");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Estado é conveniência, não dado de verdade: se corromper, começa vazio em vez de travar.
            AnsiConsole.MarkupLine($"[grey]não deu pra restaurar a sessão anterior: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private void Salvar(string serial)
    {
        try
        {
            var e = new Estado
            {
                Contatos = [.. _contatos.Select(c => $"{c.Numero};{c.Nome ?? ""}")],
                Textos = [.. _textos],
                MinDelay = _min,
                MaxDelay = _max,
                Teto = _teto,
                Agenda = _agenda,
                DigitacaoHumana = _digitacaoHumana,
            };
            File.WriteAllText(Path.Combine(Pasta, $"{Higienizar(serial)}.json"), JsonSerializer.Serialize(e));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[grey]não deu pra salvar a sessão: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static string AbrirLog(string serial)
    {
        var caminho = Path.Combine(Pasta, $"envios-{Higienizar(serial)}.csv");
        if (!File.Exists(caminho))
        {
            File.WriteAllText(caminho, "quando;serial;numero;nome;variante;enviado;entrega;erro;texto\n", Encoding.UTF8);
        }
        return caminho;
    }

    /// <summary>Grava linha a linha, não no fim: se a janela morrer no meio do lote, o que já saiu
    /// continua registrado. Sem isto não há como saber quem já recebeu.</summary>
    private static void Registrar(
        string caminho, string serial, Contato c, int variante, string texto, WhatsAppSendResult r)
    {
        try
        {
            var linha = string.Join(';',
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                Csv(serial), Csv(c.Numero), Csv(c.Nome ?? ""), variante.ToString(CultureInfo.InvariantCulture),
                r.Sent ? "sim" : "nao", Csv(r.DeliveryStatus ?? ""), Csv(r.Error ?? ""), Csv(texto));
            File.AppendAllText(caminho, linha + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[grey]falha ao gravar no log: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>Quem já recebeu deste aparelho, lido do log. É a ÚNICA memória entre execuções: a
    /// lista em si é esvaziada dos entregues, mas nada impede recolar os mesmos números amanhã.</summary>
    private static HashSet<string> NumerosJaEnviados(string serial)
    {
        var numeros = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var caminho = Path.Combine(Pasta, $"envios-{Higienizar(serial)}.csv");
            if (!File.Exists(caminho))
            {
                return numeros;
            }
            foreach (var linha in File.ReadLines(caminho).Skip(1))
            {
                var campos = CamposCsv(linha);
                if (campos.Count > 5 && campos[5] == "sim")
                {
                    numeros.Add(campos[2]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log ilegível não pode impedir o envio; no pior caso o aviso de repetição não aparece.
        }
        return numeros;
    }

    /// <summary>Separa por ';' respeitando aspas, senão um nome com ';' desloca todas as colunas.</summary>
    private static List<string> CamposCsv(string linha)
    {
        var campos = new List<string>();
        var atual = new StringBuilder();
        var emAspas = false;
        foreach (var c in linha)
        {
            if (c == '"')
            {
                emAspas = !emAspas;
            }
            else if (c == ';' && !emAspas)
            {
                campos.Add(atual.ToString());
                atual.Clear();
            }
            else
            {
                atual.Append(c);
            }
        }
        campos.Add(atual.ToString());
        return campos;
    }

    private static string Csv(string v) =>
        v.Contains(';') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"").Replace('\n', ' ')}\""
            : v;
}
