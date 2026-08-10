using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;
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
/// <remarks>
/// <para>NÃO fala com o banco, e isso é escopo, não limitação: a lista vem COLADA. O console já teve um
/// comando <c>sistema</c> que trazia os contatos importados pelo painel, removido a pedido do operador
/// em 2026-08-10. Se voltar a ser preciso, o caminho é <c>IServiceScopeFactory</c> e escopo curto por
/// consulta, nunca <c>IContactRepository</c> direto: o repositório é Scoped e o <c>TypeResolver</c> do
/// CLI resolve do provider RAIZ, então injetá-lo aqui o tornaria cativo, com um <c>DbContext</c> vivo
/// pelas HORAS que o console fica aberto.</para>
/// </remarks>
internal sealed class PhoneConsoleCommand(
    IPhoneOrchestrator phone,
    IOptions<PhoneOptions> options,
    CancellationTokenProvider cancellation) : AsyncCommand<PhoneConsoleCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--teto <N>")]
        [Description("Cota por execução. 0 (default) = sem teto: manda a lista inteira.")]
        public int Teto { get; init; }
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
        // Anulável desde que zero virou "sem teto": como valor legítimo, ele não pode mais servir de
        // "não gravado". Sessão anterior a esta mudança tem o número que o operador escolheu e segue
        // valendo; sessão anterior ao campo tem null e cai no default.
        public int? Teto { get; init; }
        public int PararEm { get; init; }

        // Anuláveis de propósito: zero é valor LEGÍTIMO ("sem blocos"), então não dá pra usá-lo como
        // "não gravado". Sessão anterior a este ajuste tem null e cai no default; quem escolheu zero
        // continua com zero.
        public int? Bloco { get; init; }
        public int? PausaMin { get; init; }
        public int? HoraInicio { get; init; }
        public int? HoraFim { get; init; }
        public bool Agenda { get; init; } = true;
        public bool DigitacaoHumana { get; init; } = true;

        // Anulável pelo mesmo motivo do Bloco: false é escolha legítima. Sessão anterior ao campo tem
        // null e cai no default LIGADO; quem desligou continua desligado.
        public bool? Bip { get; init; }
    }

    private const string TokenNome = "{nome}";

    /// <summary>Falhas SEGUIDAS que interrompem o lote. ZERO = nunca interrompe, que é o padrão.</summary>
    /// <remarks>
    /// 🔴 ZERO POR DECISÃO DO OPERADOR, com o caso na mão (2026-08-07): num lote de 30, três números na
    /// forma errada, seguidos, derrubaram a execução em 22/30 com o aparelho perfeito, e 15 contatos
    /// bons ficaram sem receber. O argumento que decidiu: se falhou, nada saiu, então mostrar a falha e
    /// ir para o próximo não custa entrega nenhuma, enquanto travar custa o resto da lista. Quem falha
    /// continua na lista, então nada se perde ao seguir.
    ///
    /// <para>O custo de seguir foi levantado e é real: abrir conversa atrás de conversa para número que
    /// não existe é o padrão de bot enumerando. As respostas ficaram sendo o RITMO (a espera curta
    /// pós-falha só vale para falha isolada; virando sequência, volta ao intervalo normal) e o AVISO de
    /// aparelho suspeito. Ver <see cref="BatchStopPolicy"/>.</para>
    /// </remarks>
    private int _pararEm;

    /// <summary>Espera depois de uma FALHA, em segundos. Curta porque nada foi enviado, mas não zero
    /// porque abrir conversas em rajada para números inexistentes é o padrão de um bot enumerando.
    /// Mesmo intervalo que o Dispatcher usa para operações que não enviam.</summary>
    private const int FalhaEsperaMin = 8;
    private const int FalhaEsperaMax = 21;

    /// <summary>Stateless, então uma instância serve. Valida o que é COLADO — ver ParseContato.</summary>
    private static readonly BrazilPhoneValidator Telefones = new();

    /// <summary>Spintax do painel: <c>{a|b}</c>. Aqui NÃO é expandido, e é isso que o aviso conta.</summary>
    private static readonly Regex SpintaxRx = new(@"\{[^{}]*\|[^{}]*\}", RegexOptions.Compiled);

    private readonly List<Contato> _contatos = [];
    private readonly List<string> _textos = [];
    private int _min = 150;
    private int _max = 360;
    /// <summary>Cota desta execução. ZERO = sem teto, manda a lista inteira.</summary>
    /// <remarks>
    /// 🔴 SEM TETO POR PADRÃO, decisão do operador em 2026-08-07: "o teto pode ser 20, 50, 100, 1000, o
    /// usuário que vai decidir". Antes era 30 e RECUSAVA lista maior, o que empurrava para as duas
    /// saídas ruins: subir o teto para o tamanho da lista, que é a rajada que ele existia para impedir,
    /// ou recortar a lista à mão a cada execução.
    ///
    /// <para>O freio deixou de ser o teto e passou a ser o RITMO: blocos com pausa sorteada entre eles
    /// (ver <see cref="_bloco"/>), que é o que de fato desmancha o padrão de máquina. Teto continua
    /// disponível para quem quiser fatiar por dia, e o Ctrl+C interrompe a qualquer momento com o que
    /// já saiu registrado no CSV.</para>
    /// </remarks>
    private int _teto;

    /// <summary>Mensagens por BLOCO antes de uma pausa longa. Zero = sem blocos.</summary>
    /// <remarks>
    /// 🔴 O que pesa contra o chip é PADRÃO, não volume. O comentário da curva do <c>WarmupManager</c>
    /// registra quatro chips perdidos em quatro dias, dois deles com DUAS mensagens e um com ZERO: o
    /// que restringiu foi a assinatura de máquina, não a quantidade. Fluxo contínuo, hora após hora,
    /// no mesmo intervalo, é assinatura de máquina; gente manda um punhado, some, volta depois.
    ///
    /// <para>15 e 30 min não são chute: com o intervalo padrão de 150-360s, um bloco de 15 leva ~1h, e
    /// 1h + 30min = 1h30 por bloco. Num dia útil de 12h isso dá 8 blocos, ou seja 120 mensagens, que é
    /// exatamente o PLATÔ da curva de aquecimento do motor. Os dois caminhos, o automático e o manual,
    /// chegam ao mesmo teto diário sem que ninguém precise decorar dois números.</para>
    /// </remarks>
    private int _bloco = 15;

    /// <summary>Minutos de pausa entre um bloco e o próximo.</summary>
    private int _pausaMin = 30;

    /// <summary>Folga sorteada em torno do bloco e da pausa configurados.</summary>
    /// <remarks>
    /// 🔴 O CENTRO É O QUE VOCÊ CONFIGURA; O QUE SAI É SORTEADO EM VOLTA. Um loop de exatamente 15
    /// mensagens e exatamente 30 minutos é um carimbo perfeito no eixo do tempo: os intervalos entre
    /// mensagens já eram sorteados (150-360s), e a pausa cravada seria a ÚNICA regularidade da série,
    /// ou seja, exatamente a assinatura que o bloco existe para desmanchar. Com a folga, "15 e 30" na
    /// prática são 12 a 18 mensagens e 22 a 38 minutos.
    /// </remarks>
    private const double JitterBloco = 0.20;

    private const double JitterPausa = 0.25;

    /// <summary>Janela em que é permitido enviar (hora local). Fora dela a execução encerra.</summary>
    /// <remarks>
    /// 🔴 ABERTA (0h-24h) POR DECISÃO DO OPERADOR, 2026-08-07: o console tem que poder rodar em
    /// qualquer horário. A janela nasceu fechada em 8h-22h, copiando o <c>WarmupEngineOptions</c> e o
    /// <c>HumanPhaseOptions</c>, que usam esse par com o comentário "mandar mensagem de madrugada é
    /// sinal de robô". A diferença que justifica o padrão diferente aqui: aqueles dois são motores
    /// AUTOMÁTICOS, que decidem sozinhos a hora de escrever; este console só roda quando alguém abre e
    /// confirma, então quem escolhe o horário é uma pessoa, e a escolha é dela.
    ///
    /// <para>O mecanismo fica, desligado, em vez de ser removido: quem quiser a proteção liga com
    /// <c>janela 8 22</c>, e a razão dela continua registrada aqui em vez de virar conhecimento
    /// perdido. Ressalva que não muda com configuração: disparo às 4h destoa de comportamento humano
    /// mesmo com o ritmo e as pausas certas.</para>
    ///
    /// <para>Hora LOCAL, e não Brasília convertida de UTC como no servidor: aqui o console roda na
    /// máquina do operador, ao lado do celular, então o relógio da parede é o relógio certo.</para>
    /// </remarks>
    private int _horaInicio;

    private int _horaFim = 24;

    /// <summary>Grava o contato na agenda do aparelho antes de enviar. LIGADO por padrão por decisão
    /// do operador (2026-07-30). Sessão salva com o valor desligado continua desligada: escolha
    /// explícita ganha do default.</summary>
    private bool _agenda = true;

    /// <summary>Bip a cada mensagem, para acompanhar o lote de ouvido.</summary>
    /// <remarks>
    /// 🔴 O PONTO É NÃO PRECISAR OLHAR. Entre uma mensagem e a seguinte passam 150-360s, e um bloco
    /// leva ~1h: ninguém fica encarando o terminal esse tempo todo. Sem som, a única forma de saber que
    /// o lote anda é voltar na tela, e é aí que um lote que morreu às 2h só é descoberto às 8h.
    ///
    /// <para>DOIS TONS, e não um: o bip existe pra informar de longe, e "saiu" e "falhou" mandam a
    /// pessoa a lugares diferentes. Um bip único obrigaria a conferir a tela pra saber qual foi, ou
    /// seja, devolveria o problema que o som resolve. Agudo curto = saiu; dois graves = não saiu.</para>
    ///
    /// <para>Desligável porque este console roda em qualquer horário (a janela vem 0h-24h por decisão
    /// do operador), e bip de madrugada na casa dos outros é motivo pra fechar a janela do lote.</para>
    /// </remarks>
    private bool _bip = true;

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
        _teto = Math.Max(0, s.Teto);

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
                            case var m: LerTextos(serial, somar: m == ModoLista.Acrescentar); break;
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
                        LerTextos(serial, somar: partes is [_, "+", ..]);
                        Salvar(serial);
                        break;
                    case "ver":
                        Ver();
                        break;
                    case "previa":
                        Previa();
                        break;
                    case "c" or "conferir":
                        Conferir();
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
                    case "parar":
                        Parar(partes);
                        Salvar(serial);
                        break;
                    case "b" or "blocos":
                        BlocosOuInterativo(partes, serial);
                        break;
                    case "janela":
                        Janela(partes);
                        Salvar(serial);
                        break;
                    case "3" or "agenda":
                        _agenda = !_agenda;
                        AnsiConsole.MarkupLine($"gravar na agenda antes de enviar: [bold]{(_agenda ? "ligado" : "desligado")}[/]");
                        Salvar(serial);
                        break;
                    case "bip" or "som":
                        _bip = !_bip;
                        AnsiConsole.MarkupLine(_bip
                            ? "bip a cada mensagem: [bold]ligado[/] [grey](agudo curto = saiu; dois graves "
                              + "= não saiu). dá pra acompanhar o lote sem olhar a tela.[/]"
                            : "bip a cada mensagem: [grey]desligado (lote silencioso).[/]");
                        if (_bip)
                        {
                            await BiparAsync(true); // amostra na hora: som que ninguém ouviu não foi configurado.
                        }
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
                    case "gravar" or "g":
                        await GravarAgendaAsync(ct);
                        break;
                    default:
                        // 🔴 UMA LINHA EM BRANCO NO MEIO DA LISTA ENCERRA O BLOCO (ver FimDoBloco), e as
                        // linhas seguintes caem AQUI, uma a uma, como "comando desconhecido". Medido com
                        // o operador: 31 números colados, bloco cortado, e a mensagem genérica não
                        // ligava uma coisa à outra. Reconhecer que o "comando" é um telefone custa uma
                        // linha e transforma quinze erros crípticos num diagnóstico.
                        if (ParecemDigitosDeTelefone(linha))
                        {
                            AnsiConsole.MarkupLine(
                                "[yellow]isso parece um telefone, não um comando.[/] [grey]uma linha em "
                                + "branco no meio da lista encerra o bloco. use[/] [bold]contatos +[/] "
                                + "[grey]para SOMAR o resto sem perder o que já entrou.[/]");
                            break;
                        }
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

    /// <summary>A linha digitada no menu parece um telefone? Usado só para dar um diagnóstico melhor
    /// que "comando desconhecido" quando o bloco de contatos foi cortado por uma linha em branco.</summary>
    private static bool ParecemDigitosDeTelefone(string linha)
    {
        var digitos = linha.Count(char.IsDigit);
        // 10+ dígitos cobre do fixo com DDD ao E.164 completo, e nenhum comando do console tem
        // isso. "intervalo 150 360" tem 6, "teto 30" tem 2 — não caem aqui.
        return digitos >= 10;
    }

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
        if (numero.Length is < 12 or > 13)
        {
            return (null, $"{numero.Length} dígitos, esperado 12 ou 13");
        }

        // 🔴 COMPRIMENTO CERTO NÃO É NÚMERO CERTO. "5537368544314" tem 13 dígitos e DDD que existe,
        // mas o dígito depois do DDD não é 9 — não é celular brasileiro. Um caso IGUAL (um número da
        // Moldávia) passou por checagem só de comprimento em 2026-07-27, virou contato ativo, ganhou
        // entrada na agenda Google e foi enfileirado; só não recebeu porque alguém olhou a lista à mão.
        //
        // IsPlausibleBrazilian e NÃO o Validate estrito: o legado de 12 dígitos, SEM o 9º, é o caso
        // normal na base fria, e exigir validade de hoje já fez um grupo inteiro importar zero contatos.
        //
        // Vale para TODO contato, porque colar é a única entrada que existe. Enquanto havia o comando
        // `sistema`, o que vinha do banco pulava esta validação de propósito: aquilo veio do WhatsApp,
        // que não rotearia número inexistente, e revalidar formato ali descartaria contato bom. Fica
        // registrado porque a regra é a mesma se a entrada voltar: rigor se calibra pela origem do dado.
        if (!Telefones.IsPlausibleBrazilian("+" + numero))
        {
            return (null, "não parece um celular brasileiro (confira o 55, o DDD e o 9)");
        }

        return (new Contato(numero, string.IsNullOrWhiteSpace(nome) ? null : nome), null);
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

    /// <remarks>
    /// 🔴 SALVA A CADA TEMPLATE, e não só no fim. O `Salvar` de quem chama só roda quando o bloco
    /// INTEIRO termina, e o bloco só termina com a linha vazia extra do final. Quem colou três
    /// templates e fechou a janela sem dar esse Enter perdia os três, mesmo tendo lido
    /// "template N gravado" na tela — porque ali "gravado" era na memória. Medido com o operador em
    /// 2026-08-05: estado com 16 contatos e ZERO templates depois de colar três.
    /// Um template colado é a unidade de trabalho cara de refazer; é ela que define o ponto de
    /// persistência, não o bloco.
    /// </remarks>
    private void LerTextos(string serial, bool somar)
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
                Salvar(serial);   // ver o remarks: "gravado" tem que significar EM DISCO
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
        AvisarSpintax();
        AvisarNaoDigitaveis();
    }

    /// <summary>Aponta spintax colada num console que NÃO a expande.</summary>
    /// <remarks>
    /// O painel expande <c>{a|b}</c> (SpintaxExpander); aqui a ÚNICA substituição é o
    /// <see cref="TokenNome"/>. Sem este aviso, o texto é aceito em silêncio e o destinatário recebe
    /// literalmente <c>{Oi|E aí}</c>, com chaves e barra — descoberto por leitura, não por teste, e a
    /// única razão de ninguém ter recebido assim é que o operador perguntou antes de disparar.
    /// A variação aqui se faz com VÁRIOS templates: cada contato sorteia um.
    /// </remarks>
    private void AvisarSpintax()
    {
        var comSpintax = _textos
            .Select((t, i) => (Indice: i + 1, Achados: SpintaxRx.Matches(t)))
            .Where(x => x.Achados.Count > 0)
            .ToList();
        if (comSpintax.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]atenção:[/] {comSpintax.Count} template(s) têm [bold]{{a|b}}[/], e este console "
            + "[bold]não expande[/] spintax (só o " + TokenNome + ").");
        foreach (var (indice, achados) in comSpintax)
        {
            var amostra = string.Join(" ", achados.Take(3).Select(m => m.Value));
            AnsiConsole.MarkupLine(
                $"  [yellow]template {indice}:[/] {amostra.EscapeMarkup()}"
                + (achados.Count > 3 ? $" [grey](+{achados.Count - 3})[/]" : ""));
        }
        AnsiConsole.MarkupLine(
            "[grey]do jeito que está, o contato recebe as chaves e a barra. escreva cada variação como "
            + "um template separado — cada contato sorteia um.[/]");
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
            $"[grey]intervalo {_min}-{_max}s · teto {TetoDescrito()} · "
            + (_bloco > 0 ? $"blocos ~{_bloco}/pausa ~{_pausaMin}min · " : "sem blocos · ")
            + $"{JanelaDescrita()} · agenda {(_agenda ? "ligada" : "desligada")}[/]");
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

    /// <summary>Classifica cada número da lista pela FORMA, e diz se ele tem outra forma para a segunda
    /// chance. Não toca no aparelho.</summary>
    /// <remarks>
    /// 🔴 Existe porque "12 ou 13 dígitos" é a conta errada, e ela confundiu o operador e a mim em
    /// 2026-08-06. O que decide não é o comprimento, é o PRIMEIRO DÍGITO DO ASSINANTE: um "84 9471-5083"
    /// tem 12 dígitos e é celular de verdade, enquanto um "11 2140-4487" tem os mesmos 12 e é faixa de
    /// fixo. Contar dígitos separa mal; olhar a faixa separa certo.
    /// <para>Chama o <see cref="BrazilPhoneValidator"/> em vez de repetir a regra aqui. Um relatório que
    /// classifica por conta própria pode discordar do que o envio vai fazer, e aí ele tranquiliza sobre
    /// um comportamento que não é o real — que é o defeito mais caro que um relatório pode ter.</para>
    /// </remarks>
    private void Conferir()
    {
        if (_contatos.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]sem contatos.[/] use [bold]contatos[/] antes.");
            return;
        }

        // Diz pra que serve ANTES da tabela. Uma tabela que aparece sem introdução obriga quem lê a
        // deduzir a pergunta a partir das colunas, e as duas primeiras ("número", "díg") sugerem que
        // o assunto é comprimento — que é justamente a leitura errada.
        AnsiConsole.MarkupLine(
            "[grey]a FORMA de cada número, pelas mesmas regras que o envio usa. o que decide não é o "
            + "total de dígitos, é o[/] [bold]primeiro dígito depois do DDD[/][grey]: 6 a 9 é celular, "
            + "2 a 5 é fixo.[/]");

        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("#");
        t.AddColumn("número");
        t.AddColumn("díg");
        t.AddColumn("forma");
        t.AddColumn("outra forma (2ª chance)");

        var suspeitos = 0;
        for (var i = 0; i < _contatos.Count; i++)
        {
            var numero = _contatos[i].Numero;
            var forma = BrazilPhoneValidator.ShapeOf(numero);
            var alternativa = BrazilPhoneValidator.AlternateBrazilianForm(numero);
            if (forma is BrazilPhoneValidator.BrazilNumberShape.FixoOuSemONono
                or BrazilPhoneValidator.BrazilNumberShape.NaoBrasileiro)
            {
                suspeitos++;
            }
            t.AddRow(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                numero,
                numero.Length.ToString(CultureInfo.InvariantCulture),
                DescreverForma(forma),
                alternativa is null ? "[grey](nenhuma)[/]" : alternativa);
        }
        AnsiConsole.Write(t);

        if (suspeitos > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{suspeitos} número(s) merecem conferência na origem.[/] [grey]assinante começando "
                + "em 2-5 é faixa de fixo; se a pessoa tem celular, o que falta é o 9º dígito.[/]");
        }
        AnsiConsole.MarkupLine(
            "[grey]\"outra forma\" é o que a 2ª chance tentaria se o envio falhar. Vazio significa que não "
            + "existe alternativa plausível, e uma falha ali é definitiva.[/]");
    }

    private static string DescreverForma(BrazilPhoneValidator.BrazilNumberShape forma) => forma switch
    {
        BrazilPhoneValidator.BrazilNumberShape.CelularModerno => "[green]celular (13 díg)[/]",
        BrazilPhoneValidator.BrazilNumberShape.CelularLegado => "[green]celular legado (12 díg)[/]",
        BrazilPhoneValidator.BrazilNumberShape.FixoOuSemONono => "[yellow]FIXO ou falta o 9º[/]",
        _ => "[red]não é brasileiro[/]",
    };

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

    // ── Agenda e banco ───────────────────────────────────────────────────────────────────────────

    /// <summary>Grava na agenda do aparelho TODOS os contatos carregados, sem enviar nada.</summary>
    /// <remarks>
    /// <para>O <c>enviar</c> já grava, mas DOIS SEGUNDOS antes de cada mensagem — tarde demais para a
    /// cadeia anti-463. O contato precisa descer pela conta Google até o WhatsApp do aparelho; o
    /// <c>DispatchEngine</c> reconhece isso esperando <c>GraceSeconds</c> (180s) depois de criar.</para>
    /// <para>Separando a ação, você grava o lote inteiro, deixa sincronizar, e dispara depois com a
    /// agenda pronta — em vez de pagar a espera no meio do lote. É também o modo de usar o console SÓ
    /// como sincronizador, sem disparar por ele.</para>
    /// <para>Gravar é idempotente (<c>SaveContactAsync</c> devolve "já existe"), então repetir é
    /// seguro e barato.</para>
    /// </remarks>
    private async Task GravarAgendaAsync(CancellationToken ct)
    {
        if (_contatos.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]sem contatos.[/] use [bold]contatos[/] (cola a lista).");
            return;
        }
        if (!await AparelhoPronto(ct))
        {
            return;
        }

        // 🔴 CONFIRMAÇÃO OBRIGATÓRIA, e não é simetria com o `enviar` por gosto. Gravar escreve na
        // agenda de um celular REAL, que sincroniza pra conta Google: é ação difícil de desfazer. Cada
        // contato custa 3-4 chamadas adb, então uma lista grande é DEZENAS DE MINUTOS de laço — e listas
        // grandes acontecem (549 contatos colados de uma vez, na base do operador em 2026-08-05). Sem
        // este passo, um `g` distraído logo depois de uma colagem vira meia hora de escrita não
        // intencional.
        var estimativaMin = Math.Max(1, _contatos.Count * 3 / 60);
        AnsiConsole.MarkupLine(
            $"vai gravar [bold]{_contatos.Count}[/] contato(s) na agenda do aparelho "
            + $"[grey](~{estimativaMin} min; sincroniza pra conta Google do aparelho)[/].");

        // 🔴 O AVISO NÃO É BUROCRACIA. Número na forma errada (sem o 9º dígito, por exemplo) gravado na
        // agenda faz o WhatsApp sincronizar, não achar conta e passar a responder "não tem WhatsApp"
        // TODA VEZ para aquele número — inclusive quando a pessoa existe e está ativa. E não há como
        // desfazer por aqui: o adb grava contato, não apaga. Fazer isso com uma lista inteira de
        // origem duvidosa estraga o alcance do aparelho pra aquelas pessoas de forma persistente.
        // Diagnosticado em 2026-08-05, depois de horas perseguindo um contato que existia e o app
        // insistia em negar.
        AnsiConsole.MarkupLine(
            "[yellow]só faça isso com lista confiável.[/] [grey]número na forma errada gravado aqui faz "
            + "o WhatsApp marcá-lo como \"sem conta\" de forma PERSISTENTE, mesmo para quem existe — "
            + "e não dá pra desfazer pelo adb. Na dúvida, dispare primeiro e grave depois.[/]");
        AnsiConsole.Markup("[yellow]digite[/] [bold]sim[/] [yellow]para confirmar:[/] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "sim", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]cancelado. nada foi gravado.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[grey]Ctrl+C interrompe. gravar é idempotente, então rodar de novo continua de onde parou.[/]");

        // Mesma proteção do lote, e pelo mesmo motivo: a estimativa logo acima fala em DEZENAS DE
        // MINUTOS sem teclado nem mouse. Só o `enviar` estava coberto, e o gravar é justamente o que se
        // manda rodar e sai de perto (o fluxo recomendado é gravar o lote, esperar o sync e disparar
        // depois). Proteção que cobre um caminho e não o gêmeo dele é proteção que engana.
        using var acordado = PcAcordado.Ligar();

        var (criados, jaTinha, falhas) = await GravarPassadaAsync(_contatos, ct);

        // 🔴 SEGUNDA PASSADA SÓ NOS QUE FALHARAM. A gravação recusa quando outro escritor mexe na agenda
        // no meio (o sync do WhatsApp ou o da conta Google), e essa recusa é TRANSITÓRIA por construção.
        // Numa base inteira o laço leva dezenas de minutos com o sync ativo o tempo todo, então algumas
        // voltas vão cair aí. Sem isto, o operador teria que reparar no contador e rodar o comando de
        // novo pra algo que o próprio programa sabe que é só repetir.
        if (falhas.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{falhas.Count} não entrou(aram) na primeira passada; repetindo só esses…[/]");
            var (c2, j2, f2) = await GravarPassadaAsync([.. falhas.Select(f => f.Contato)], ct);
            (criados, jaTinha, falhas) = (criados + c2, jaTinha + j2, f2);
        }

        AnsiConsole.MarkupLine(
            $"[green]{criados}[/] criado(s), [grey]{jaTinha} já estava(m) na agenda[/]"
            + (falhas.Count > 0 ? $", [red]{falhas.Count} falha(s)[/]" : "") + ".");
        MostrarAlguns(falhas.Count, 10,
            i => $"[red]falhou:[/] {falhas[i].Contato.Numero} {falhas[i].Motivo.EscapeMarkup()}", " falha(s)");

        if (criados > 0)
        {
            // Não é enfeite: é o MESMO motivo do Defer de 180s do DispatchEngine. Disparar agora para
            // um contato recém-criado é disparar antes de o WhatsApp do aparelho conhecê-lo.
            AnsiConsole.MarkupLine(
                "[yellow]dê alguns minutos antes de disparar[/][grey]: contato recém-criado precisa "
                + "sincronizar pela conta Google até o WhatsApp do aparelho.[/]");
        }
    }

    /// <summary>Uma passada de gravação sobre a lista. Devolve os que falharam, pra quem chama decidir
    /// se repete.</summary>
    private async Task<(int Criados, int JaTinha, List<(Contato Contato, string Motivo)> Falhas)>
        GravarPassadaAsync(List<Contato> lista, CancellationToken ct)
    {
        var criados = 0;
        var jaTinha = 0;
        var falhas = new List<(Contato, string)>();

        for (var i = 0; i < lista.Count; i++)
        {
            var c = lista[i];
            // 🔴 As comparações abaixo dependem do TEXTO devolvido pelas implementações de
            // SaveContactAsync (WhatsAppContactsReader no físico, DockerCliPhoneOrchestrator no
            // emulador). As duas concordam hoje em "ok" e "já existe"; nada no compilador garante isso.
            // Se um dia divergirem, o sintoma aqui é TUDO virar "falha" com o contato gravado do mesmo
            // jeito — por isso a mensagem crua vai no relatório, em vez de só o contador.
            var r = await phone.SaveContactAsync(c.Numero, c.Nome, ct);
            // ⚠️ StartsWith e não igualdade: o sucesso pode vir qualificado ("ok (corrigido: …)"), e
            // comparar exato transformaria uma gravação BEM SUCEDIDA em falha no relatório. Aconteceu
            // ao acrescentar o aviso de registro curado, em 2026-08-05 — o mesmo acoplamento por texto
            // que este trecho já documentava como frágil.
            if (r.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
            {
                criados++;
                AnsiConsole.MarkupLine(
                    $"  [grey]{i + 1}/{lista.Count}[/] [green]criado[/] {c.Numero} "
                    + $"{(c.Nome ?? "").EscapeMarkup()} {(r.Length > 2 ? $"[yellow]{r[2..].EscapeMarkup()}[/]" : "")}");
            }
            else if (r == "já existe")
            {
                jaTinha++;
            }
            else
            {
                falhas.Add((c, r));
            }
        }

        return (criados, jaTinha, [.. falhas]);
    }

    // ── Envio ────────────────────────────────────────────────────────────────────────────────────

    private async Task EnviarAsync(string serial, CancellationToken ct)
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

        if (!await AparelhoPronto(ct))
        {
            return;
        }

        var plano = Sortear();

        // 🔴 O TETO CORTA, e não recusa mais. Antes, lista maior que o teto era um NÃO seco: "1000
        // contatos passam do teto de 30, suba o teto ou reduza a lista". Isso empurrava o operador
        // para as duas saídas ruins — subir o teto para 1000, que é o disparo em rajada que o teto
        // existe para impedir, ou recortar a lista à mão a cada execução. Com blocos e pausas o lote
        // se auto-regula, então o teto passa a ser a COTA da execução: manda os primeiros, e o resto
        // fica na lista, que é justamente o que faz o ciclo de vários dias funcionar sozinho.
        if (_teto > 0 && plano.Count > _teto)
        {
            AnsiConsole.MarkupLine(
                $"[grey]lista com[/] [bold]{plano.Count}[/][grey] contato(s); esta execução manda os "
                + $"primeiros[/] [bold]{_teto}[/] [grey](teto). o resto fica na lista para a próxima.[/]");
            plano = [.. plano.Take(_teto)];
        }

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
        AvisarFormaSuspeita(plano);

        // Quantas pausas cabem: uma a cada bloco fechado, e nenhuma depois da última mensagem. Com 30
        // mensagens em blocos de 15 são 2 blocos e UMA pausa, não duas.
        var blocos = _bloco > 0 ? (plano.Count + _bloco - 1) / _bloco : 1;
        var pausas = _bloco > 0 ? Math.Max(0, blocos - 1) : 0;
        var esperaEntreMsg = (plano.Count - 1 - pausas) * ((_min + _max) / 2.0);
        var estimativa = TimeSpan.FromSeconds(esperaEntreMsg + (pausas * _pausaMin * 60.0));

        AnsiConsole.MarkupLine(
            $"[bold]{plano.Count}[/] mensagem(ns), intervalo {_min}-{_max}s"
            + (_bloco > 0
                ? $", em ~[bold]{blocos}[/] bloco(s) de ~{_bloco} com pausa de ~{_pausaMin} min "
                  + $"({JanelaDescrita()})."
                : $", sem pausa entre blocos ({JanelaDescrita()})."));
        AnsiConsole.MarkupLine(
            $"[grey]~[/][bold]{Duracao(estimativa)}[/][grey] só de espera (o envio em si soma mais). "
            + $"término por volta das[/] [bold]{DateTime.Now.Add(estimativa):HH:mm}[/][grey].[/]");
        AnsiConsole.Markup("[bold]confirmar? digite[/] sim [bold]:[/] ");

        if (!string.Equals(Console.ReadLine()?.Trim(), "sim", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]cancelado, nada foi enviado.[/]");
            return;
        }

        var log = AbrirLog(serial);

        // 🔴 SEGURA O PC ACORDADO PELO LOTE INTEIRO. A pausa entre blocos passa ~30 min sem teclado nem
        // mouse, que é o gatilho do standby do Windows: o mesmo defeito do celular dormindo, do outro
        // lado do cabo. Ver PcAcordado — inclusive por que a TELA continua livre pra apagar.
        using var acordado = PcAcordado.Ligar();
        if (acordado.Ativo)
        {
            // Dito em voz alta porque a proteção é PARCIAL: fechar a tampa é política de energia do
            // Windows e nenhuma API de processo a sobrepõe. Deixar isso implícito faria a mensagem
            // parecer "pode fechar o notebook", que é justamente o que mata o lote.
            AnsiConsole.MarkupLine(
                "[grey]o PC não vai suspender enquanto o lote roda (a tela pode apagar, não atrapalha). "
                + "só não FECHE A TAMPA: isso suspende por política do Windows e nenhum programa impede.[/]");
        }

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
        // O disjuntor mora no Core, e não em variáveis soltas aqui, porque tem estado e casos de borda
        // (ver BatchStopPolicy) e este projeto não tem como ser testado. A primeira versão era feita à
        // mão neste laço e nasceu com um furo na sequência alternada.
        var disjuntor = new BatchStopPolicy(_pararEm);
        var entregues = new HashSet<string>(StringComparer.Ordinal);

        // 🔴 TENTADOS, ao lado de ENTREGUES. O de entregues só registra SUCESSO, então um número morto
        // não deixava rastro: o irmão dele (mesma pessoa, outra forma), mais adiante no mesmo plano,
        // tentava a forma B e depois a A na segunda chance — as duas já tentadas. Quatro aberturas de
        // conversa para um número que não existe, e abrir conversa atrás de conversa para números
        // inexistentes é o padrão de bot enumerando que o comentário da espera pós-falha diz evitar.
        // Quem falhou CONTINUA na lista pra ser tentado noutro lote; o que este conjunto impede é a
        // repetição dentro da MESMA execução.
        var tentados = new HashSet<string>(StringComparer.Ordinal);

        // Correções que a segunda chance descobrir, para repetir de uma vez no fim do lote.
        var corrigidos = new List<(string De, string Para)>();

        // Tentativas desde a última pausa longa, e o tamanho SORTEADO deste bloco.
        var noBloco = 0;
        var alvoBloco = ComJitter(_bloco, JitterBloco);

        // 🔴 TIRA DA LISTA E GRAVA EM DISCO A CADA ENTREGA, e não só no `finally`.
        //
        // O `finally` cobre exceção e Ctrl+C, que passam por ele. NÃO cobre queda de energia, reboot
        // forçado nem suspensão mal resolvida — e é justamente quando o lote roda a noite toda, sem
        // ninguém por perto, que esses são os desfechos prováveis. Nesses casos a lista voltava com os
        // já entregues DENTRO dela, prontos para receber de novo.
        //
        // O CSV é a rede de segurança e continua sendo, mas ele AVISA e deixa decidir (ver
        // AvisarRepeticao): quem não ler o aviso e não tirar com `x` manda a mesma campanha duas vezes
        // pra mesma pessoa, que é o pior desfecho possível com contato frio. Gravar na hora fecha a
        // janela em vez de depender de alguém ler.
        //
        // Custo: uma escrita de JSON por mensagem, ou seja uma a cada 150-360s. Irrelevante perto do
        // que ela evita. O `Salvar` já engole erro de disco, então isto não introduz caminho de falha.
        void Persistir()
        {
            _contatos.RemoveAll(c => entregues.Contains(c.Numero));
            Salvar(serial);
        }

        try
        {
            for (var i = 0; i < plano.Count; i++)
            {
                var (contato, variante, texto) = plano[i];

                // 🔴 JANELA ATIVA, conferida antes de CADA mensagem e não só na virada do bloco: um
                // bloco leva ~1h, então quem checasse só na entrada começaria às 21h50 e terminaria
                // depois das 23h. Encerra em vez de esperar amanhecer: console aberto de madrugada
                // depende da tela do celular acesa a noite toda, o que não se sustenta, e ninguém
                // estaria por perto para ver dar errado. O resto fica na lista.
                if (!DentroDaJanela())
                {
                    AnsiConsole.MarkupLine(
                        $"[blue]fora da janela de envio ({_horaInicio}h-{_horaFim}h): execução "
                        + $"encerrada.[/] [grey]{plano.Count - i} contato(s) ficam na lista para o "
                        + "próximo dia. abra com[/] janela 0 24 [grey]se quiser rodar em qualquer "
                        + "horário.[/]");
                    break;
                }

                // 🔴 A MESMA PESSOA NAS DUAS FORMAS. Uma lista de fontes misturadas traz o mesmo
                // contato com e sem o 9º dígito, e a dedup da colagem compara dígitos exatos, então os
                // dois entram como contatos diferentes. Sem esta guarda, o de 12 falha, a segunda
                // chance entrega pela forma de 13, e o contato de 13 — que está mais adiante no MESMO
                // plano — entrega de novo. A pessoa recebe a campanha duas vezes, que é das piores
                // coisas que se pode fazer com contato frio.
                // O plano é um retrato tirado antes do laço, então remover de `_contatos` não basta:
                // a checagem tem que ser aqui, contra o que já saiu NESTA execução.
                var irmao = BrazilPhoneValidator.AlternateBrazilianForm(contato.Numero);
                if (entregues.Contains(contato.Numero) || (irmao is not null && entregues.Contains(irmao)))
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]({i + 1}/{plano.Count}) pulado[/] {contato.Numero} "
                        + "[grey]— a mesma pessoa já recebeu neste lote, na outra forma do número.[/]");
                    entregues.Add(contato.Numero);   // sai da lista junto: é duplicata, não pendência
                    Persistir();
                    continue;
                }

                // Já tentei esta pessoa neste lote e não deu certo nas duas formas. Repetir agora só
                // gastaria sinal de bot; ela fica na lista pra uma execução futura.
                if (tentados.Contains(contato.Numero) || (irmao is not null && tentados.Contains(irmao)))
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]({i + 1}/{plano.Count}) pulado[/] {contato.Numero} "
                        + "[grey]— a mesma pessoa já foi tentada neste lote, nas duas formas do número.[/]");
                    continue;
                }

                if (_agenda)
                {
                    var saved = await phone.SaveContactAsync(contato.Numero, contato.Nome, ct);
                    AnsiConsole.MarkupLine($"[grey]agenda[/] {contato.Numero}: {saved.EscapeMarkup()}");
                }

                tentados.Add(contato.Numero);
                // Conta TENTATIVA, não entrega: o que desenha padrão para o WhatsApp é a conversa
                // aberta, e ela é aberta mesmo quando o envio falha. Contato pulado por duplicata não
                // chega aqui, e é certo que não conte: nada foi aberto por ele.
                noBloco++;
                var r = await phone.SendWhatsAppMessageAsync(contato.Numero, texto, ct);

                // 🔴 SEGUNDA CHANCE COM A OUTRA FORMA DO NÚMERO. O WhatsApp guarda a conta ora com o
                // 9º dígito, ora sem, conforme a época do registro, e abrir a conversa pela forma
                // errada faz o app responder "não tem WhatsApp": um contato BOM parece morto. Medido
                // em 2026-08-05, no mesmo DDD 84 — um número de 12 dígitos entregou e outro falhou.
                //
                // Aqui e não na entrada: converter a lista toda pra 13 dígitos quebraria justamente os
                // que funcionam em 12, e não há como saber qual forma a conta usa sem tentar.
                //
                // UMA tentativa a mais, nunca um laço: duas formas é diagnóstico, N formas é
                // enumeração de números — o padrão que se quer evitar. Se as duas falharem, o número
                // é morto de verdade.
                // 🔴 SÓ COM FALHA CONCLUSIVA. `Uncertain` significa que o toque em enviar já aconteceu e
                // não deu pra confirmar o resultado — a mensagem PODE ter saído. Reabrir a conversa na
                // outra forma do número nesse estado entrega a campanha DUAS VEZES para a mesma pessoa,
                // que é o pior desfecho possível com contato frio.
                // Antes isto não tinha como ser distinguido: o `Sent` era bool e "não saiu" chegava aqui
                // igual a "não sei se saiu".
                var numeroUsado = contato.Numero;
                if (!r.Sent && !r.Uncertain
                    && BrazilPhoneValidator.AlternateBrazilianForm(contato.Numero) is { } alternativo)
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]tentando a outra forma do número ({alternativo})…[/]");
                    await Task.Delay(
                        TimeSpan.FromSeconds(Random.Shared.Next(FalhaEsperaMin, FalhaEsperaMax + 1)), ct);
                    tentados.Add(alternativo);
                    var r2 = await phone.SendWhatsAppMessageAsync(alternativo, texto, ct);
                    if (r2.Sent)
                    {
                        r = r2;
                        numeroUsado = alternativo;
                        corrigidos.Add((contato.Numero, alternativo));
                        AnsiConsole.MarkupLine(
                            $"[yellow]a lista tem esse contato na forma errada:[/] {contato.Numero} "
                            + $"[yellow]→[/] [bold]{alternativo}[/] [grey](corrija na origem)[/]");

                        // 🔴 A agenda ficou com o número ERRADO, gravado logo antes da primeira
                        // tentativa. Isso não é só informação faltando: o WhatsApp sincroniza a agenda,
                        // não acha conta pra aquele número e passa a responder "não tem WhatsApp" toda
                        // vez, mesmo para a pessoa que existe. Gravar aqui a forma que REALMENTE recebeu
                        // é o que impede a agenda de continuar envenenando as próximas tentativas.
                        // O errado continua lá (o adb não apaga contato por aqui), mas ao lado do certo.
                        if (_agenda)
                        {
                            var corrigido = await phone.SaveContactAsync(alternativo, contato.Nome, ct);
                            AnsiConsole.MarkupLine(
                                $"[grey]agenda[/] {alternativo}: {corrigido.EscapeMarkup()} "
                                + "[grey](a forma que funciona)[/]");
                        }
                    }
                }

                if (r.Sent)
                {
                    enviados++;
                    disjuntor.Delivered();
                    entregues.Add(contato.Numero);
                    // A forma que REALMENTE recebeu também entra, senão o irmão dela (mesma pessoa,
                    // outro formato) passaria pela guarda lá em cima e receberia de novo.
                    entregues.Add(numeroUsado);
                    // Em disco AGORA: daqui até o fim do lote podem passar horas, e a mensagem já saiu.
                    Persistir();
                    AnsiConsole.MarkupLine(
                        $"[green]({i + 1}/{plano.Count}) enviado[/] {numeroUsado} tpl {variante} "
                        + $"(entrega: {r.DeliveryStatus ?? "?"})");
                }
                else if (r.Uncertain)
                {
                    // NÃO conta como enviado nem sai da lista: não há o que afirmar. Mas também não é
                    // uma falha comum, e chamá-la assim faria o operador reenviar achando que nada saiu.
                    falhas++;
                    // Conta como falha de APARELHO: "toquei enviar e não consegui confirmar" fala da
                    // leitura de tela, não do número, e repetido é sinal de aparelho ruim.
                    disjuntor.DeviceFailure();
                    AnsiConsole.MarkupLine(
                        $"[yellow]({i + 1}/{plano.Count}) NÃO CONFIRMADO[/] {contato.Numero}: "
                        + $"{(r.Error ?? "").EscapeMarkup()}");
                    AnsiConsole.MarkupLine(
                        "[yellow]  confira esta conversa no aparelho antes de mandar de novo.[/] "
                        + "[grey]fica na lista, e o log guarda como \"incerto\" pra avisar no próximo lote.[/]");
                }
                else if (r.NoWhatsAppAccount)
                {
                    // 🔴 CONTA À PARTE das falhas de aparelho. O app afirmou que ESTE número não tem
                    // conta, o que não prevê nada sobre o próximo contato: não pode alimentar o aviso
                    // que manda conferir a tela do celular. Linha própria também na tela, porque
                    // "falhou" e "sem conta" mandam o operador para lugares diferentes.
                    falhas++;
                    disjuntor.NoAccount();
                    AnsiConsole.MarkupLine(
                        $"[red]({i + 1}/{plano.Count}) sem conta[/] {contato.Numero}: "
                        + $"{(r.Error ?? "").EscapeMarkup()}");
                }
                else
                {
                    falhas++;
                    disjuntor.DeviceFailure();
                    AnsiConsole.MarkupLine(
                        $"[red]({i + 1}/{plano.Count}) falhou[/] {contato.Numero}: {(r.Error ?? "").EscapeMarkup()}");
                }
                // 🔴 REGISTRA QUEM RECEBEU, não quem estava na lista. Quando a segunda chance entrega
                // pela outra forma do número, `contato.Numero` é justamente a forma que FALHOU: o CSV
                // gravava "enviado=sim" para um número que não recebeu nada, e quem recebeu não
                // aparecia em lugar nenhum.
                // Não é só auditoria errada. O CSV é a ÚNICA memória entre execuções (ver
                // NumerosJaEnviados), então a pessoa colada amanhã na forma certa passava sem aviso e
                // podia receber a campanha de novo. A dedup DENTRO do lote já sabia que as duas formas
                // são a mesma pessoa; a de fora do lote não ficava sabendo.
                Registrar(log, serial, contato with { Numero = numeroUsado }, variante, texto, r);

                // DEPOIS do registro em disco, de propósito: o CSV é o que sobrevive a tudo, o bip é
                // conforto. Se a máquina morrer entre os dois, o que se perde é o som.
                // "Incerto" bipa como FALHA porque é o resultado que pede uma pessoa: alguém precisa
                // abrir a conversa no aparelho e conferir. Bipar como sucesso esconderia justamente o
                // caso que não pode ser resolvido sozinho.
                if (_bip)
                {
                    await BiparAsync(r.Sent);
                }

                // 🔴 AVISA, NÃO TRAVA. Antes, três falhas seguidas interrompiam o lote. Medido em
                // 2026-08-07: três números na forma errada mataram uma execução em 22/30 com o aparelho
                // perfeito, e 15 contatos bons ficaram sem receber. Se falhou, nada saiu — seguir para
                // o próximo não custa entrega nenhuma, e quem falhou continua na lista.
                //
                // O aviso fica porque falha de APARELHO é a única que prevê o próximo contato: tela
                // bloqueada continua bloqueada. Sem ele, um celular travado no meio do lote só seria
                // descoberto no fim, com trinta linhas vermelhas e nenhuma entrega.
                // 🔴 LISTA SENDO RECUSADA EM SEQUÊNCIA. Um número negado é rotina e não prevê nada
                // sobre o próximo. Três seguidos preveem: a causa provável deixou de ser "esses números
                // morreram" e passou a ser comum aos três. Sem este aviso o lote seguia até o fim
                // abrindo conversa para cada um, que é a enumeração que o disjuntor existe pra evitar —
                // 87 contatos viram até 174 aberturas sem UMA entrega.
                if (disjuntor.AcabouDeAcusarLista)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]atenção: {disjuntor.ConsecutiveNoAccount} números seguidos recusados "
                        + "como \"sem conta\".[/] [grey]isso raramente é coincidência. confira, nesta "
                        + "ordem: 1. abra o WhatsApp NO CELULAR e procure um desses contatos — se ele "
                        + "existe lá, o problema não é a lista. 2. veja a tela salva que o erro aponta, "
                        + "pra saber o que o app mostrou de verdade. 3. lista de origem duvidosa? o lote "
                        + "continua, mas cada recusa abre uma conversa à toa —[/] Ctrl+C [grey]interrompe, "
                        + "e[/] parar 5 [grey]faz o lote parar sozinho da próxima vez.[/]");
                }

                if (disjuntor.AcabouDeAcusarAparelho)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]atenção: {disjuntor.ConsecutiveDeviceFailures} falhas de APARELHO "
                        + "seguidas.[/] [grey]isso quase nunca é o contato. confira a tela (desbloqueada? "
                        + "WhatsApp aberto? cabo firme?). o lote continua, mas se o celular estiver "
                        + "travado o resto vai falhar igual —[/] Ctrl+C [grey]interrompe.[/]");
                }

                if (disjuntor.ShouldStop)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]{disjuntor.ConsecutiveFailures} falhas seguidas: lote interrompido.[/] "
                        + "[grey]este teto é o que VOCÊ pediu em[/] parar[grey]; volte a[/] parar 0 "
                        + "[grey]para nunca interromper por falha.[/]");
                    break;
                }

                // 🔴 PAUSA LONGA ENTRE BLOCOS, e ela SUBSTITUI a espera normal, não soma. O que pesa
                // contra o chip é padrão: fluxo contínuo, hora após hora, no mesmo intervalo, é
                // assinatura de máquina. Gente manda um punhado, some, volta depois.
                if (_bloco > 0 && noBloco >= alvoBloco && i < plano.Count - 1)
                {
                    await PausarAsync(ComJitter(_pausaMin, JitterPausa), noBloco, plano.Count - (i + 1), ct);
                    noBloco = 0;
                    alvoBloco = ComJitter(_bloco, JitterBloco);
                }
                else if (i < plano.Count - 1)
                {
                    // 🔴 FALHA NÃO É ENVIO. Nada saiu, então esperar os 150-360s do ritmo normal é
                    // pagar o preço do anti-ban por uma mensagem que não existiu: um lote com cinco
                    // números mortos custava meia hora só de espera por nada.
                    // Mas NÃO é zero. Abrir conversa atrás de conversa para números que não existem é
                    // exatamente o padrão de um bot enumerando números, e isso é sinal forte de ban.
                    // 8-21s é o mesmo intervalo que o DispatchEngine já usa para operações que NÃO
                    // enviam (o check-exists); reusado aqui de propósito, em vez de inventar um número.
                    //
                    // 🔴 A RAJADA é o que pesa, não a falha isolada. Enquanto o lote parava em 3 falhas,
                    // a espera curta valia sempre, porque a sequência nunca passava disso. Agora que
                    // falha não interrompe mais nada, uma lista com 20 números mortos abriria 20
                    // conversas de 9 em 9 segundos, que é o desenho de enumeração que essa mesma espera
                    // dizia evitar. Falha isolada segue rápida; virando SEQUÊNCIA, volta ao ritmo
                    // normal, que espaça as aberturas. Com a parada fora, o ritmo é a proteção que
                    // sobrou, e por isso ele deixou de ser opcional.
                    var emSequencia = disjuntor.InFailureStreak;
                    var (min, max) = r.Sent || emSequencia ? (_min, _max) : (FalhaEsperaMin, FalhaEsperaMax);
                    var espera = Random.Shared.Next(min, max + 1);
                    AnsiConsole.MarkupLine(
                        r.Sent
                            ? $"[grey]aguardando {espera}s antes do próximo…  (Ctrl+C interrompe)[/]"
                            : emSequencia
                                ? $"[grey]{disjuntor.ConsecutiveFailures} falhas seguidas: voltando ao "
                                  + $"ritmo normal, {espera}s, para não abrir conversas em rajada…  "
                                  + "(Ctrl+C interrompe)[/]"
                                : $"[grey]nada foi enviado, então só {espera}s antes do próximo…  (Ctrl+C interrompe)[/]");
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
            // Hoje o `Persistir` já gravou a cada entrega, então isto virou REDE DE SEGURANÇA e não o
            // caminho principal. Fica: é barato, e cobre qualquer saída que não passe por uma entrega
            // (lista que acabou, janela de envio fechada, teto atingido).
            Persistir();
            if (entregues.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]{entregues.Count} contato(s) que receberam saíram da lista. "
                    + $"restam {_contatos.Count} (os que falharam continuam, para tentar de novo).[/]");
            }

            // 🔴 REPETE NO FIM o que já foi dito na hora. Entre uma entrega e a seguinte passam 150-360s
            // de espera, então a linha "corrija na origem" sai da tela muito antes de o lote acabar — e
            // ela é justamente a única que gera trabalho FORA daqui. Sem esta lista, a correção depende
            // de o operador ter visto passar, e a mesma lista volta amanhã com o mesmo número errado,
            // pagando de novo a tentativa perdida.
            if (corrigidos.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{corrigidos.Count} contato(s) só entregaram na OUTRA forma do número. "
                    + "corrija na origem:[/]");
                MostrarAlguns(corrigidos.Count, 10,
                    i => $"  {corrigidos[i].De} [yellow]→[/] [bold]{corrigidos[i].Para}[/]");
            }
        }

        return (enviados, falhas);
    }

    /// <summary>Avisa quem desta lista já recebeu deste aparelho. O log é a única memória entre
    /// execuções. Avisa e deixa decidir, em vez de pular calado: reenviar às vezes é intencional.</summary>
    /// <remarks>
    /// 🔴 Confere as DUAS FORMAS do número, igual à dedup dentro do lote (ver DispararAsync). Com
    /// comparação de dígitos exatos conviviam duas noções de "mesmo contato" no mesmo arquivo, e a mais
    /// fraca era justamente a que guarda o reenvio ENTRE sessões: quem recebeu ontem em 12 dígitos e é
    /// colado hoje em 13 não era reconhecido, e o aviso que existe pra impedir a segunda mensagem não
    /// aparecia.
    /// </remarks>
    private void AvisarRepeticao(List<(Contato Contato, int Variante, string Texto)> plano, string serial)
    {
        var jaReceberam = NumerosJaEnviados(serial);
        var repetidos = plano
            .Where(p => jaReceberam.Contains(p.Contato.Numero)
                || (BrazilPhoneValidator.AlternateBrazilianForm(p.Contato.Numero) is { } outra
                    && jaReceberam.Contains(outra)))
            .ToList();
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

    /// <summary>A faixa que o <see cref="ComJitter"/> pode sortear, como "12-18". Deriva dos MESMOS
    /// limites do sorteio: texto escrito à mão divergiria do código no primeiro ajuste da folga, e aí a
    /// tela estaria mentindo sobre o que o sistema faz.</summary>
    private static string FaixaJitter(int centro, double folga) =>
        centro <= 0
            ? "0"
            : $"{Math.Max(1, (int)Math.Floor(centro * (1 - folga)))}-"
              + $"{Math.Max(1, (int)Math.Ceiling(centro * (1 + folga)))}";

    /// <summary>Bip de acompanhamento: agudo curto quando saiu, dois graves quando não saiu.</summary>
    /// <remarks>
    /// 🔴 NUNCA PROPAGA. Um lote de horas não pode morrer porque a máquina não tem alto-falante, ou
    /// porque a saída foi redirecionada pra arquivo. O bip é conforto, a mensagem é o trabalho.
    /// <para>`Console.Beep(freq, ms)` só existe no Windows; fora dele resta o `\a`, que o terminal
    /// decide se toca. A guarda de plataforma é obrigatória: sem ela o analisador (CA1416) reprova a
    /// build, e com razão, porque a chamada estouraria em Linux.</para>
    /// <para>Bloqueia pela duração do tom, e é por isso que ele é curto: 120ms uma vez a cada 150-360s
    /// não desloca o ritmo, mas um tom longo entraria na conta do intervalo entre mensagens.</para>
    /// </remarks>
    private static async Task BiparAsync(bool saiu)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Write('\a');
                return;
            }
            if (saiu)
            {
                Console.Beep(880, 120);
                return;
            }
            // Dois graves: distingue de longe, sem depender de a pessoa lembrar de um tom só.
            Console.Beep(330, 180);
            // `await` e não `Thread.Sleep`: o intervalo entre os dois tons não tem por que segurar a
            // thread. `Console.Beep` em si já bloqueia (é a API do Windows), e não há o que fazer
            // quanto a isso — mas somar uma espera SÍNCRONA a ela seria escolher bloquear de graça,
            // dentro de um método assíncrono, num laço que espera cancelamento por Ctrl+C.
            await Task.Delay(90);
            Console.Beep(330, 180);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException
                                      or ArgumentOutOfRangeException)
        {
            // Sem som disponível: segue o lote em silêncio. A tela e o CSV continuam contando tudo.
        }
    }

    /// <summary>Sorteia em torno de um centro, com a folga percentual dada. Nunca devolve menos de 1.</summary>
    private static int ComJitter(int centro, double folga) =>
        centro <= 0
            ? centro
            : Math.Max(1, Random.Shared.Next(
                (int)Math.Floor(centro * (1 - folga)),
                (int)Math.Ceiling(centro * (1 + folga)) + 1));

    /// <summary>Agora está dentro da janela em que é permitido mandar mensagem?</summary>
    private bool DentroDaJanela()
    {
        var h = DateTime.Now.Hour;
        return h >= _horaInicio && h < _horaFim;
    }

    /// <summary>A janela em uma linha, para o menu e o pré-voo. 0h-24h não é horário, é "sem
    /// restrição", e mostrar "0h-24h" faria o operador procurar uma limitação que não existe.</summary>
    private string TetoDescrito() =>
        _teto == 0 ? "sem teto" : _teto.ToString(CultureInfo.InvariantCulture);

    private string JanelaDescrita() =>
        _horaInicio == 0 && _horaFim == 24 ? "qualquer horário" : $"{_horaInicio}h-{_horaFim}h";

    /// <summary>Pausa entre blocos, com o horário de volta e um cronômetro na tela.</summary>
    /// <remarks>
    /// 🔴 O CRONÔMETRO NÃO É ENFEITE. Uma pausa de 30 minutos sem nada na tela é indistinguível de um
    /// console travado, e a reação natural é fechar a janela — que é justamente o que faz perder o
    /// lote. O horário de volta serve para quem sai de perto: dá para conferir o relógio da parede em
    /// vez de ficar olhando o terminal.
    ///
    /// <para>Contagem sem markup e reescrita na MESMA linha com <c>\r</c>: uma tag do Spectre aberta
    /// numa linha parcial embaralha a saída seguinte. E a linha é limpa com espaços no fim, senão o
    /// resto do texto anterior fica pendurado quando o contador encurta.</para>
    /// </remarks>
    private static async Task PausarAsync(int minutos, int noBloco, int restantes, CancellationToken ct)
    {
        var volta = DateTime.Now.AddMinutes(minutos);
        AnsiConsole.MarkupLine(
            $"[blue]bloco de {noBloco} concluído.[/] [grey]pausa de {minutos} min. volta às[/] "
            + $"[bold]{volta:HH:mm}[/][grey], com {restantes} contato(s) pela frente.[/]");

        var fim = DateTime.UtcNow.AddMinutes(minutos);
        for (var falta = fim - DateTime.UtcNow; falta > TimeSpan.Zero; falta = fim - DateTime.UtcNow)
        {
            Console.Write($"\r   retomando em {falta:hh\\:mm\\:ss}  (Ctrl+C interrompe)   ");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        Console.Write("\r" + new string(' ', 48) + "\r");
        AnsiConsole.MarkupLine("[blue]voltando.[/]");
    }

    /// <summary>Avisa, ANTES do lote começar, quais números não têm forma de celular.</summary>
    /// <remarks>
    /// 🔴 Mira exatamente o que a SEGUNDA CHANCE não alcança. Para 10 dígitos nacionais com assinante
    /// em 2-5, o <see cref="BrazilPhoneValidator.AlternateBrazilianForm"/> devolve null: não existe
    /// outra forma plausível, então não há o que tentar depois da falha e ela é definitiva. O caso que
    /// TEM conserto (celular legado, assinante 6-9) já se resolve sozinho durante o envio, sem avisar
    /// ninguém — e é por isso que o alarme aqui é sobre o outro.
    ///
    /// <para>A tentativa perdida não custa só tempo: abrir conversa para número que não existe é o
    /// padrão de bot enumerando, e três falhas seguidas derrubam o lote inteiro pelo disjuntor. Uma
    /// lista com quatro números assim pode parar tudo antes da metade.</para>
    ///
    /// <para>O comando `conferir` já sabia disso desde 2026-08-06, mas é opt-in: exige lembrar de
    /// rodar antes. Aqui a mesma informação passa a estar no caminho de quem vai disparar, ao lado do
    /// <see cref="AvisarRepeticao"/>, que já segue esse padrão de avisar e deixar decidir.</para>
    ///
    /// <para>AVISA, não bloqueia: linha fixa pode ter WhatsApp Business, então assinante em 2-5 é
    /// suspeita forte e não veredito. Bloquear transformaria um aviso útil num impedimento errado.</para>
    ///
    /// <para>Usa o <see cref="DescreverForma"/> do `conferir` de propósito: dois vocabulários para a
    /// mesma classificação fariam o aviso e a tabela parecerem discordar.</para>
    /// </remarks>
    private static void AvisarFormaSuspeita(List<(Contato Contato, int Variante, string Texto)> plano)
    {
        var suspeitos = plano
            .Where(p => BrazilPhoneValidator.ShapeOf(p.Contato.Numero)
                is BrazilPhoneValidator.BrazilNumberShape.FixoOuSemONono
                or BrazilPhoneValidator.BrazilNumberShape.NaoBrasileiro)
            .ToList();
        if (suspeitos.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]atenção: {suspeitos.Count} número(s) desta lista não têm forma de celular, "
            + "e para eles NÃO existe segunda chance.[/]");
        MostrarAlguns(suspeitos.Count, 5,
            i => $"  [yellow]{suspeitos[i].Contato.Numero}[/] "
                + $"{(suspeitos[i].Contato.Nome ?? "").EscapeMarkup()} "
                + DescreverForma(BrazilPhoneValidator.ShapeOf(suspeitos[i].Contato.Numero)));
        AnsiConsole.MarkupLine(
            "[yellow]assinante começando em 2-5 é faixa de fixo. se a pessoa tem celular, o que falta é "
            + "o 9º dígito, e ele precisa vir corrigido da ORIGEM.[/] "
            + "[grey]veja a lista toda com[/] c[grey], tire da lista com[/] x[grey].[/]");
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
        if (partes.Length < 2 || !int.TryParse(partes[1], out var n) || n < 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]uso:[/] teto <n>   (atual: {(_teto == 0 ? "sem teto" : _teto.ToString(CultureInfo.InvariantCulture))})");
            AnsiConsole.MarkupLine(
                "[grey]quantas mandar NESTA execução; o resto fica na lista.[/] 0 [grey]= sem teto, "
                + "manda a lista inteira.[/]");
            return;
        }
        _teto = n;
        AnsiConsole.MarkupLine(
            _teto == 0
                ? "teto: [bold]sem teto[/] [grey](manda a lista inteira)[/]."
                : $"cota por execução: [bold]{_teto}[/] [grey](o resto fica na lista)[/].");
    }

    /// <summary>Tamanho do bloco e duração da pausa, num comando só.</summary>
    /// <remarks>
    /// Os dois juntos porque um sem o outro não quer dizer nada: bloco sem pausa é o fluxo contínuo de
    /// antes, e pausa sem bloco não tem quando acontecer. Separar em dois comandos deixaria o console
    /// passar por estados que não fazem sentido entre um ajuste e o outro.
    /// </remarks>
    /// <summary>Digitado com argumentos, aplica; digitado sozinho (ou pelo menu), pergunta.</summary>
    private void BlocosOuInterativo(string[] partes, string serial)
    {
        if (partes.Length >= 3)
        {
            Blocos(partes);
            Salvar(serial);
            return;
        }

        AnsiConsole.MarkupLine(
            $"[grey]hoje: {(_bloco == 0 ? "sem blocos" : $"{_bloco} mensagens e pausa de {_pausaMin} min")}."
            + " 0 mensagens desliga a pausa.[/]");
        AnsiConsole.Markup("[grey]mensagens por bloco (Enter mantém):[/] ");
        var qtd = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(qtd))
        {
            AnsiConsole.MarkupLine("[grey]mantido.[/]");
            return;
        }
        AnsiConsole.Markup("[grey]minutos de pausa entre blocos:[/] ");
        var min = Console.ReadLine();
        Blocos(["blocos", qtd.Trim(), (min ?? "").Trim()]);
        Salvar(serial);
    }

    private void Blocos(string[] partes)
    {
        var atual = _bloco == 0
            ? "sem blocos (fluxo contínuo)"
            : $"{_bloco} mensagens, pausa de {_pausaMin} min";
        if (partes.Length < 3
            || !int.TryParse(partes[1], out var n) || n < 0 || n > 200
            || !int.TryParse(partes[2], out var min) || min < 1 || min > 720)
        {
            AnsiConsole.MarkupLine($"[red]uso:[/] blocos <mensagens> <minutos de pausa>   (atual: {atual})");
            AnsiConsole.MarkupLine(
                "[grey]exemplo:[/] blocos 15 30 [grey]manda um punhado de ~15, para ~30 min, e repete.[/] "
                + "blocos 0 30 [grey]desliga a pausa e volta ao fluxo contínuo.[/]");
            AnsiConsole.MarkupLine(
                "[grey]os dois números são o CENTRO, não o valor exato: cada bloco sorteia em volta "
                + "(12-18 mensagens, 22-38 min). repetir 15 e 30 cravados seria o único trecho "
                + "regular da série, que é a assinatura que a pausa existe para desmanchar.[/]");
            return;
        }
        (_bloco, _pausaMin) = (n, min);
        // 🔴 A FAIXA SORTEADA NO RAMO DE SUCESSO, e não só no de erro. Ela estava explicada apenas na
        // mensagem de uso, ou seja, só via quem errava o comando. Quem acertava configurava 15 e 30, via
        // sair 14 e 29, e concluía que o sistema não obedece — relatado operando em 2026-08-10. Número
        // sorteado sem a faixa à vista não parece sorteio, parece defeito.
        AnsiConsole.MarkupLine(
            _bloco == 0
                ? "blocos: [bold]desligados[/] [grey](fluxo contínuo)[/]."
                : $"blocos de [bold]{_bloco}[/] mensagem(ns), pausa de [bold]{_pausaMin}[/] min entre eles. "
                  + $"[grey]cada bloco sorteia em volta disso: {FaixaJitter(_bloco, JitterBloco)} "
                  + $"mensagens e {FaixaJitter(_pausaMin, JitterPausa)} min. repetir os números cravados "
                  + "seria o único trecho regular da série, que é o padrão que a pausa desmancha.[/]");
    }

    private void Janela(string[] partes)
    {
        if (partes.Length < 3
            || !int.TryParse(partes[1], out var ini) || !int.TryParse(partes[2], out var fim)
            || ini < 0 || fim > 24 || ini >= fim)
        {
            AnsiConsole.MarkupLine(
                $"[red]uso:[/] janela <hora inicial> <hora final>   (atual: {_horaInicio}h-{_horaFim}h)");
            // A janela que cruza a meia-noite é recusada de propósito, e não "corrigida" no chute: o
            // HumanPhaseAutoSender já registra que uma janela invertida deixa o robô mudo em silêncio,
            // sem nenhuma pista de por quê. Aqui ela deixaria o lote encerrando na primeira mensagem.
            AnsiConsole.MarkupLine(
                "[grey]o início tem que ser ANTES do fim: a janela não atravessa a meia-noite. "
                + "exemplo:[/] janela 8 22[grey].[/]");
            return;
        }
        (_horaInicio, _horaFim) = (ini, fim);
        AnsiConsole.MarkupLine($"janela de envio: [bold]{JanelaDescrita()}[/].");
    }

    /// <summary>Quantas falhas seguidas interrompem o lote. Zero (padrão) = nunca interrompe.</summary>
    /// <remarks>
    /// 🔴 NÃO entra no menu principal, e é exceção consciente à regra de que todo comando novo entra
    /// lá. O menu é a superfície do dia a dia; este ajuste só interessa no minuto em que o lote para
    /// por esse motivo, e é justamente ali que a mensagem de interrupção o oferece, com o valor atual.
    /// Descoberta no ponto de uso, que é o que a regra do menu persegue. Continua listado em
    /// <c>comandos</c>, que é a referência completa.
    ///
    /// <para>Aceita no máximo 30 porque o número existe para limitar: um valor gigante é o mesmo que
    /// deixar em zero, só que com aparência de proteção ligada.</para>
    /// </remarks>
    private void Parar(string[] partes)
    {
        var atual = _pararEm == 0 ? "nunca" : _pararEm.ToString(CultureInfo.InvariantCulture);
        if (partes.Length < 2 || !int.TryParse(partes[1], out var n) || n < 0 || n > 30)
        {
            AnsiConsole.MarkupLine($"[red]uso:[/] parar <0-30>   (atual: {atual})");
            AnsiConsole.MarkupLine(
                "[grey]quantas falhas seguidas interrompem o lote.[/] 0 [grey]= nunca interrompe, que é "
                + "o padrão: se falhou, nada saiu, e o contato que falhou continua na lista.[/]");
            return;
        }
        _pararEm = n;
        AnsiConsole.MarkupLine(
            _pararEm == 0
                ? "falhas seguidas: [bold]nunca interrompem o lote[/]."
                : $"falhas seguidas que interrompem o lote: [bold]{_pararEm}[/].");
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
    /// <para>🔴 TODO COMANDO NOVO ENTRA AQUI. O `conferir` nasceu só na ajuda de abertura e na lista do
    /// `comandos`, e o operador perguntou "c serve pra quê? não tem nada explicando" — com razão: a
    /// ajuda rola pra fora da tela e o `comandos` exige saber que ele existe. ESTE painel é o único
    /// que fica visível o tempo todo, então um comando que não está nele, na prática, não existe.</para>
    /// </remarks>
    private void Menu(string serial)
    {
        var t = new Table().Border(TableBorder.Rounded).HideHeaders();
        t.AddColumn(new TableColumn("n").NoWrap());
        t.AddColumn(new TableColumn("o que é"));
        t.AddColumn(new TableColumn("agora"));

        t.AddRow("[bold]1[/]", "ritmo entre mensagens", $"[bold]{_min}-{_max}s[/]");
        t.AddRow("[bold]2[/]", "cota por execução", $"[bold]{TetoDescrito()}[/]");
        t.AddRow("[bold]b[/]", "blocos, pausa e janela",
            (_bloco == 0
                ? "[grey]fluxo contínuo[/]"
                : $"~[bold]{_bloco}[/] por vez, pausa ~[bold]{_pausaMin}[/] min")
            + $" [grey]· {JanelaDescrita()}[/]");
        t.AddRow("[bold]3[/]", "gravar na agenda", _agenda ? "[bold]ligado[/]" : "[grey]desligado[/]");
        t.AddRow("[bold]d[/]", "digitação humana",
            _digitacaoHumana
                ? "[bold]ligada[/] [grey](só ASCII)[/]"
                : "[grey]desligada[/] [green](aceita acento)[/]");
        t.AddRow("[bold]bip[/]", "aviso sonoro por mensagem",
            _bip ? "[bold]ligado[/] [grey](acompanha de ouvido)[/]" : "[grey]desligado[/]");
        t.AddRow("[bold]4[/]", "contatos",
            _contatos.Count == 0 ? "[grey]vazio[/]" : $"[bold]{_contatos.Count}[/] na lista");
        t.AddRow("[bold]5[/]", "templates",
            _textos.Count == 0 ? "[grey]vazio[/]" : $"[bold]{_textos.Count}[/] template(s)");
        t.AddEmptyRow();
        // Letras porque os dígitos acabaram, e renumerar 1..9 quebraria a memória de quem já usa.
        t.AddRow("[bold]g[/]", "gravar", "[grey]grava a lista na agenda do aparelho, sem enviar[/]");
        t.AddEmptyRow();
        t.AddRow("[bold]6[/]", "ver", "[grey]confere o que está carregado[/]");
        t.AddRow("[bold]c[/]", "conferir", "[grey]a forma de cada número: celular, legado ou fixo[/]");
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
        AnsiConsole.Markup(
            $"[grey]cota atual: {TetoDescrito()}. quantas mandar por execução, 0 = a lista toda "
            + "(Enter mantém):[/] ");
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
            ("7  previa", "quem recebe qual texto, sem tocar no aparelho"),
            ("c", "a forma de cada número: celular, legado ou fixo"),
            ("atencao", "o que vale nao e o total de digitos"),
            ("regra", "o digito depois do DDD tem que ser 6 a 9"));

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
        t.AddRow("gravar", "grava a lista na agenda do aparelho, SEM enviar nada");
        t.AddRow("contatos [grey]| contatos +[/]", "cola a lista (substitui | soma). formato: numero ou numero;nome");
        t.AddRow("textos [grey]| textos +[/]", $"cola os templates (substitui | soma). {TokenNome} vira o nome do contato");
        t.AddRow("ver", "mostra a lista, os templates e os ajustes atuais");
        t.AddRow("previa", "simula quem receberia qual template, sem tocar no aparelho");
        t.AddRow("conferir [grey]| c[/]", "classifica cada número: celular, legado, fixo ou faltando o 9º dígito");
        t.AddRow("enviar", "pré-voo, plano, confirmação e disparo do lote");
        t.AddRow("status", "reconsulta o aparelho pelo adb");
        t.AddRow("intervalo <min> <max>", "segundos entre um envio e o próximo (default 150 360)");
        t.AddRow("blocos <n> <min> [grey]| b[/]", "manda ~n, pausa ~min minutos, repete (default 15 e 30; n=0 desliga)");
        t.AddRow("janela <ini> <fim>", "horas em que é permitido enviar (default 8 22)");
        t.AddRow("teto <n>", "cota desta execução: manda os n primeiros e deixa o resto na lista");
        t.AddRow("parar <n>", "falhas seguidas que interrompem o lote (default 0 = nunca interrompe)");
        t.AddRow("agenda", "liga/desliga gravar o contato na agenda antes de enviar");
        t.AddRow("bip [grey]| som[/]", "liga/desliga o aviso sonoro (agudo = saiu, dois graves = não saiu)");
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
            if (e.Teto is { } teto)
            {
                _teto = Math.Max(0, teto);
            }
            // Sem guarda de zero aqui, ao contrário do teto: zero é um valor LEGÍTIMO deste ajuste
            // ("nunca interrompa") e também o padrão, então sessão antiga e escolha explícita levam ao
            // mesmo lugar.
            _pararEm = Math.Clamp(e.PararEm, 0, 30);
            if (e.Bip is { } bip)
            {
                _bip = bip;
            }
            if (e.Bloco is { } b)
            {
                _bloco = Math.Clamp(b, 0, 200);
            }
            if (e.PausaMin is { } p)
            {
                _pausaMin = Math.Clamp(p, 1, 720);
            }
            // Só aceita o par se ele for coerente. Janela invertida gravada por um bug antigo deixaria
            // o console MUDO para sempre, e é o defeito que o HumanPhaseAutoSender já documenta.
            if (e.HoraInicio is { } hi && e.HoraFim is { } hf && hi >= 0 && hf <= 24 && hi < hf)
            {
                (_horaInicio, _horaFim) = (hi, hf);
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
                PararEm = _pararEm,
                Bloco = _bloco,
                PausaMin = _pausaMin,
                HoraInicio = _horaInicio,
                HoraFim = _horaFim,
                Agenda = _agenda,
                DigitacaoHumana = _digitacaoHumana,
                Bip = _bip,
            };
            // 🔴 ESCREVE NUM TEMPORÁRIO E TROCA, em vez de sobrescrever direto.
            //
            // Um `WriteAllText` TRUNCA o arquivo antes de escrever, então morrer no meio deixa um JSON
            // pela metade — e o `Carregar` trata JSON quebrado começando VAZIO ("Estado é conveniência,
            // não dado de verdade"). Ou seja, o modo de falha é PERDER A LISTA INTEIRA.
            //
            // Isso era improvável enquanto se gravava uma vez por lote. Passou a ser provável quando a
            // gravação virou uma por ENTREGA (ver Persistir): a janela de corrupção ficou dezenas de
            // vezes maior, e ela abre exatamente no cenário que motivou gravar mais — queda de energia
            // no meio da madrugada. Consertar um risco criando outro não é conserto.
            //
            // `File.Move` com overwrite é atômico no mesmo volume: ou o arquivo antigo continua
            // inteiro, ou o novo aparece inteiro. Nunca um meio-termo.
            var destino = Path.Combine(Pasta, $"{Higienizar(serial)}.json");
            var temporario = destino + ".tmp";
            File.WriteAllText(temporario, JsonSerializer.Serialize(e));
            File.Move(temporario, destino, overwrite: true);
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
            // Três valores na coluna "enviado", não dois. "incerto" é o envio cujo toque aconteceu e não
            // deu pra confirmar: gravar isso como "nao" mentiria pro NumerosJaEnviados, que é a única
            // memória entre execuções, e a pessoa voltaria amanhã sem nenhum aviso de que talvez já
            // tenha recebido.
            var enviado = r.Sent ? "sim" : r.Uncertain ? "incerto" : "nao";
            var linha = string.Join(';',
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                Csv(serial), Csv(c.Numero), Csv(c.Nome ?? ""), variante.ToString(CultureInfo.InvariantCulture),
                enviado, Csv(r.DeliveryStatus ?? ""), Csv(r.Error ?? ""), Csv(texto));
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
                // "incerto" entra JUNTO com "sim": a pergunta que este conjunto responde é "pode já ter
                // recebido?", e não "recebeu com certeza?". Deixar o incerto de fora devolveria o
                // silêncio que o aviso existe pra quebrar.
                if (campos.Count > 5 && campos[5] is "sim" or "incerto")
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
