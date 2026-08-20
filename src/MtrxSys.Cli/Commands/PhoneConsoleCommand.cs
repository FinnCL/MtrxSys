using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Cli.Reporting;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Reporting;
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
        /// <summary>Anulável para distinguir "não passei a flag" de "passei 0".</summary>
        /// <remarks>
        /// 🔴 Era <c>int</c>, e por isso a flag MENTIA CALADA: o valor era atribuído antes do
        /// <c>Carregar</c>, que sobrescrevia com o teto da sessão anterior. Quem rodasse com
        /// <c>--teto 20</c> num aparelho com estado salvo não recebia teto nenhum e nem aviso.
        /// Com <c>int</c> também não havia como consertar a ordem: zero é valor legítimo ("sem teto") e
        /// é o default, então "não passei" e "passei 0" chegavam idênticos aqui.
        /// </remarks>
        [CommandOption("--teto <N>")]
        [Description("Cota por execução, em ENVIOS. 0 = sem limite: manda a lista inteira.")]
        public int? Teto { get; init; }
    }

    /// <param name="Numero">Só dígitos, já validado em 12 ou 13 (55 + DDD + número).</param>
    /// <param name="Nome">Opcional; alimenta o token <c>{nome}</c> nos textos.</param>
    private sealed record Contato(string Numero, string? Nome);

    /// <summary>Uma etapa da agenda: a que horas começa e quantos envios ela gasta.</summary>
    /// <param name="Hora">Hora do dia. Sem data de propósito: a agenda é o dia de operação, e o
    /// console não sobrevive a uma noite (ver a janela no laço de disparo).</param>
    /// <param name="Quantos">Envios desta etapa, não contatos percorridos. Mesma moeda da cota: falha
    /// e contato segurado não gastam.</param>
    private sealed record Agendamento(TimeOnly Hora, int Quantos);

    /// <summary>O que o lote produziu.</summary>
    /// <param name="SemConta">Subconjunto de <paramref name="Falhas"/>: os recusados pelo app.</param>
    /// <param name="EntregasConfirmadas">Subconjunto de <paramref name="Enviados"/> em que a tela já
    /// mostrava tique de entrega. Ver o comentário no fecho do lote: é PISO, não taxa.</param>
    /// <remarks>Record em vez de tupla de quatro inteiros: nome de campo é o que impede alguém inverter
    /// dois deles na chamada com o compilador aceitando de boa vontade.</remarks>
    private sealed record ResumoDoLote(int Enviados, int Falhas, int SemConta, int EntregasConfirmadas);

    /// <summary>O que o lote apurou, preenchido ENQUANTO ele roda.</summary>
    /// <remarks>
    /// 🔴 MUTÁVEL E DE FORA PRA DENTRO, ao contrário do <see cref="ResumoDoLote"/>, que só nasce no fim.
    /// A razão é uma só: Ctrl+C faz o <c>DispararAsync</c> nunca RETORNAR. A exceção sobe pelo
    /// <c>finally</c> e leva o valor de retorno junto — e é justamente o lote interrompido no meio da
    /// madrugada que mais precisa do relatório, porque é o que ninguém viu acontecer.
    ///
    /// <para>Quem cria é o CHAMADOR, e isso é o ponto: o objeto sobrevive à exceção porque a referência
    /// já é dele antes de a chamada começar. Devolver pelo retorno, por definição, não sobreviveria.</para>
    ///
    /// <para><see cref="Interrompido"/> nasce true e só vira false na última linha do lote. Provar que
    /// terminou é responsabilidade de quem terminou; assumir que terminou e desmarcar no erro deixaria
    /// todo caminho de saída novo nascer mentindo que foi até o fim.</para>
    /// </remarks>
    private sealed class DiarioDoLote
    {
        public DateTimeOffset Inicio { get; } = DateTimeOffset.Now;
        public List<ContatoSuspenso> Suspensos { get; } = [];
        public List<CorrecaoDeNumero> Corrigidos { get; } = [];
        public int AgendaConfirmou { get; set; }
        public int AgendaNaoConfirmou { get; set; }

        /// <summary>O lote não chegou à própria última linha. Na prática: Ctrl+C ou erro inesperado.</summary>
        /// <remarks>
        /// Parar por cota, janela ou disjuntor NÃO é interrupção: esses caminhos fazem <c>break</c>, saem
        /// pelo fim normal e já se explicam na tela. O que este flag pega é a saída que ninguém escreveu.
        /// </remarks>
        public bool Interrompido { get; set; } = true;

        public ContextoDoLote ParaContexto() =>
            new(Inicio, Corrigidos, Suspensos, AgendaConfirmou, AgendaNaoConfirmou, Interrompido);
    }

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
        // 🔴 CAMPO APOSENTADO em 2026-08-11, mantido no record só pra sessão antiga não quebrar na
        // desserialização. O toggle "gravar na agenda antes de enviar" saiu: ele gravava 2 SEGUNDOS
        // antes do envio, e o WhatsApp leva de 2,5 a 7 min pra publicar a marca — ou seja, tarde demais
        // pra o próprio envio que ele deveria ajudar. No lote de 2026-08-10 ele respondeu "já existe"
        // nos 53 contatos, gastando duas chamadas adb por contato pra confirmar o óbvio. Quem grava é o
        // `gravar`, com tempo pra sincronizar.
        public bool Agenda { get; init; } = true;
        public bool DigitacaoHumana { get; init; }

        /// <summary>Versão dos PADRÕES que este arquivo já viu. null = gravado antes de 2026-08-20.
        /// </summary>
        /// <remarks>
        /// 🔴 EXISTE PORQUE "MUDEI O PADRÃO" NÃO CHEGA EM QUEM JÁ TEM SESSÃO. Todo aparelho que já
        /// operou tem os três toggles gravados, e o <c>Carregar</c> os aplica por cima do padrão novo.
        /// Sem esta marca, trocar o default só valeria para aparelho novo, ou seja, para ninguém.
        /// <para>Não dá pra distinguir "o operador escolheu ligada" de "veio ligada por default" nos
        /// arquivos antigos: o campo é <c>bool</c> e sempre foi gravado. Por isso a correção é de UMA
        /// VEZ e AVISADA na tela: os três voltam ao padrão novo, a marca é gravada, e daí em diante
        /// escolha explícita manda.</para>
        /// </remarks>
        public int? Versao { get; init; }

        // Anulável pelo mesmo motivo do Bloco: false é escolha legítima. Sessão anterior ao campo tem
        // null e cai no default LIGADO; quem desligou continua desligado.
        public bool? Bip { get; init; }

        public bool? SegurarNaoConfirmados { get; init; }

        /// <summary>Teto automático ligado. Persistido pra o cronograma valer nos lotes seguintes.</summary>
        public bool? TetoAuto { get; init; }

        /// <summary>Marca da conta do WhatsApp vista no último lote. Só serve pra COMPARAR.</summary>
        public string? Conta { get; init; }

        /// <summary>Data (yyyy-MM-dd) em que a conta atual começou neste aparelho. Antes dela, o
        /// histórico do CSV é de OUTRA conta e não conta pro aquecimento.</summary>
        public string? ChipDesde { get; init; }

        /// <summary>Quem saiu da lista por ser número morto, no formato "numero;nome".</summary>
        /// <remarks>
        /// Anulável pela mesma razão dos outros: lista VAZIA é estado legítimo ("já devolvi todo mundo"),
        /// então ela não pode servir de "não gravado". Sessão anterior a este campo tem null.
        /// <para>Mesmo formato dos <see cref="Contatos"/> de propósito: os dois viram e voltam a ser
        /// <c>Contato</c>, e um formato só significa um lugar só pra errar o escape.</para>
        /// </remarks>
        public List<string>? Suspensos { get; init; }

        /// <summary>As etapas do dia, no formato "HH:mm;quantos".</summary>
        /// <remarks>Texto e não um record próprio pelo mesmo motivo dos contatos: o arquivo é lido por
        /// gente quando algo dá errado, e "08:00;50" se entende sem manual.
        /// <para>O nome não é só <c>Agenda</c> porque esse já foi gasto pelo toggle aposentado logo
        /// acima, e reusá-lo faria uma sessão antiga desserializar <c>true</c> dentro de uma lista.
        /// </para></remarks>
        public List<string>? AgendaDeEnvios { get; init; }
    }

    private const string TokenNome = "{nome}";

    /// <summary>Sobe quando um PADRÃO muda e precisa alcançar quem já tem sessão gravada.
    /// Ver <see cref="Estado.Versao"/>.</summary>
    private const int VersaoDosPadroes = 3;

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

    /// <summary>Tirados da fila por serem número morto, guardados em vez de apagados.</summary>
    /// <remarks>
    /// 🔴 QUARENTENA, NÃO LIXEIRA. Um número que o app negou nas DUAS formas volta em todo lote futuro
    /// se ficar na lista, abrindo conversa contra um número inexistente — que é o padrão de bot
    /// enumerando que este arquivo passa o tempo todo tentando evitar. Mas apagar de vez torna o console
    /// capaz de destruir trabalho do operador em silêncio, e quando ele desconfiar não vai ter com que
    /// discordar.
    /// <para>Guardar resolve os dois: sai da fila, continua existindo, volta com um comando. É a mesma
    /// doutrina do resto do console, que informa e deixa a decisão com quem opera.</para>
    /// </remarks>
    private readonly List<Contato> _suspensos = [];

    private readonly List<string> _textos = [];

    /// <summary>As etapas do dia, ordenadas pela hora. VAZIA é o estado normal: sem agenda, o lote sai
    /// quando o operador manda e para na cota.</summary>
    /// <remarks>
    /// 🔴 QUANDO TEM ETAPA, ELA MANDA NA COTA. Duas fontes para "quantos saem agora" seriam duas
    /// respostas para a mesma pergunta, e a que perde a disputa é sempre a que o operador acabou de
    /// configurar. O pré-voo diz qual está valendo, em vez de deixar descobrir pelo resultado.
    /// </remarks>
    private readonly List<Agendamento> _agenda = [];
    /// <summary>Intervalo de fábrica. Nomeado porque é COMPARADO, não só atribuído.</summary>
    /// <remarks>
    /// 🔴 É assim que o console distingue "o operador escolheu" de "ninguém mexeu", sem guardar um flag
    /// a mais. O flag pareceria mais explícito e seria pior: ele teria que ser persistido, e sessão
    /// antiga voltaria sem ele, indistinguível de escolha. O valor em si já responde a pergunta, e quem
    /// digitar exatamente o padrão só perde o preenchimento automático, que é inofensivo.
    /// <para>Guardados em SEGUNDOS porque é o que o laço de disparo usa; a tela toda fala em minutos.
    /// 150 e 300 são 2,5 e 5 min, escolhidos pelo operador em 2026-08-20. Na média (3,75 min) isso dá
    /// ~128 mensagens numa janela de 8 horas, que continua sendo o PLATÔ da curva de aquecimento. Ver
    /// ChipHistory.IntervaloPara.</para>
    /// </remarks>
    private const int MinPadrao = 150;
    private const int MaxPadrao = 300;

    /// <summary>O máximo de fábrica ANTERIOR (6 min). Serve só para reconhecer, na sessão gravada, quem
    /// nunca escolheu um ritmo e por isso deve receber o padrão novo.</summary>
    private const int MaxPadraoAntigo = 360;

    private int _min = MinPadrao;
    private int _max = MaxPadrao;
    /// <summary>Cota desta execução. ZERO = sem teto, manda a lista inteira.</summary>
    /// <remarks>
    /// 🔴 SEM TETO POR PADRÃO, decisão do operador em 2026-08-07: "o teto pode ser 20, 50, 100, 1000, o
    /// usuário que vai decidir". Antes era 30 e RECUSAVA lista maior, o que empurrava para as duas
    /// saídas ruins: subir o teto para o tamanho da lista, que é a rajada que ele existia para impedir,
    /// ou recortar a lista à mão a cada execução.
    ///
    /// <para>O freio deixou de ser o teto e passou a ser o RITMO entre mensagens, e o Ctrl+C interrompe
    /// a qualquer momento com o que já saiu registrado no CSV.</para>
    ///
    /// <para>🔴 SÓ VALE SEM AGENDA. Com etapas marcadas, a cota da execução é a soma delas, e este
    /// número fica de fora; o menu mostra "sem efeito" na linha em vez de deixar descobrir pelo
    /// resultado.</para>
    /// </remarks>
    private int _teto;

    // 🔴 BLOCOS E PAUSA FORAM REMOVIDOS em 2026-08-20, por decisão do operador ("sem pausas"). Eles
    // mandavam ~15 mensagens, sumiam ~30 min e voltavam, para o lote não ter a regularidade de máquina
    // que o comentário da curva do WarmupManager associa a chip restringido. O trabalho não ficou sem
    // dono: a AGENDA faz o mesmo desenho (punhado, silêncio longo, punhado) com as horas escolhidas por
    // quem opera, em vez de sorteadas pelo programa. O que se perde é a proteção AUTOMÁTICA de quem
    // dispara sem agenda: ali sobra o ritmo entre mensagens, e ele passou a ser a única defesa.

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


    /// <summary>SEGURAR o contato quando a agenda não confirma que ele tem WhatsApp. DESLIGADO por
    /// padrão: a consulta roda e é reportada, mas não muda o que sai.</summary>
    /// <remarks>
    /// 🔴 O PEDIDO QUE ORIGINOU foi "não quero gastar disparos para que falhe", e o espelho da agenda
    /// responde de graça o que a conversa responderia caro. Nasceu ligado, e o operador barrou na
    /// primeira versão com dado de operação: EXISTEM NÚMEROS COM WHATSAPP QUE O ESPELHO NÃO CONFIRMA.
    /// Ficou em MODO OBSERVAÇÃO (roda, conta, reporta, não segura ninguém) até os lotes dizerem se o
    /// espelho acerta neste parque de aparelhos.
    ///
    /// <para>🔴 EM 2026-08-20 O MESMO OPERADOR PEDIU O PADRÃO LIGADO, com os lotes já rodados na mão.
    /// Fica registrado o que isso troca: quem a agenda não confirmar NÃO recebe neste lote. O risco de
    /// antes continua existindo (espelho fraco segura contato bom), e a razão de ele ser aceitável é
    /// que SEGURAR NÃO DESCARTA: o contato continua na lista, aparece como "segurado" na linha do lote,
    /// e volta a ser tentado assim que o `4` for desligado ou o espelho sincronizar. O desperdício que
    /// ele evita é irreversível; o dele é reversível com uma tecla.</para>
    ///
    /// <para>🔴 LIGADO POR PADRÃO EM 2026-08-20 E REVERTIDO NO MESMO DIA, com dado de operação. O
    /// pedido foi "não gastar disparo em número morto", e o efeito medido foi outro: em três lotes
    /// seguidos, 20 contatos, o espelho não confirmou quase ninguém e NADA saiu. A causa não era a
    /// lista, era o RELÓGIO. O sync do WhatsApp publica o espelho minutos depois do `gravar`, e o fluxo
    /// real do operador é colar, gravar e disparar em seguida: nesse fluxo o espelho nunca chegou a
    /// tempo, e "não sei" virou "não" para o lote inteiro.</para>
    ///
    /// <para>Medido no aparelho RQ8WB048RFW: dos 5 primeiros contatos, 4 tinham espelho MINUTOS DEPOIS
    /// do lote que os segurou; dos 10 seguintes, recém-gravados, só 1 tinha. O mesmo código que segurou
    /// os 4 os confirma hoje. Ou seja, ligado por padrão ele não protege de número morto: ele adia
    /// TODO lote de contato novo, que é o caso comum aqui.</para>
    ///
    /// <para>Continua disponível no menu para quem opera lista grande e fria, onde a economia de
    /// tentativa compensa esperar o sync. Quando ligar: grave, espere o espelho aparecer, e só então
    /// dispare.</para>
    /// </remarks>
    private bool _segurarNaoConfirmados = true;

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
    /// (desligada). Espelha <see cref="PhoneOptions.HumanTyping"/>, que o driver lê a cada envio.
    /// DESLIGADA por padrão no console desde 2026-08-20, por decisão do operador.</summary>
    /// <remarks>
    /// Vira botão do console porque a escolha é de OPERAÇÃO, não de instalação: ligada, só sai ASCII
    /// (o `input text` não digita acento nem emoji); desligada, sai qualquer caractere, mas o campo
    /// nasce preenchido e o destinatário nunca vê "digitando…".
    /// <para>🔴 O PADRÃO DO CONSOLE NÃO É MAIS O DE <see cref="PhoneOptions.HumanTyping"/>, que segue
    /// ligado para o dispatcher e o `phone send`. Aqui o texto é COLADO por uma pessoa, e texto colado
    /// por brasileiro vem com acento: com a digitação ligada, o pré-voo barrava o lote e a saída era
    /// tirar os acentos da mensagem. O console prefere a mensagem certa à animação de "digitando…".</para>
    /// </remarks>
    private bool _digitacaoHumana;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var ct = cancellation.Token;

        var opts = options.Value;
        var serial = string.IsNullOrWhiteSpace(opts.AdbSerial) ? "(sem serial)" : opts.AdbSerial;
        // NÃO herda mais o Phone__HumanTyping: o console tem o próprio padrão (desligada) e ele é quem
        // manda em opts logo abaixo. Herdar aqui só fazia o padrão do produto vazar para uma tela que
        // tem botão próprio para isso, e o operador via "ligada" sem ter ligado.

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

        // 🔴 A FLAG GANHA DO ESTADO SALVO, e por isso vem DEPOIS do Carregar. Antes vinha antes, e era
        // sobrescrita em silêncio: `--teto 20` num aparelho com sessão salva não fazia nada e não dizia
        // nada. Ordem invertida sem alarde é a pior forma de configuração errada, porque o operador tem
        // prova na tela de que pediu.
        if (s.Teto is { } tetoDaLinha)
        {
            _teto = Math.Max(0, tetoDaLinha);
            _tetoAuto = false;
            AnsiConsole.MarkupLine(
                $"[grey]--teto {_teto} veio da linha de comando e vale para esta sessão"
                + (_teto == 0 ? " (sem limite)" : "") + ".[/]");
        }

        // O que a sessão gravou manda no que o driver faz: PhoneOptions é singleton e o driver relê
        // HumanTyping a cada envio, então escrever aqui basta.
        opts.HumanTyping = _digitacaoHumana;

        // 🔴 A TELA DE AJUDA FOI REMOVIDA em 2026-08-20, por decisão do operador. Ela era um tutorial de
        // 8 passos impresso a cada abertura, e existia porque o menu antigo não contava a ordem das
        // coisas: 1 e 2 eram ajustes, contatos era o 4, e a sequência real só estava escrita ali.
        // Com o menu reordenado pelo fluxo (cola, grava, confere, escreve, agenda, dispara), o tutorial
        // passou a repetir o painel, e repetição desatualiza: a ajuda ainda mandava digitar "9 ver"
        // depois do `ver` ter sido removido. As instruções de COLAGEM continuam vivas, impressas no
        // momento em que a colagem começa, que é onde elas são lidas de verdade. O `comandos` continua
        // sendo a referência seca de tudo.
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
                    // A ORDEM AQUI É A DA TABELA do Menu, e vale mantê-la: quando as duas divergiram,
                    // a linha nova entrou no fim do switch e ninguém percebeu que o número já era de
                    // outro comando. Lendo de cima a baixo, o conflito aparece.
                    case "1":
                        switch (PerguntarModo(_contatos.Count, "contato(s)"))
                        {
                            case ModoLista.Editar: EditarContato(); break;
                            case ModoLista.Cancelar: AnsiConsole.MarkupLine("[grey]cancelado.[/]"); break;
                            case var m: LerContatos(somar: m == ModoLista.Acrescentar); break;
                        }
                        Salvar(serial);
                        break;
                    case "2":
                        await GravarAgendaAsync(ct);
                        break;
                    case "3":
                        Conferir();
                        break;
                    case "4":
                        switch (PerguntarModo(_textos.Count, "template(s)"))
                        {
                            case ModoLista.Editar: EditarTexto(); break;
                            case ModoLista.Cancelar: AnsiConsole.MarkupLine("[grey]cancelado.[/]"); break;
                            case var m: LerTextos(serial, somar: m == ModoLista.Acrescentar); break;
                        }
                        Salvar(serial);
                        break;
                    case "5":
                        QuantoEQuando();
                        Salvar(serial);
                        break;
                    case "6":
                        Previa();
                        break;
                    case "7":
                        IntervaloInterativo();
                        Salvar(serial);
                        break;
                    case "8":
                        JanelaInterativa();
                        Salvar(serial);
                        break;
                    case "9":
                        AlternarDigitacaoHumana();
                        Salvar(serial);
                        break;
                    case "10":
                        AlternarSegurar();
                        Salvar(serial);
                        break;
                    case "11":
                        await AlternarBipAsync();
                        Salvar(serial);
                        break;
                    case "12":
                        Planilha(serial);
                        Salvar(serial);
                        break;
                    // Sem "13": o disparo só atende pela palavra (o `case "enviar"` mais abaixo). Ver o
                    // Menu para o porquê de ele ser a única exceção ao painel numerado.
                    case "0":
                        sair = true;
                        break;

                    // ── por extenso, e os que valem nas duas formas ──────────────────────────────
                    // `ajuda` caindo na lista de comandos: a tela de tutorial saiu, e mandar quem pediu
                    // ajuda para "comando desconhecido" seria a pior resposta possível à palavra.
                    case "ajuda" or "?" or "help" or "comandos":
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
                    case "agenda":
                        LerAgenda(somar: partes is [_, "+", ..]);
                        Salvar(serial);
                        break;
                    // `ver` sobrevive como APELIDO, não como tela: o comando sumiu do painel, mas quem
                    // já digitava ver merece cair onde a informação foi parar, e não em "comando
                    // desconhecido". Custa uma linha e evita a busca por uma tela que não existe mais.
                    case "previa" or "ver":
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
                    // Manual porque nem todo aparelho responde qual conta está registrada. Onde ele
                    // responde, a troca é detectada sozinha e ninguém precisa deste comando.
                    case "chip" when partes.Length >= 2
                        && string.Equals(partes[1], "novo", StringComparison.OrdinalIgnoreCase):
                        ChipNovo(serial);
                        break;
                    case "chip":
                        AnsiConsole.MarkupLine(
                            "[red]uso:[/] chip novo   [grey]— use SÓ depois de registrar outra conta do "
                            + "WhatsApp neste aparelho. trocar o SIM sem registrar de novo mantém a mesma "
                            + "conta, e aí o histórico continua valendo.[/]");
                        break;
                    case "parar":
                        Parar(partes);
                        Salvar(serial);
                        break;
                    // `blocos` some junto com o ajuste: um comando que ainda responde depois de o
                    // recurso sair é pior que um comando desconhecido, porque parece ter funcionado.
                    case "janela":
                        Janela(partes);
                        Salvar(serial);
                        break;
                    case "segurar":
                        AlternarSegurar();
                        Salvar(serial);
                        break;
                    case "bip" or "som":
                        await AlternarBipAsync();
                        Salvar(serial);
                        break;
                    case "acentos" or "semacento":
                        TirarAcentos();
                        Salvar(serial);
                        break;
                    case "digitacao" or "digitação":
                        AlternarDigitacaoHumana();
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
                    // `suspensos` continua caindo aqui: a quarentena virou aba da planilha, e quem
                    // digitava a palavra tem que chegar onde o dado foi parar.
                    case "relatorio" or "relatório" or "planilha" or "suspensos":
                        Planilha(serial);
                        Salvar(serial);
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
                        AnsiConsole.MarkupLine(
                            $"[red]comando desconhecido:[/] {partes[0].EscapeMarkup()}. "
                            + "o menu está logo acima; [bold]comandos[/] lista todos.");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C cancela o token do processo inteiro (Program.cs), então não dá pra voltar ao
                // prompt: o próximo envio nasceria cancelado. Sai avisando, com o estado salvo.
                AnsiConsole.MarkupLine("\n[yellow]interrompido. o que já saiu está no log; a lista foi salva.[/]");
                Salvar(serial);
                // 130 e não 1: é o código consagrado para "interrompido pelo operador", e o atalho
                // usa exatamente essa distinção para NÃO mostrar a caixa de "o console não abriu".
                // Interromper um lote de propósito não é defeito, e tratar como defeito ensina o
                // operador a ignorar a caixa que existe para os defeitos de verdade.
                return 130;
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
    /// <summary>Como se escreve uma linha em branco DENTRO do texto, dito nos dois caminhos que colam
    /// template: o de colar novo e o de corrigir um existente.</summary>
    /// <remarks>
    /// 🔴 UM MÉTODO, e não a mesma frase escrita duas vezes. Ela já nasceu divergindo: a versão do
    /// "corrigir" entrou depois e, enquanto foram dois textos, qualquer ajuste num deles (o `..` virar
    /// outro escape, por exemplo) deixaria o outro ensinando o caminho antigo. Instrução duplicada
    /// envelhece pela metade, e a metade velha é indistinguível da nova para quem lê.
    /// </remarks>
    private static void ExplicarLinhaEmBranco() =>
        AnsiConsole.MarkupLine(
            "[grey]para uma[/] [bold]linha em branco DENTRO[/] [grey]do texto, use[/] [bold]..[/] "
            + "[grey](DOIS pontos) numa linha sozinha. um ponto só, como a linha vazia, encerra.[/]");

    private static (string? Texto, bool Fim) LerVariante()
    {
        var linhas = new List<string>();
        while (true)
        {
            var l = Console.ReadLine();
            if (l is null || l.Trim() is "fim" or ".")
            {
                // 🔴 O PONTO SOZINHO EXPLICA O QUE ACABOU DE FAZER. Ele encerra a colagem inteira, e a
                // confusão com o `..` foi relatada pelo próprio operador em 2026-08-20 ("eu achava que
                // era com um ponto"). O engano é silencioso e caro: o bloco fecha, e as linhas seguintes
                // do texto que a pessoa ia colar caem no menu, uma a uma, como comando desconhecido.
                // Uma linha de aviso na hora vale mais que qualquer instrução impressa antes, porque
                // chega no instante em que a expectativa e o resultado divergem.
                if (l?.Trim() == ".")
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]o ponto sozinho encerrou a colagem.[/] [grey]se você queria uma linha "
                        + "em branco DENTRO do texto, ela se escreve com[/] [bold]..[/] [grey](dois "
                        + "pontos). entre de novo na opção[/] [bold]4[/] [grey]para continuar.[/]");
                }
                return (Juntar(linhas), true);
            }
            if (l.Trim().Length == 0)
            {
                return (Juntar(linhas), false);
            }
            // 🔴 ESCAPE PARA LINHA EM BRANCO DENTRO DO TEXTO. A linha vazia já significa "fecha o
            // template", então ela não pode ao mesmo tempo significar "parágrafo" — é o mesmo caractere
            // com dois papéis, e um deles tem que ceder. O texto cede pro escape.
            //
            // `..` e não `.` porque o ponto sozinho JÁ encerra o bloco (ver FimDoBloco). Dois pontos
            // seguidos é mnemônico contra ele: um encerra, dois abrem espaço.
            linhas.Add(l.Trim() == ".." ? "" : l.TrimEnd());
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
        ExplicarLinhaEmBranco();

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
                + "(emoji ou símbolo). tire à mão, ou desligue a digitação humana no [bold]9[/].");
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

    /// <summary>A única tela de conferência antes do disparo: quem recebe qual texto, e com quais
    /// ajustes.</summary>
    /// <remarks>
    /// 🔴 ABSORVEU O `ver`, que era um segundo comando fazendo quase a mesma coisa: listava contatos e
    /// templates lado a lado, sem cruzar um com o outro. Dois comandos vizinhos que respondem "o que
    /// está carregado?" obrigam a pessoa a abrir os dois para ter certeza de que não perdeu nada, que é
    /// o oposto de conferir. Sobrou o que cruza, e o que só o `ver` mostrava veio junto:
    /// <list type="bullet">
    /// <item>a linha de ajustes no rodapé (intervalo, cota ou agenda, janela);</item>
    /// <item>o modo SEM TEMPLATE: o `ver` funcionava com a lista colada e nenhum texto, e a prévia
    /// recusava. Recusar aqui obrigaria a inventar um template só para reler a lista.</item>
    /// </list>
    /// </remarks>
    private void Previa()
    {
        if (_contatos.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]sem contatos.[/] use [bold]contatos[/] e cole a lista.");
            return;
        }

        // Sem template ainda não há cruzamento, mas há lista: mostra o que existe em vez de recusar.
        if (_textos.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{_contatos.Count} contato(s) na lista, nenhum template ainda.[/] [grey]cole ao "
                + "menos um em[/] [bold]4[/] [grey]para ver quem recebe o quê.[/]");
            MostrarAlguns(_contatos.Count, 15, i => $"  [blue]{i + 1}[/] {DescreverContato(i)}");
            MostrarAjustes();
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
        MostrarAjustes();
    }

    /// <summary>Os ajustes do lote em uma linha. Herdado do `ver`: o plano na tela não diz nada sobre o
    /// RITMO em que ele sai, e é o ritmo que decide se o lote acaba hoje ou de madrugada.</summary>
    private void MostrarAjustes() =>
        AnsiConsole.MarkupLine(
            $"[grey]ritmo {RitmoDescrito()} · "
            + (_agenda.Count > 0 ? $"agenda: {_agenda.Count} etapa(s)" : $"cota {TetoDescrito()}")
            + $" · {JanelaDescrita()}[/]");

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
            //
            // 🔴 SEM PRAZO PROMETIDO, e com um jeito de CONFERIR no lugar dele. A medição que temos é de
            // um dia, num parque de aparelhos: ela descreve o que aconteceu, não o que vai acontecer.
            // Quem sincroniza é a conta Google do celular, e isso depende da rede dele, da bateria e do
            // humor do Android. Prazo inventado vira promessa quebrada; o teste abaixo não quebra.
            AnsiConsole.MarkupLine(
                "[yellow]não dispare ainda[/][grey]: contato recém-criado só chega ao WhatsApp depois "
                + "que o aparelho sincroniza pela conta Google. na nossa medição isso levou de 2,5 a 7 "
                + "min, mas é o que MEDIMOS um dia, não um prazo: pode demorar mais.[/]");
            // 🔴 O TESTE É O PRÓPRIO LOTE, e ele é seguro por causa do `segurar`: contato que a agenda
            // não confirma é PULADO sem abrir conversa nenhuma, então sondar não gasta tentativa nem
            // risco. Sem essa propriedade, "dispare para descobrir" seria um conselho caro.
            AnsiConsole.MarkupLine(
                "[grey]como saber que já foi, em vez de cronometrar: com o[/] [bold]10[/] [grey]ligado, "
                + "dispare e olhe a contagem de \"a agenda confirmou\". quem ela não confirma é segurado "
                + "sem gastar tentativa, então a sondagem é de graça: se não confirmar quase ninguém, "
                + "ainda não sincronizou.[/]");
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
        // existe para impedir, ou recortar a lista à mão a cada execução. Com a agenda o lote
        // se espalha pelo dia, então o teto passa a ser a COTA da execução: manda os primeiros, e o resto
        // fica na lista, que é justamente o que faz o ciclo de vários dias funcionar sozinho.
        // O log é lido AQUI porque o teto automático precisa dele antes de cortar o plano, e o painel
        // logo abaixo usa o mesmo resumo. Uma leitura serve às três coisas.
        // A conta ANTES de ler o log: se ela mudou, o corte do histórico muda junto e o painel logo
        // abaixo já mostra a curva certa. Ler primeiro daria o número da conta anterior.
        await ConferirContaAsync(serial, ct);
        var resumoDoLog = LerLog(serial, _chipDesde);

        // 🔴 TETO AUTOMÁTICO: a cota do dia sai do histórico deste chip, descontando o que já saiu hoje.
        // É a única coisa que corta sozinha, e só porque o operador pediu com `teto auto`.
        // 🔴 A AGENDA MANDA NA COTA quando existe. Duas fontes para "quantos saem agora" dariam duas
        // respostas, e a que perde seria sempre a que o operador acabou de digitar. Aqui a cota da
        // execução é a soma das etapas, e o pré-voo diz isso em voz alta.
        var tetoEfetivo = _agenda.Count > 0 ? TotalAgendado() : _teto;
        if (_tetoAuto && _agenda.Count == 0)
        {
            var sug = ChipHistory.Sugerir(
                resumoDoLog.DiasAtivos, resumoDoLog.UltimoFechado, resumoDoLog.DiasDesdeUltimoDia);
            tetoEfetivo = Math.Max(0, sug.Sugestao - resumoDoLog.EnviadasHoje);
            if (tetoEfetivo == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]teto automático: a cota de hoje (~{sug.Sugestao}) já foi alcançada. "
                    + $"{resumoDoLog.EnviadasHoje} já saíram deste aparelho.[/] [grey]nada será enviado "
                    + "agora. volte amanhã, ou use[/] teto <n> [grey]pra assumir o controle.[/]");
                return;
            }
        }

        // 🔴 A COTA É DE ENVIO, NÃO DE TENTATIVA, e a versão anterior cortava o plano em N CONTATOS.
        // Falha, contato segurado pela agenda e duplicata pulada gastavam cota sem nada sair. Na lista
        // real de 2026-08-10 isso é a maioria: 87 contatos, 34 segurados e ~17 na forma de fixo — cota
        // de 40 entregava perto de 20, e o aquecimento andava pela metade da velocidade sem explicação.
        // A cota protege a conta contra CONVERSA ABERTA, e recusa não abre conversa nenhuma.
        // Por isso o plano NÃO é mais recortado aqui: o laço percorre a lista e para no N-ésimo ENVIO.
        var alcance = tetoEfetivo > 0 ? Math.Min(plano.Count, tetoEfetivo) : plano.Count;
        if (tetoEfetivo > 0 && plano.Count > tetoEfetivo)
        {
            AnsiConsole.MarkupLine(
                $"[grey]lista com[/] [bold]{plano.Count}[/][grey] contato(s); esta execução para depois "
                + $"de[/] [bold]{tetoEfetivo}[/] [grey]ENVIO(s) "
                + $"({(_agenda.Count > 0 ? "soma da agenda" : _tetoAuto ? "teto automático" : "cota")}). "
                + "quem falhar ou não tiver WhatsApp não gasta cota, então ela pode percorrer mais "
                + "contatos que isso. o resto fica na lista.[/]");
        }

        // 🔴 VOLUME E INTERVALO SÃO UM PARÂMETRO SÓ (ver ChipHistory), e o `teto auto` mexia só no
        // volume. Cota de 2 com o intervalo do platô despacha o dia em 4 minutos e silencia 12 horas,
        // que é o padrão que o volume baixo existia pra evitar.
        //
        // 🔴 SÓ PREENCHE O QUE VOCÊ NÃO ESCOLHEU. Se o operador digitou `intervalo`, o console não
        // sobrescreve: mandar automatizar o VOLUME não é autorizar mexer numa escolha explícita dele.
        // Aqui ele avisa e deixa como está. Local, nunca em _min/_max: gravar tornaria o ajuste
        // "escolhido" e o lote seguinte já não saberia distinguir.
        var (minEfetivo, maxEfetivo) = (_min, _max);
        if (_tetoAuto && alcance > 0)
        {
            var (im, ix) = ChipHistory.IntervaloPara(alcance, _horaFim - _horaInicio);
            if (_min == MinPadrao && _max == MaxPadrao)
            {
                (minEfetivo, maxEfetivo) = (im, ix);
                AnsiConsole.MarkupLine(
                    $"[grey]ritmo desta execução:[/] [bold]{EmMinutos(im)} a {EmMinutos(ix)} min[/] "
                    + $"[grey](calculado pra espalhar {alcance} pela janela; você não escolheu um, então "
                    + "o teto automático preenche). fixe o seu com[/] intervalo <min> <max][grey].[/]");
            }
            else if (ix > _max * 2)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]seu ritmo é {RitmoDescrito()} e a cota é {alcance}.[/] [grey]isso despacha "
                    + $"o dia inteiro em pouco tempo e depois silencia. pra espalhar, seria[/] "
                    + $"intervalo {EmMinutos(im)} {EmMinutos(ix)}[grey]. não mudei nada: o ajuste é seu.[/]");
            }
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

        AvisarRepeticao(plano, resumoDoLog.JaEnviados, resumoDoLog.TalvezReceberam);
        AvisarFormaSuspeita(plano);

        var segurarNesteLote = await SegurarValeNesteLoteAsync(plano, ct);
        if (segurarNesteLote is null)
        {
            return;   // o operador escolheu esperar o sync
        }

        // Estimativa sobre o ALCANCE (a cota), não sobre a lista: com cota, o lote termina na cota.
        var estimativa = EsperaDe(alcance, minEfetivo, maxEfetivo);

        AnsiConsole.MarkupLine(
            $"[bold]{alcance}[/] mensagem(ns), ritmo de {EmMinutos(minEfetivo)} a "
            + $"{EmMinutos(maxEfetivo)} min ({JanelaDescrita()}).");
        if (_agenda.Count > 0)
        {
            // Com agenda, a linha acima descreve o dia INTEIRO, e o dia não sai de uma vez: a tabela é
            // que diz quando cada pedaço começa. Sem ela, "término por volta das 18h" seria lido como
            // "vai ficar mandando até as 18h", que é o oposto do que a agenda faz.
            AnsiConsole.MarkupLine(
                "[bold]a agenda manda nesta execução[/] [grey](a cota fica de fora). o console espera a "
                + "hora de cada etapa e dispara sozinho, então ele precisa ficar ABERTO até a última.[/]");
            MostrarAgenda();
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[grey]~[/][bold]{Duracao(estimativa)}[/][grey] só de espera (o envio em si soma mais). "
                + $"término por volta das[/] [bold]{DateTime.Now.Add(estimativa):HH:mm}[/][grey].[/]");
        }
        MostrarPainelDoChip(alcance, resumoDoLog);
        AvisarRiscoDoLote(alcance);
        AnsiConsole.Markup("[bold]confirmar? digite[/] sim [bold]:[/] ");

        if (!string.Equals(Console.ReadLine()?.Trim(), "sim", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]cancelado, nada foi enviado.[/]");
            return;
        }

        var log = AbrirLog(serial);

        // 🔴 SEGURA O PC ACORDADO PELO LOTE INTEIRO. Uma etapa agendada passa horas sem teclado nem
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

        // 🔴 CRIADO AQUI FORA, e o carimbo de início nasce com ele. O CSV é append-only e não tem noção
        // de "lote": ele é um rio de tentativas, e o recorte no relatório é "tudo gravado daqui pra
        // frente". Ver o DiarioDoLote sobre por que ele é do chamador e não do lote.
        var diario = new DiarioDoLote();
        try
        {
            var resumo = _agenda.Count == 0
                ? await DispararAsync(
                    plano, log, serial, tetoEfetivo, minEfetivo, maxEfetivo, segurarNesteLote.Value, diario, ct)
                : await DispararAgendaAsync(
                    log, serial, minEfetivo, maxEfetivo, segurarNesteLote.Value, diario, ct);

            // O recorte de "sem conta" sai NO FECHO porque é a linha que sobra na tela e a que o
            // operador lê de manhã. Sem ele, "0 enviada(s), 87 falha(s)" some com a informação que
            // importa: as 87 são a MESMA categoria, e categoria única em bloco é sintoma de causa
            // comum, não de lista fria.
            var recorte = resumo.SemConta == 0 ? "" : $" [grey]({resumo.SemConta} sem conta)[/]";
            AnsiConsole.MarkupLine(
                resumo.Falhas == 0
                    ? $"[green]lote concluído: {resumo.Enviados} enviada(s), sem falhas.[/]"
                    : $"[yellow]lote concluído: {resumo.Enviados} enviada(s), {resumo.Falhas} falha(s).[/]{recorte}");

            // 🔴 MEDE E MOSTRA, NÃO ALARMA. A tentação era acusar shadow-restriction quando poucas
            // entregas se confirmam. Não faço, e a razão está escrita no DispatchEngine: a leitura
            // acontece SEGUNDOS depois do toque, então destinatário com o aparelho desligado aparece
            // como "sent". Este número é um PISO da taxa real, não a taxa — e o próprio motor mantém o
            // guard DESLIGADO no caminho de UI por isso, com o plano explícito de "primeiro se acumula
            // o dado, depois se escolhe o limiar em cima da distribuição observada". Alarmar agora, com
            // limiar chutado, pausaria lote por gente offline. Mostrar acumula a distribuição sem
            // prometer conclusão nenhuma.
            if (resumo.Enviados > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]entrega já confirmada na tela em {resumo.EntregasConfirmadas} de "
                    + $"{resumo.Enviados}. o resto pode ter sido entregue depois: a leitura acontece "
                    + "segundos após o envio, então este número é um piso, não a taxa real.[/]");
            }
            AnsiConsole.MarkupLine($"[grey]log: {log.EscapeMarkup()}[/]");
        }
        finally
        {
            // 🔴 NO FINALLY, e é o motivo de o diário existir. Ctrl+C num lote de horas fazia o
            // relatório ser pulado e o console fechar em seguida, então nem o comando `relatorio`
            // sobrava: o único lote que ninguém viu acontecer era justamente o que ficava sem
            // explicação. O que já saiu está no CSV de qualquer jeito, e é dele que a planilha nasce.
            GerarRelatorio(serial, diario.ParaContexto());
        }
    }

    /// <summary>Escreve a planilha do lote e a abre.</summary>
    /// <remarks>
    /// 🔴 NUNCA PROPAGA. Mesma doutrina do <c>Registrar</c> e do <c>BiparAsync</c>: um lote de horas não
    /// pode morrer depois de ter enviado tudo porque o disco encheu, porque o relatório anterior ficou
    /// aberto no Excel, ou porque o antivírus segurou o arquivo. A mensagem é o trabalho; a planilha é a
    /// leitura do trabalho, e ela chega no CSV de qualquer jeito.
    ///
    /// <para>Abrir sozinha é o ponto do "automaticamente": um caminho impresso no fim de um lote que
    /// terminou às 3h da manhã é um caminho que ninguém vai copiar. A abertura também engole erro, e
    /// separadamente: falhar em ABRIR não pode esconder que o arquivo foi GERADO.</para>
    /// </remarks>
    private void GerarRelatorio(string serial, ContextoDoLote? lote)
    {
        try
        {
            var linhas = LerLinhas(serial);
            if (linhas.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]sem envios registrados neste aparelho: nada pra relatar.[/]");
                return;
            }

            var arquivo = PlanilhaDeEnvios.Gerar(
                Higienizar(serial), linhas, lote, Pasta, DateTimeOffset.Now,
                [.. _suspensos.Select(c => (c.Numero, c.Nome))]);
            AnsiConsole.MarkupLine($"[green]planilha:[/] {arquivo.EscapeMarkup()}");
            Abrir(arquivo);
        }
        // 🔴 CATCH LARGO, DE PROPÓSITO, e este é um dos poucos lugares onde ele se justifica. Aqui as
        // mensagens JÁ SAÍRAM: o trabalho está feito e gravado no CSV, e o que resta é desenhar. Uma
        // biblioteca de terceiro no meio (ClosedXML sobre OpenXML sobre zip) tem um repertório de
        // exceções que não dá pra enumerar honestamente, e enumerar errado significa derrubar o console
        // depois de um lote de horas por causa de um arquivo .xlsx.
        // OperationCanceledException é reerguida porque não é falha: é o Ctrl+C do operador subindo, e
        // engoli-la faria o console continuar como se nada tivesse sido pedido.
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[grey]não deu pra gerar a planilha: {ex.Message.EscapeMarkup()}[/] "
                + "[grey]o CSV do lote está intacto e o relatório pode ser refeito com[/] relatorio");
        }
    }

    /// <summary>Abre o arquivo no programa padrão do sistema. Silencioso quando não dá.</summary>
    private static void Abrir(string arquivo)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(arquivo) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Máquina sem Excel, sem associação de .xlsx, ou console rodando por SSH. O caminho já foi
            // impresso na linha de cima, então não há nada a acrescentar — e falhar em ABRIR não pode
            // esconder que o arquivo foi GERADO, que é a parte que importa.
        }
    }

    /// <summary>O laço de disparo. Separado do <see cref="EnviarAsync"/> porque lá tudo é decisão
    /// (pode? vale a pena? confirma?) e aqui tudo é execução — e porque um laço com efeito
    /// irreversível merece um <c>finally</c> visível em vez de ficar no meio de 140 linhas.</summary>
    /// <param name="cota">Quantos ENVIOS esta execução pode fazer. 0 = sem cota.</param>
    /// <param name="intervaloMin">Intervalo mínimo DESTA execução; pode diferir de <c>_min</c> quando o
    /// teto automático preencheu um intervalo que o operador nunca escolheu.</param>
    /// <summary>O `segurar` é confiável NESTE lote? Sonda a agenda antes de gastar o disparo.</summary>
    /// <returns>true = segurar; false = disparar sem segurar; null = o operador cancelou.</returns>
    /// <remarks>
    /// 🔴 NASCEU DE UM LOTE PERDIDO, em 2026-08-20. O `segurar` pergunta à agenda "este número tem
    /// WhatsApp?" e ela responde "sim" ou "NÃO SEI", nunca "não". Ligado, ele trata "não sei" como
    /// "não", e isso está certo enquanto o espelho do app existe. Só que o espelho é publicado pelo
    /// sync do WhatsApp MINUTOS depois do `gravar`, e o fluxo real é colar, gravar e disparar: o
    /// operador rodou três lotes, 20 contatos, e nenhum saiu. Nenhum deles era número morto: medido no
    /// aparelho, 4 dos 5 primeiros tinham espelho MINUTOS DEPOIS do lote que os segurou.
    ///
    /// <para>A correção não é desligar o `segurar`, é parar de confiar nele quando a resposta é
    /// uniforme. O próprio <c>WhatsAppContactsReader</c> já registra a regra: "negativo em 100% de um
    /// lote não é o mundo real, é uma consulta olhando para o lugar errado". Aqui essa regra sai do
    /// comentário e vira decisão, ANTES de gastar o lote.</para>
    ///
    /// <para>SAI NA PRIMEIRA CONFIRMAÇÃO, e é o que torna a sondagem barata: se o espelho está
    /// funcionando, o primeiro ou o segundo número já respondem "sim" e a sondagem custa segundos. Ela
    /// só percorre a amostra inteira no caso em que isso vale a pena, que é justamente aquele em que o
    /// lote inteiro seria segurado.</para>
    /// </remarks>
    private async Task<bool?> SegurarValeNesteLoteAsync(
        List<(Contato Contato, int Variante, string Texto)> plano, CancellationToken ct)
    {
        if (!_segurarNaoConfirmados || plano.Count == 0)
        {
            return _segurarNaoConfirmados;
        }

        // Amostra, não a lista toda: cada pergunta é uma ida ao aparelho, e o que se quer saber aqui é
        // se o espelho RESPONDE, não quem exatamente ele confirma. Doze é o bastante para distinguir
        // "espelho vazio" de "lista com alguns mortos", e o laço já para na primeira confirmação.
        var amostra = Math.Min(plano.Count, 12);
        AnsiConsole.Markup($"[grey]conferindo o espelho da agenda em até {amostra} contato(s)…[/] ");
        var confirmados = 0;
        for (var i = 0; i < amostra && confirmados == 0; i++)
        {
            if (await phone.IsOnWhatsAppAsync(plano[i].Contato.Numero, ct) is true)
            {
                confirmados++;
            }
        }

        if (confirmados > 0)
        {
            AnsiConsole.MarkupLine("[green]o espelho está respondendo.[/]");
            return true;
        }

        AnsiConsole.MarkupLine($"[yellow]nenhum dos {amostra} foi confirmado.[/]");
        AnsiConsole.MarkupLine(
            "[grey]isso quase nunca é lista morta: o espelho do WhatsApp só aparece MINUTOS depois do[/] "
            + "[bold]2[/] [grey](gravar), e sem ele a agenda responde \"não sei\" para todo mundo. com o[/] "
            + "[bold]10[/] [grey]ligado, \"não sei\" segura, e o lote inteiro ficaria parado sem nada "
            + "sair.[/]");
        AnsiConsole.MarkupLine("  [bold]1[/] [grey]disparar agora, SEM segurar neste lote (o ajuste do 10 fica como está)[/]");
        AnsiConsole.MarkupLine("  [bold]2[/] [grey]cancelar e esperar o espelho sincronizar[/]");
        AnsiConsole.Markup("[grey]escolha (Enter cancela):[/] ");

        if (Console.ReadLine()?.Trim() == "1")
        {
            AnsiConsole.MarkupLine(
                "[grey]só neste lote: o console vai tentar todo mundo e descobrir abrindo a conversa. "
                + "quem não tiver conta aparece com o motivo, e o[/] [bold]10[/] [grey]continua ligado "
                + "para o próximo.[/]");
            return false;
        }

        AnsiConsole.MarkupLine(
            "[grey]cancelado, nada foi enviado. rode o[/] [bold]2[/] [grey]se ainda não gravou, espere "
            + "alguns minutos e dispare de novo: quando o espelho chegar, esta conferência passa "
            + "direto.[/]");
        return null;
    }

    /// <summary>O lote em ETAPAS: espera a hora de cada uma e dispara a cota dela.</summary>
    /// <remarks>
    /// 🔴 CHAMA O MESMO <see cref="DispararAsync"/> uma vez por etapa, em vez de ensinar o laço de
    /// disparo a conhecer horário. O laço já tem disjuntor, dedup, segunda chance, quarentena e
    /// janela; enfiar agenda ali dentro seria uma sexta razão para ele parar no meio, e cada uma delas
    /// precisa de mensagem própria. Aqui a etapa é só "uma execução com cota N que começa às H".
    /// <para>SORTEIA DE NOVO A CADA ETAPA, e é obrigatório: quem recebeu na etapa anterior já saiu de
    /// <c>_contatos</c> (o <c>Persistir</c> tira a cada entrega), então um plano tirado lá atrás
    /// mandaria de novo para quem já recebeu.</para>
    /// <para>O console precisa ficar ABERTO entre as etapas, e por isso ele segura o PC acordado e a
    /// espera tem cronômetro na tela: espera sem contador é indistinguível de programa travado.</para>
    /// </remarks>
    private async Task<ResumoDoLote> DispararAgendaAsync(
        string log,
        string serial,
        int intervaloMin,
        int intervaloMax,
        bool segurar,
        DiarioDoLote diario,
        CancellationToken ct)
    {
        var (enviados, falhas, semConta, confirmadas) = (0, 0, 0, 0);

        for (var i = 0; i < _agenda.Count; i++)
        {
            var etapa = _agenda[i];
            await EsperarHoraAsync(etapa, i + 1, _agenda.Count, ct);

            var plano = Sortear();
            if (plano.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]a lista acabou antes da etapa {i + 1}.[/] [grey]as etapas restantes não "
                    + "têm quem receber; cole mais contatos e rode de novo.[/]");
                break;
            }

            // 🔴 O APARELHO É RECONFERIDO A CADA ETAPA, e não só no pré-voo. Entre confirmar o lote e a
            // etapa das 18h passam horas: cabo esbarrado, celular reiniciado, USB que dormiu, tela
            // travada. Sem esta conferência, a etapa inteira era despejada contra um aparelho morto, uma
            // falha por contato, e com `parar 0` (o padrão) nada interrompia isso.
            //
            // PULA A ETAPA em vez de encerrar o lote: as etapas seguintes são horas depois, e nesse
            // intervalo o cabo pode voltar. Encerrar tudo por causa de um tropeço às 14h jogaria fora um
            // cronograma que ainda tinha conserto, e é a decisão irreversível entre as duas.
            if (!await AparelhoPronto(ct))
            {
                AnsiConsole.MarkupLine(
                    $"[red]etapa {i + 1} pulada: o aparelho não está pronto agora.[/] [grey]nada foi "
                    + "tentado, e ninguém saiu da lista. religue o cabo e a próxima etapa segue no "
                    + "horário dela.[/]");
                continue;
            }

            AnsiConsole.Write(new Rule(
                $"[bold]etapa {i + 1}/{_agenda.Count}[/]  ·  {etapa.Hora:HH\\:mm}  ·  "
                + $"{etapa.Quantos} envio(s)").LeftJustified());

            var r = await DispararAsync(
                plano, log, serial, etapa.Quantos, intervaloMin, intervaloMax, segurar, diario, ct);
            enviados += r.Enviados;
            falhas += r.Falhas;
            semConta += r.SemConta;
            confirmadas += r.EntregasConfirmadas;
        }

        return new ResumoDoLote(enviados, falhas, semConta, confirmadas);
    }

    /// <summary>Segura o lote até a hora da etapa, com cronômetro. Hora já passada começa agora.</summary>
    /// <remarks>
    /// Hora passada NÃO vira "amanhã": o console não sobrevive a uma noite por decisão registrada na
    /// janela do laço de disparo, e adiar para amanhã seria prometer um envio que ninguém vai ver
    /// acontecer. Começar agora é o que o operador quis dizer ao confirmar o lote depois da hora.
    /// </remarks>
    private static async Task EsperarHoraAsync(Agendamento etapa, int numero, int total, CancellationToken ct)
    {
        var alvo = DateTime.Now.Date.Add(etapa.Hora.ToTimeSpan());
        if (alvo <= DateTime.Now)
        {
            AnsiConsole.MarkupLine(
                $"[blue]etapa {numero}/{total}:[/] [grey]as {etapa.Hora:HH\\:mm} já passaram, "
                + "então ela começa agora.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[blue]etapa {numero}/{total} começa às {etapa.Hora:HH\\:mm}.[/] [grey]deixe o console "
            + "aberto; o PC não vai suspender enquanto o lote roda.[/]");
        for (var falta = alvo - DateTime.Now; falta > TimeSpan.Zero; falta = alvo - DateTime.Now)
        {
            Console.Write($"\r   começa em {falta:hh\\:mm\\:ss}  (Ctrl+C interrompe)   ");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        Console.Write("\r" + new string(' ', 48) + "\r");
    }

    /// <param name="segurar">Vale para ESTE lote, e por isso é parâmetro em vez de ler o campo direto.
    /// O ajuste do operador (o <c>10</c>) diz o que ele QUER; este parâmetro diz o que é confiável
    /// agora, depois da sondagem do espelho. Ver <see cref="SegurarValeNesteLoteAsync"/>.</param>
    private async Task<ResumoDoLote> DispararAsync(
        List<(Contato Contato, int Variante, string Texto)> plano,
        string log,
        string serial,
        int cota,
        int intervaloMin,
        int intervaloMax,
        bool segurar,
        DiarioDoLote diario,
        CancellationToken ct)
    {
        var enviados = 0;
        var falhas = 0;
        var semConta = 0;
        // Subconjunto de `falhas`, ao lado do `semConta` e pela mesma razão: os três baldes mandam o
        // operador para lugares DIFERENTES. Sem separar o incerto, "restam N na lista" não tem como
        // distinguir quem nunca recebeu de quem talvez já tenha recebido.
        var incertas = 0;
        var entregasConfirmadas = 0;

        // 🔴 SEPARADO DE `enviados` porque a pergunta é outra. `enviados` é o que SAIU, e vai pro
        // relatório. Este é o que a conta GASTOU, e inclui o não confirmado: ali a conversa foi aberta e
        // a mensagem pode ter saído, então cobrar é o lado seguro. É também a mesma regra que o LerLog
        // aplica ao reler o CSV ("sim" ou "incerto"), e as duas contagens precisam concordar: se o lote
        // não cobrasse o incerto e a releitura cobrasse, a cota de amanhã sairia menor sem explicação.
        var cotaGasta = 0;
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

        // 🔴 QUEM O APP NEGOU NAS DUAS FORMAS. Sai da lista no fim do lote, e é a ÚNICA coisa que sai
        // dela sem ter recebido mensagem. Antes ninguém saía: um número inexistente voltava em todo lote
        // futuro, abrindo conversa atrás de conversa contra ninguém — que é exatamente o padrão de bot
        // enumerando números que o comentário do `tentados` diz querer evitar, só que espalhado ao longo
        // de dias em vez de dentro de um lote.
        // Falha de APARELHO não entra aqui, e essa é a linha que importa: tela travada numa terça não diz
        // nada sobre o número na quarta, e tirar esses da lista jogaria fora gente boa em silêncio.
        // Lista de CANDIDATOS: quem de fato sai é decidido no finally, contra o lote inteiro.
        var candidatos = new List<ContatoSuspenso>();

        // A agenda NÃO confirmou que têm WhatsApp. Só são segurados com `segurar` ligado; por padrão a
        // lista existe pra medir o quanto o espelho erra, comparando com quem de fato entregou.
        var naoConfirmados = new List<string>();
        var confirmados = 0;

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
                    // 🔴 QUEM SOBREPÕE QUEM, dito na hora em que a regra é aplicada. A janela vence a
                    // agenda, e a palavra "execução" mentia quando havia etapas: o que acaba aqui é a
                    // ETAPA, e as seguintes continuam esperando a hora delas. Quem lesse "execução
                    // encerrada" às 22h fecharia a janela do console achando que o dia tinha acabado.
                    AnsiConsole.MarkupLine(
                        _agenda.Count > 0
                            ? $"[blue]{DateTime.Now:HH\\:mm} está fora da janela ({JanelaDescrita()}): "
                              + $"esta etapa não manda nada.[/] [grey]a janela do[/] [bold]8[/] "
                              + "[grey]manda sobre a hora do[/] [bold]5[/][grey]. os "
                              + $"{plano.Count - i} contato(s) ficam na lista, e as etapas seguintes "
                              + "continuam valendo.[/]"
                            : $"[blue]fora da janela de envio ({JanelaDescrita()}): execução "
                              + $"encerrada.[/] [grey]{plano.Count - i} contato(s) ficam na lista para o "
                              + "próximo dia. abra a janela no[/] [bold]8[/] [grey]se quiser rodar em "
                              + "qualquer horário.[/]");
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

                // 🔴 PERGUNTA À AGENDA ANTES DE GASTAR O DISPARO. O espelho `vnd.com.whatsapp.profile`
                // que o próprio WhatsApp publica na agenda do Android responde "este número é usuário?"
                // sem abrir conversa, sem consumir tentativa e sem tocar no destinatário. O
                // DispatchEngine já faz exatamente isto antes de enviar; o console descobria do jeito
                // caro, abrindo a conversa e lendo o diálogo de recusa.
                //
                // `IsOnWhatsAppAsync` NUNCA devolve false, só true ou null — doutrina do projeto ("quando
                // errar é caro, 'não sei' precisa caber no tipo"). Então null NÃO é veredito de número
                // morto: é "não sei", e pode ser só o sync que ainda não rodou. Por isso o contato é
                // SEGURADO e continua na lista, jamais descartado. O motor resolve o mesmo dilema
                // adiando, pela mesma razão.
                //
                // NÃO grava na agenda quem não passou: gravar 87 contatos em rajada é escrita que sobe
                // pro Google e é justamente o que o comentário da espera do motor manda evitar. Quem
                // precisa entrar na agenda entra pelo `gravar`, que existe pra isso, avisa e confirma.
                var confirmadoPelaAgenda = await phone.IsOnWhatsAppAsync(contato.Numero, ct) is true;
                if (confirmadoPelaAgenda)
                {
                    confirmados++;
                }
                else
                {
                    naoConfirmados.Add(contato.Numero);
                    if (segurar)
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey]({i + 1}/{plano.Count}) segurado[/] {contato.Numero} "
                            + "[grey]— a agenda não confirma que este número tem WhatsApp. nada foi "
                            + "aberto e nenhuma tentativa foi gasta.[/]");
                        continue;
                    }
                }

                tentados.Add(contato.Numero);
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
                // 🔴 CONTA RESTRITA CANCELA A SEGUNDA CHANCE. Ela existe pra descobrir se o número está
                // na outra forma do 9º dígito — pergunta que só faz sentido se o app CONSEGUE responder.
                // Com a conta restrita nenhuma forma funciona, e insistir seria só mais uma conversa
                // aberta contra um chip já sob restrição. MEDIDO em 2026-08-10: 22 dos 23 fracassos
                // daquele lote pagaram essa tentativa extra à toa.
                var numeroUsado = contato.Numero;
                if (!r.Sent && !r.Uncertain && !r.ContaRestringida
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
                        // Incondicional: isto não é o "gravar antes de enviar" (que saiu, ver o remarks
                        // do `gravar`), é CORREÇÃO de um registro que está envenenando a agenda. Deixar
                        // isso opcional seria permitir que o operador desligue o conserto do próprio
                        // estrago.
                        var corrigido = await phone.SaveContactAsync(alternativo, contato.Nome, ct);
                        AnsiConsole.MarkupLine(
                            $"[grey]agenda[/] {alternativo}: {corrigido.EscapeMarkup()} "
                            + "[grey](a forma que funciona)[/]");
                    }
                }

                // 🔴 EM MODO OBSERVAÇÃO. A contradição (app recusa um número que o espelho dele mesmo
                // marca como usuário) vai pro CSV e NÃO muda comportamento nenhum. O detector tem zero
                // lotes de histórico, e o operador relatou casos reais de várias falhas seguidas em chip
                // SAUDÁVEL, com o envio voltando ao normal depois. Ligar ação a um sinal não validado,
                // numa operação onde o falso positivo é comprovadamente comum, é o erro que a parada
                // dura já cometeu uma vez aqui.
                //
                // A coluna é o que permite responder por DADO, depois de alguns lotes: se a contradição
                // só aparece nos lotes de fato restritos, o sinal serve e aí ele ganha o volante; se
                // aparece nos saudáveis também, ele é insuficiente sozinho. É a mesma disciplina que o
                // DispatchEngine aplica ao guard de entrega no caminho de UI, mantido desligado até
                // haver distribuição observada.
                var contradito = false;

                // 🔴 PARA NA HORA, e desta vez a parada é justificada: não é inferência por sequência
                // de falhas, é o PRÓPRIO APP declarando na tela que a conta está restringida.
                //
                // MEDIDO em 2026-08-10: o lote entregou 30, o chip foi restringido no meio, e seguiu
                // por mais 33 contatos abrindo conversa sem nenhuma chance de entrega — cada um com a
                // segunda chance dobrando as aberturas, contra um chip JÁ sob restrição. Foi o operador
                // que teve de olhar o log e entender. Nada disso pode se repetir.
                //
                // Nem tenta a outra forma do número: com a conta restrita nenhuma forma funciona, e a
                // segunda tentativa seria só mais uma conversa aberta.
                if (r.ContaRestringida)
                {
                    Registrar(log, serial, contato, variante, texto, r, contradito: false);
                    AnsiConsole.MarkupLine(
                        $"[red]({i + 1}/{plano.Count}) LOTE INTERROMPIDO: o WhatsApp declarou na tela "
                        + $"que ESTA CONTA está restringida.[/] [grey]{(r.Error ?? "").EscapeMarkup()}[/]");
                    AnsiConsole.MarkupLine(
                        "[yellow]não é o número nem o aparelho, é o chip.[/] [grey]enquanto durar, "
                        + "nenhuma mensagem sai para ninguém. os contatos que faltam continuam na "
                        + "lista, então nada se perdeu ao parar. NÃO dispare deste chip até normalizar: "
                        + "insistir é o que transforma restrição temporária em banimento.[/]");
                    break;
                }

                if (r.Sent)
                {
                    enviados++;
                    cotaGasta++;
                    // Tique de entrega JÁ visível na tela. Só "delivered"/"read" contam: "sent" é o
                    // estado normal segundos depois do toque, e tratá-lo como entrega mentiria.
                    if (r.DeliveryStatus is "delivered" or "read")
                    {
                        entregasConfirmadas++;
                    }
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
                    incertas++;
                    // Gasta cota mesmo assim: a conversa foi aberta e a mensagem pode ter saído.
                    cotaGasta++;
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
                    // Contado à parte pro FECHO do lote: "0 enviada(s), 87 falha(s)" esconde que as 87
                    // são a MESMA categoria, e é justamente isso que denuncia causa comum. Quem lê só a
                    // última linha de manhã precisa enxergar o padrão sem reler 87 linhas.
                    semConta++;

                    // 🔴 CONFRONTA O APP COM O ESPELHO DELE MESMO. O deep link acabou de dizer "este
                    // número não tem conta"; o `vnd.com.whatsapp.profile` que o sync de contatos do
                    // WhatsApp publica na agenda do Android diz se ele É usuário. Duas fontes
                    // independentes sobre o mesmo fato — e a DISCORDÂNCIA entre elas responde o que
                    // nenhuma das duas responde sozinha: se o número é morto ou se a CONTA parou de
                    // resolver. É o mesmo que o operador faz à mão ao procurar o contato no celular.
                    //
                    // Custa 2 chamadas adb e SÓ na recusa, não em todo envio. E `IsOnWhatsAppAsync`
                    // nunca devolve false (só true ou null), então isto só produz evidência A FAVOR de
                    // restrição, nunca um "está tudo bem" falso.
                    var espelho = await phone.IsOnWhatsAppAsync(contato.Numero, ct);
                    contradito = espelho == true;
                    disjuntor.NoAccount(contradito);
                    AnsiConsole.MarkupLine(
                        $"[red]({i + 1}/{plano.Count}) sem conta[/] {contato.Numero}: "
                        + $"{(r.Error ?? "").EscapeMarkup()}");
                    if (contradito)
                    {
                        AnsiConsole.MarkupLine(
                            "[yellow]  ⚠ a agenda do aparelho diz que ESTE número É usuário do "
                            + "WhatsApp.[/] [grey]o app se contradisse: não é o número que está morto.[/]");
                    }
                    else
                    {
                        // 🔴 A CONTRADIÇÃO SALVA O CONTATO, e é por isso que a suspensão mora no `else`.
                        // Quando o espelho da agenda diz que o número É usuário, a recusa do app não é
                        // veredito sobre o número: é o app discordando de si mesmo, e a explicação mais
                        // provável passa a ser a CONTA, não a lista. Tirar da fila quem foi contradito
                        // seria condenar o inocente com a única prova que existe a favor dele em mãos.
                        candidatos.Add(new ContatoSuspenso(
                            numeroUsado, contato.Nome, r.Causa, DateTimeOffset.Now));
                        AnsiConsole.MarkupLine(
                            "[grey]  sai da lista no fim do lote. volta pela planilha, no[/] [bold]12[/][grey].[/]");
                    }
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
                // LerLog), então a pessoa colada amanhã na forma certa passava sem aviso e
                // podia receber a campanha de novo. A dedup DENTRO do lote já sabia que as duas formas
                // são a mesma pessoa; a de fora do lote não ficava sabendo.
                Registrar(log, serial, contato with { Numero = numeroUsado }, variante, texto, r, contradito);

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
                // 🔴 AVISA E SEGUE. Cheguei a fazer isto PARAR o lote e recuei: três recusas seguidas
                // acontecem com frequência SEM restrição nenhuma, e travar interrompia o fluxo no caso
                // comum. O aviso repete a cada 10 (ver DeveAlertarRecusas) porque com a conta restrita
                // um único alerta no 3º contato deixaria os 84 seguintes em silêncio.
                //
                // O aparelho NÃO consegue distinguir "lista ruim" de "conta restringida": a restrição
                // quebra a resolução de número NOVO, e nenhuma sonda no celular passa por esse caminho
                // (conversa já existente abre normal; número nunca contatado esbarra no cache local).
                // Quem tem essa informação é o operador, olhando o WhatsApp Web — por isso o texto
                // manda começar por lá, e por isso a decisão de parar fica com ele.
                // 🔴 CERTEZA, e não suspeita. Duas contradições sem nenhuma entrega no meio: o app negou
                // dois números que o espelho dele mesmo marca como usuários. Isso não é lista fria, e
                // não depende de limiar estatístico nenhum. Sai na hora, fora da cadência do aviso comum.
                if (disjuntor.AcabouDeConfirmarContradicao)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]CONTA MUITO PROVAVELMENTE RESTRINGIDA.[/] [grey]{disjuntor.ConsecutiveContradicoes} "
                        + "números recusados que a agenda do aparelho marca como usuários do WhatsApp. "
                        + "Duas fontes do próprio app discordando: o número não é o problema, a conta "
                        + "parou de resolver.[/]");
                    AnsiConsole.MarkupLine(
                        "[yellow]enquanto isso durar NADA será entregue, para ninguém.[/] [grey]o lote "
                        + "continua e retoma sozinho assim que a conta voltar (a primeira entrega zera "
                        + "tudo). mas insistir com a conta sob restrição é o que transforma restrição "
                        + "temporária em banimento —[/] Ctrl+C [grey]interrompe, e[/] parar 5 "
                        + "[grey]faz parar sozinho da próxima vez.[/]");
                }
                else if (disjuntor.DeveAlertarRecusas)
                {
                    // 🔴 O MESMO NÚMERO DE RECUSAS, DOIS ALARMES DIFERENTES. Três recusas depois de
                    // entregas provam que a conta resolve número: a causa provável são aqueles três
                    // números, e gritar alto aí é o falso positivo que atrapalhava o operador. Três
                    // recusas com ZERO entregas no lote inteiro é outra coisa — nada resolveu, nem uma
                    // vez. O dado já existia (ver TotalDelivered) e ninguém lia.
                    if (disjuntor.SuspeitaRecaiSobreAConta)
                    {
                        // Duas portas de entrada aqui, e a segunda é a que faltava: ou o lote NUNCA
                        // entregou, ou ele entregou e a sequência de recusas continuou crescendo depois.
                        // No segundo caso as entregas antigas não explicam o presente — a restrição pode
                        // ter começado no meio do lote.
                        var abertura = disjuntor.NadaEntregouAinda
                            ? $"{disjuntor.ConsecutiveNoAccount} recusas e NENHUMA entrega neste lote"
                            : $"{disjuntor.ConsecutiveNoAccount} recusas SEGUIDAS, mesmo com "
                              + $"{disjuntor.TotalDelivered} entrega(s) antes";
                        AnsiConsole.MarkupLine(
                            $"[red]atenção: {abertura}.[/] [grey]a suspeita não é mais \"esses números "
                            + "morreram\": é algo comum a todos. A causa mais séria é a CONTA "
                            + "restringida, que para de resolver número e faz todo contato voltar como "
                            + "\"sem conta\", inclusive quem existe e está na agenda.[/]");
                        AnsiConsole.MarkupLine(
                            "[grey]confira: abra o WhatsApp no celular e procure um desses contatos. Se "
                            + "ele existe lá, não é a lista. A tela salva que o erro aponta mostra o que "
                            + "o app respondeu de verdade. (Com poucos chips, o WhatsApp Web é onde a "
                            + "restrição aparece — o aparelho costuma não mostrar.)[/]");
                        AnsiConsole.MarkupLine(
                            "[grey]o lote CONTINUA.[/] Ctrl+C [grey]interrompe agora, e[/] parar 5 "
                            + "[grey]faz parar sozinho da próxima vez. insistir com a conta sob "
                            + "restrição é o que transforma restrição temporária em banimento.[/]");
                    }
                    else
                    {
                        // Brando, e no PRETÉRITO de propósito: "resolveu" e não "resolve". A prova é de
                        // antes, e afirmar no presente é o que faria esta linha tranquilizar um chip
                        // que acabou de cair. Se a sequência crescer, o ramo de cima assume.
                        AnsiConsole.MarkupLine(
                            $"[yellow]{disjuntor.ConsecutiveNoAccount} números seguidos recusados como "
                            + $"\"sem conta\".[/] [grey]este lote já entregou "
                            + $"{disjuntor.TotalDelivered}, então a conta resolveu número há pouco: a "
                            + "causa provável são esses números mesmo. o lote continua, e se a "
                            + "sequência crescer eu aviso de novo com mais peso.[/]");
                    }
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

                // 🔴 A COTA SE GASTA AQUI, e não recortando o plano lá atrás. Contar CONTATOS fazia
                // falha, contato segurado e duplicata pulada consumirem cota sem nada sair. A cota
                // protege a conta contra CONVERSA ABERTA, e recusa não abre conversa nenhuma.
                // Depois da parada por falha, de propósito: chegar na cota é fim normal e não deve
                // mascarar um lote que estava sendo interrompido por problema.
                if (cota > 0 && cotaGasta >= cota)
                {
                    AnsiConsole.MarkupLine(
                        $"[blue]cota de {cota} envio(s) completa.[/] [grey]{plano.Count - (i + 1)} "
                        + "contato(s) ficam na lista para a próxima execução.[/]");
                    break;
                }

                // 🔴 A PAUSA LONGA ENTRE BLOCOS SAIU em 2026-08-20, por decisão do operador ("sem
                // pausas"). Ela mandava um punhado, sumia por ~30 min e voltava, para o lote não ter a
                // regularidade de máquina. Quem faz esse trabalho agora é a AGENDA: etapas com hora
                // marcada produzem o mesmo desenho (punhado, silêncio longo, punhado), com a diferença
                // de que os intervalos são escolhidos por quem opera em vez de sorteados. O que a agenda
                // NÃO cobre é o lote sem agenda, e ali o espaçamento é o ritmo entre mensagens.
                if (i < plano.Count - 1)
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
                    var (min, max) = r.Sent || emSequencia
                        ? (intervaloMin, intervaloMax)
                        : (FalhaEsperaMin, FalhaEsperaMax);
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

            // 🔴 NO FINALLY, junto do resto: um Ctrl+C no meio do lote não pode deixar a suspensão pela
            // metade. Os números já foram apurados contato a contato lá em cima; aqui só se aplica.
            //
            // 🔴 E AQUI, NÃO NA HORA, POR CAUSA DA GUARDA ABAIXO: a decisão de tirar um contato da lista
            // depende do LOTE INTEIRO, não daquele contato. Aplicada contato a contato ela não teria como
            // enxergar o padrão que a invalida.
            diario.Corrigidos.AddRange(corrigidos.Select(c => new CorrecaoDeNumero(c.De, c.Para)));
            diario.AgendaConfirmou = confirmados;
            diario.AgendaNaoConfirmou = naoConfirmados.Count;

            if (candidatos.Count > 0)
            {
                // 🔴 A GUARDA QUE IMPEDE A LISTA INTEIRA DE SUMIR. A regra mora no Core (FaxinaDaLista)
                // porque é a única decisão DESTRUTIVA do console, e uma decisão dessas não pode viver
                // como um `if` no meio de um laço que este projeto não tem como rodar em teste.
                var tentativas = enviados + falhas;
                if (!FaxinaDaLista.PodeSuspender(tentativas, semConta))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]nenhum contato foi tirado da lista.[/] [grey]"
                        + $"{FaxinaDaLista.MotivoDaRecusa(tentativas, semConta)} confira um deles à mão "
                        + "no aparelho antes de disparar de novo.[/]");
                }
                else
                {
                    // Só AQUI o candidato vira suspenso de verdade, e é por isso que o diário é
                    // preenchido neste ponto: a planilha tem que listar quem SAIU, não quem quase saiu.
                    Suspender(candidatos);
                    diario.Suspensos.AddRange(candidatos);
                    Salvar(serial);
                    AnsiConsole.MarkupLine(
                        $"[yellow]{candidatos.Count} contato(s) SAÍRAM da lista.[/] [grey]o WhatsApp "
                        + "negou estes números em todas as formas do 9º dígito que existem para eles. "
                        + "insistir só abriria conversa contra número inexistente, lote após lote. eles "
                        + "estão na planilha, na aba[/] [bold]Suspensos[/][grey], com o motivo e a última "
                        + "tentativa de cada um. para trazer de volta:[/] [bold]12[/]");
                    MostrarAlguns(candidatos.Count, 10,
                        i => $"  [red]{candidatos[i].Numero}[/] {(candidatos[i].Nome ?? "").EscapeMarkup()}");
                }

                // 🔴 O QUE FICOU É O QUE FALHOU MENOS O QUE SAIU, e não "falhas de aparelho". A conta
                // por categoria esquecia dois grupos que também ficam: o incerto (pode ter recebido) e
                // o contradito (o app negou, a agenda desmentiu). Contar por subtração não tem como
                // esquecer categoria nenhuma, porque não enumera categoria.
                // Conta contra o DIÁRIO e não contra os candidatos: quando a guarda acima segura a
                // suspensão, ninguém saiu, e todos os que falharam continuam na lista.
                var ficaram = falhas - diario.Suspensos.Count;
                if (ficaram > 0)
                {
                    var recorte = incertas == 0 ? "" : $", {incertas} deles NÃO CONFIRMADO(s)";
                    AnsiConsole.MarkupLine(
                        $"[grey]{ficaram} contato(s) CONTINUAM na lista{recorte}: a falha não foi do "
                        + "número, então vale tentar de novo.[/]");
                }
            }

            // 🔴 REPETE NO FIM o que já foi dito na hora. Entre uma entrega e a seguinte passam 150-360s
            // de espera, então a linha "corrija na origem" sai da tela muito antes de o lote acabar — e
            // ela é justamente a única que gera trabalho FORA daqui. Sem esta lista, a correção depende
            // de o operador ter visto passar, e a mesma lista volta amanhã com o mesmo número errado,
            // pagando de novo a tentativa perdida.
            // 🔴 SEGURADO NÃO É DESCARTADO, e a diferença precisa aparecer. Estes continuam na lista, e
            // a causa mais comum é sync: contato gravado há pouco leva minutos até o WhatsApp publicar o
            // espelho. Sem esta linha, o operador veria o lote "pular" contatos e concluiria que perdeu.
            // 🔴 O NÚMERO QUE INTERESSA MEDIR. Compare com quantos de fato entregaram: se o espelho
            // confirma quase todo mundo que entrega, ele é confiável NESTE aparelho e vale ligar o
            // `segurar` pra parar de gastar disparo em número morto. Se ele deixa de confirmar gente que
            // entrega, é fraco demais pra decidir — e foi por isso que ele NÃO segura por padrão.
            if (naoConfirmados.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]agenda confirmou WhatsApp em {confirmados} de "
                    + $"{confirmados + naoConfirmados.Count}."
                    + (_segurarNaoConfirmados
                        ? $" os {naoConfirmados.Count} não confirmados foram SEGURADOS e continuam na lista."
                        : $" os {naoConfirmados.Count} não confirmados foram tentados assim mesmo "
                          + "(ligue com[/] segurar [grey]pra não gastar disparo com eles).")
                    + "[/]");
                // 🔴 A CAUSA MAIS COMUM DE MUITOS NÃO CONFIRMADOS É SYNC, NÃO NÚMERO MORTO, e o texto
                // tinha deixado de dizer isso. O fluxo recomendado é `gravar`, esperar, disparar — e o
                // WhatsApp leva de 2,5 a 7 minutos (medido em 2026-07-23) pra publicar a marca na
                // agenda. Quem gravou e disparou em seguida vê a lista inteira segurada e conclui que o
                // sistema quebrou, quando faltou esperar.
                if (naoConfirmados.Count > confirmados)
                {
                    // 🔴 AS DUAS CAUSAS, e a primeira é caminho SEM SAÍDA se não for dita. Desde que o
                    // toggle "gravar antes de enviar" saiu, NADA no `enviar` põe contato na agenda. Quem
                    // colar uma lista nova e disparar direto, com `segurar` ligado, vê todo mundo
                    // segurado — e continuaria vendo pra sempre, porque nenhum lote grava. Dizer só
                    // "espere o sync" mandaria o operador esperar por algo que nunca vai acontecer.
                    AnsiConsole.MarkupLine(
                        "[yellow]mais da metade não confirmada.[/] [grey]duas causas possíveis: (1) você "
                        + "ainda não rodou o[/] gravar [grey]— sem ele os contatos NÃO entram na agenda "
                        + "do celular e vão continuar segurados; (2) você gravou há pouco e é sync, que "
                        + "na nossa medição levou de 2,5 a 7 min e pode demorar mais. se já gravou e "
                        + "esperou, aí sim provavelmente não têm conta: confira a forma deles com[/] "
                        + "[bold]3[/][grey].[/]");
                }
                MostrarAlguns(naoConfirmados.Count, 10, i => $"  [grey]{naoConfirmados[i]}[/]", " não confirmado(s)");
            }

            if (corrigidos.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{corrigidos.Count} contato(s) só entregaram na OUTRA forma do número. "
                    + "corrija na origem:[/]");
                MostrarAlguns(corrigidos.Count, 10,
                    i => $"  {corrigidos[i].De} [yellow]→[/] [bold]{corrigidos[i].Para}[/]");
            }
        }

        // Última linha do lote: daqui pra trás tudo passou, então ele NÃO foi interrompido. Ver o
        // comentário do DiarioDoLote sobre por que a prova é positiva.
        diario.Interrompido = false;
        return new ResumoDoLote(enviados, falhas, semConta, entregasConfirmadas);
    }

    /// <summary>Tira da fila e põe na quarentena, sem apagar.</summary>
    /// <remarks>
    /// Compara pelas DUAS formas do 9º dígito, igual ao resto do lote: o número suspenso é o que de fato
    /// foi tentado, e a lista pode ter o irmão dele. Deixar o irmão na fila devolveria o mesmo contato
    /// morto no lote seguinte, e a suspensão teria sido teatro.
    /// </remarks>
    private void Suspender(IReadOnlyList<ContatoSuspenso> mortos)
    {
        var alvos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in mortos)
        {
            alvos.Add(m.Numero);
            if (BrazilPhoneValidator.AlternateBrazilianForm(m.Numero) is { } irmao)
            {
                alvos.Add(irmao);
            }
        }

        // Guarda o CONTATO que estava na lista (com o nome dele), e não o número solto: é ele que volta
        // se o operador mandar devolver.
        _suspensos.AddRange(_contatos.Where(c => alvos.Contains(c.Numero)));
        _contatos.RemoveAll(c => alvos.Contains(c.Numero));
    }

    /// <summary>O histórico deste aparelho e o que ele sugere para hoje, antes do "confirmar".</summary>
    /// <remarks>
    /// 🔴 MOSTRA, NUNCA CORTA. Decisão explícita do operador em 2026-08-11, e coerente com o resto do
    /// console. O sistema não tem sinal de resposta nem de bloqueio, que é o que de fato governa a
    /// punição do WhatsApp, então decidir volume por ele seria decidir com meio dado.
    ///
    /// <para>🔴 OS NÚMEROS CRUS VÊM ANTES DA SUGESTÃO, de propósito: dá pra discordar da sugestão
    /// usando o mesmo dado que a gerou. Sugestão sozinha vira ordem disfarçada, e ordem que o operador
    /// não pode auditar é ordem que ele ignora na primeira vez que ela atrapalha.</para>
    ///
    /// <para>O caso que motivou: em 2026-08-10 o lote mandou 30 num chip novo e ele foi restringido.
    /// Nada na tela dizia quantos dias aquele chip tinha nem quanto ele havia feito antes — e o CSV
    /// sabia das duas coisas.</para>
    /// </remarks>
    private void MostrarPainelDoChip(int tamanhoDoLote, ResumoDoLog log)
    {
        var (dias, ultimo, enviadasHoje) = (log.DiasAtivos, log.UltimoFechado, log.EnviadasHoje);
        var s = ChipHistory.Sugerir(dias, ultimo, log.DiasDesdeUltimoDia);

        // "há N dias" na linha do histórico, e não só na do motivo: os dias de disparo NÃO são dias de
        // calendário (quem pula dias não regride), então sem a lacuna à vista "6 dias de disparo" pode
        // tanto ser a semana passada quanto seis dias espalhados por meio ano.
        var quando = log.DiasDesdeUltimoDia switch
        {
            <= 0 => "",
            1 => " (ontem)",
            var n => $" (há {n} dias)",
        };
        var historico = ultimo is null
            ? (dias == 0
                ? "[yellow]sem histórico de envio neste aparelho[/]"
                : $"[bold]{dias}[/] dia(s) de disparo, mas nenhum dia FECHADO ainda")
            : $"[bold]{dias}[/] dia(s) de disparo · último dia fechado{quando}: "
              + $"[bold]{ultimo.Enviadas}[/] enviada(s), {ultimo.Recusadas} recusa(s)"
              + (ultimo.Enviadas > 0
                  ? $", entrega confirmada em {ultimo.EntregasConfirmadas} de {ultimo.Enviadas}"
                  : "");

        var fase = s.Fase switch
        {
            FaseDoChip.Novo =>
                $"[red]NOVO[/] [grey](até {ChipHistory.DiasChipNovo} dias de disparo — período de "
                + "risco máximo para um número)[/]",
            FaseDoChip.Aquecendo =>
                $"[yellow]AQUECENDO[/] [grey](maduro a partir de {ChipHistory.DiasChipMaduro} dias)[/]",
            _ => "[green]MADURO[/] [grey](pode operar no platô)[/]",
        };

        AnsiConsole.MarkupLine($"[grey]chip:[/] {fase}");
        AnsiConsole.MarkupLine($"[grey]histórico:[/] {historico}");
        // 🔴 DESCONTA O QUE JÁ SAIU HOJE. Sem isto, o segundo lote do dia recomeçaria a cota do zero.
        var resta = Math.Max(0, s.Sugestao - enviadasHoje);
        AnsiConsole.MarkupLine(
            $"[grey]sugere ~[/][bold]{s.Sugestao}[/][grey] mensagem(ns) para HOJE. {s.Motivo}.[/]");
        if (enviadasHoje > 0)
        {
            AnsiConsole.MarkupLine(
                resta == 0
                    ? $"[yellow]já saíram {enviadasHoje} hoje deste aparelho: a sugestão do dia já foi "
                      + "alcançada.[/]"
                    : $"[grey]já saíram [/][bold]{enviadasHoje}[/][grey] hoje deste aparelho, então "
                      + $"restariam ~[/][bold]{resta}[/][grey].[/]");
        }
        AnsiConsole.MarkupLine($"[grey]seu lote tem [/][bold]{tamanhoDoLote}[/][grey] contato(s).[/]");

        // 🔴 VOLUME E INTERVALO JUNTOS, sempre. Sugerir volume baixo e deixar o intervalo do platô
        // despacha o dia inteiro numa hora, que é o padrão que o volume baixo tentava evitar.
        // Cota do dia já alcançada: sugerir intervalo pra espalhar zero mensagem seria ruído, e ainda
        // por cima confundiria com a linha logo acima, que acabou de dizer que não resta nada.
        if (resta == 0 && enviadasHoje > 0)
        {
            return;
        }

        // Espalha o que RESTA do dia, não a cota cheia: se metade já saiu, o resto tem a janela toda.
        var paraEspalhar = Math.Max(1, resta);
        var (im, ix) = ChipHistory.IntervaloPara(paraEspalhar, _horaFim - _horaInicio);
        AnsiConsole.MarkupLine(
            $"[grey]para espalhar ~{paraEspalhar} pela janela, o ritmo seria[/] "
            + $"[bold]{EmMinutos(im)} a {EmMinutos(ix)} min[/] [grey]entre mensagens; o seu é "
            + $"{RitmoDescrito()}.[/]");
        AnsiConsole.MarkupLine(
            "[grey]é sugestão, não limite: nada será cortado. para aplicar:[/] "
            + $"teto {paraEspalhar} [grey]e[/] intervalo {EmMinutos(im)} {EmMinutos(ix)}");
    }

    /// <summary>Os três sinais de risco que o console consegue enxergar antes de disparar.</summary>
    /// <remarks>
    /// 🔴 CADA LINHA CORRESPONDE A UM SINAL MEDIDO por fontes de 2026, não a palpite:
    /// <list type="bullet">
    /// <item>Bloqueio e denúncia acima de ~2% derrubam a qualidade da conta e cortam limites. Oferecer
    /// saída converte quem ia denunciar em quem se descadastra.</item>
    /// <item>Texto repetido em massa é sinal clássico de automação.</item>
    /// <item>Mensagem para quem não te tem salvo é o item de MAIOR peso na pontuação de spam, e lista
    /// fria é exatamente isso.</item>
    /// </list>
    /// <para>⚠️ NÃO sugere "responda SAIR". O <c>MessageComposer</c> parou de anunciar esse caminho de
    /// propósito: responder depende do inbound, e o console físico não tem nenhum. Prometer uma saída
    /// que não funciona é pior que não prometer, porque quem tenta sair e não consegue DENUNCIA. Por
    /// isso o aviso fala em LINK, que funciona sem inbound.</para>
    /// </remarks>
    private void AvisarRiscoDoLote(int tamanhoDoLote)
    {
        if (!_textos.Any(t => t.Contains("http", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine(
                "[yellow]nenhum template oferece saída.[/] [grey]bloqueio e denúncia são o que derruba "
                + "a conta, e quem não acha como sair denuncia. cole um LINK de descadastro no texto "
                + "(não peça \"responda SAIR\": sem inbound, a resposta não chega a lugar nenhum e a "
                + "pessoa denuncia achando que foi ignorada).[/]");
        }

        if (_textos.Count > 0 && tamanhoDoLote / _textos.Count >= 20)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{_textos.Count} template(s) para {tamanhoDoLote} contatos.[/] [grey]texto "
                + "repetido em massa é sinal clássico de automação. mais variações diluem isso.[/]");
        }

        AnsiConsole.MarkupLine(
            "[grey]lembre que esta lista é FRIA: mandar para quem não te tem salvo é o item de maior "
            + "peso na pontuação de spam, e o console não sabe quem já respondeu.[/]");
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
    private void AvisarRepeticao(
        List<(Contato Contato, int Variante, string Texto)> plano,
        HashSet<string> jaReceberam,
        HashSet<string> talvezReceberam)
    {
        bool Consta(HashSet<string> conjunto, string numero) =>
            conjunto.Contains(numero)
            || (BrazilPhoneValidator.AlternateBrazilianForm(numero) is { } outra && conjunto.Contains(outra));

        // 🔴 DOIS AVISOS, E O INCERTO SAI PRIMEIRO. Os dois conjuntos se sobrepõem (todo incerto também
        // é "já enviado"), então o mesmo contato apareceria nas duas listas — e a genérica engoliria a
        // específica, que é a única com ação clara. O incerto é tirado da lista da repetição de
        // propósito.
        var duvidosos = plano.Where(p => Consta(talvezReceberam, p.Contato.Numero)).ToList();
        var repetidos = plano
            .Where(p => Consta(jaReceberam, p.Contato.Numero) && !Consta(talvezReceberam, p.Contato.Numero))
            .ToList();

        if (duvidosos.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]atenção: {duvidosos.Count} número(s) desta lista ficaram NÃO CONFIRMADOS num "
                + "lote anterior.[/] [grey]o toque em enviar aconteceu e ninguém conseguiu ler a tela, "
                + "então a mensagem PODE ter saído.[/]");
            MostrarAlguns(duvidosos.Count, 5,
                i => $"  [yellow]{duvidosos[i].Contato.Numero}[/] "
                     + $"{(duvidosos[i].Contato.Nome ?? "").EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "[yellow]abra estas conversas no aparelho antes de disparar.[/] [grey]é a única forma de "
                + "saber, e mandar de novo pra quem já recebeu é o pior desfecho com contato frio. tire "
                + "com[/] x [grey]quem já tiver recebido.[/]");
        }

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

    /// <summary>Agora está dentro da janela em que é permitido mandar mensagem?</summary>
    private bool DentroDaJanela()
    {
        var h = DateTime.Now.Hour;
        return h >= _horaInicio && h < _horaFim;
    }

    /// <summary>A hora agendada cai fora da janela permitida? Mesma conta do
    /// <see cref="DentroDaJanela"/>, aplicada a uma hora futura em vez de agora.</summary>
    /// <remarks>
    /// Um método, e não a expressão repetida nos dois lugares que a usam (a observação por etapa e o
    /// aviso do rodapé). Repetida, ela vira duas verdades independentes: bastaria alguém trocar `>=`
    /// por `>` num dos lados para a tabela marcar uma etapa que o rodapé não conta, e ninguém
    /// desconfiaria de um aviso que some.
    /// </remarks>
    private bool ForaDaJanela(TimeOnly hora) => hora.Hour < _horaInicio || hora.Hour >= _horaFim;

    /// <summary>A janela em uma linha, para o menu e o pré-voo. 0h-24h não é horário, é "sem
    /// restrição", e mostrar "0h-24h" faria o operador procurar uma limitação que não existe.</summary>
    private string TetoDescrito() =>
        _tetoAuto ? "automático (pelo histórico do chip)"
        : _teto == 0 ? "sem limite"
        : _teto.ToString(CultureInfo.InvariantCulture);

    private string JanelaDescrita() =>
        _horaInicio == 0 && _horaFim == 24 ? "qualquer horário" : $"{_horaInicio}h-{_horaFim}h";

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

    /// <summary>O ritmo entre mensagens, EM MINUTOS. Guardado em segundos, falado em minutos.</summary>
    /// <remarks>
    /// 🔴 A UNIDADE DA TELA MUDOU EM 2026-08-20, por pedido do operador, e mudou INTEIRA. Segundo é a
    /// unidade certa para o laço de disparo, que sorteia a espera, e a errada para quem decide: "150 a
    /// 360" exige uma divisão de cabeça para virar a única pergunta que interessa, que é quanto tempo o
    /// lote vai levar. O campo continua em segundos; o que virou minuto é toda entrada e toda saída.
    /// <para>Trocar a unidade em UM lugar seria pior que não trocar: o menu diria "2,5 a 6 min", o
    /// comando pediria segundos, e alguém digitaria 4 querendo 4 minutos e recebendo 4 segundos, que é
    /// justamente o intervalo de rajada que o projeto inteiro existe para evitar.</para>
    /// </remarks>
    private void Intervalo(string[] partes)
    {
        if (partes.Length < 3
            || MinutosEmSegundos(partes[1]) is not { } min
            || MinutosEmSegundos(partes[2]) is not { } max
            || max < min)
        {
            AnsiConsole.MarkupLine(
                $"[red]uso:[/] intervalo <min> <max>   [grey](em MINUTOS, aceita 2,5. atual: {RitmoDescrito()})[/]");
            AnsiConsole.MarkupLine(
                "[grey]exemplo:[/] intervalo 4 10 [grey]espera de 4 a 10 min entre mensagens, sorteada a "
                + "cada uma.[/]");
            return;
        }
        (_min, _max) = (min, max);
        AnsiConsole.MarkupLine($"ritmo entre mensagens: [bold]{RitmoDescrito()}[/].");
        AvisarRitmoCurto();
    }

    /// <summary>Os dois avisos do ritmo, compartilhados pelo comando e pelo menu.</summary>
    private void AvisarRitmoCurto()
    {
        if (_max < 60)
        {
            AnsiConsole.MarkupLine(
                "[yellow]menos de um minuto entre mensagens num chip novo é o gatilho de ban que este "
                + "projeto tenta evitar.[/]");
        }
        // 🔴 O ENGANO PREVISÍVEL DA TROCA DE UNIDADE, dito em voz alta em vez de recusado. Quem tem o
        // dedo viciado em `intervalo 240 600` vai digitar isso de novo, e em minutos aquilo são 4 horas
        // entre mensagens: um lote que parece travado, sem nenhuma mensagem de erro para explicar.
        // Recusar seria pior, porque 4 horas é um valor legítimo para quem quer espalhar o dia.
        if (_max >= 3600)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]conferindo: são {EmMinutos(_max)} MINUTOS entre mensagens[/] "
                + $"[grey](até {Duracao(TimeSpan.FromSeconds(_max))} de espera). se você quis segundos, "
                + "o console agora fala em minutos:[/] intervalo 4 10 [grey]é de 4 a 10 min.[/]");
        }
    }

    /// <summary>O ritmo em uma linha, na unidade da tela.</summary>
    private string RitmoDescrito() => $"{EmMinutos(_min)} a {EmMinutos(_max)} min";

    /// <summary>Segundos em minutos para LER: "2,5", "6", "0,5". Uma casa decimal basta.</summary>
    private static string EmMinutos(int segundos) =>
        (segundos / 60.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Minutos digitados em segundos para GUARDAR. Aceita vírgula e ponto.</summary>
    /// <remarks>
    /// 🔴 O TETO DE 24 HORAS NÃO É POLÍTICA, É ARITMÉTICA. Sem ele, "999999999" vira uma multiplicação
    /// que não cabe em <c>int</c>, e o cast em C# não estoura: ele TRUNCA em silêncio e devolve um
    /// número negativo. Um ritmo negativo passaria pela validação de max >= min, seria gravado no
    /// disco e reapareceria como espera absurda no meio do lote. O limite recusa o que não tem
    /// representação, e não o que é grande demais para o gosto de alguém: 24h de espera entre
    /// mensagens continua aceito, e é mais do que qualquer janela de envio comporta.
    /// </remarks>
    private static int? MinutosEmSegundos(string texto) =>
        double.TryParse(
            texto.Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var minutos)
            && minutos >= 0
            && minutos <= 24 * 60
            ? (int)Math.Round(minutos * 60)
            : null;

    /// <summary>Teto AUTOMÁTICO: a cota do dia sai do histórico do próprio chip, a cada lote.</summary>
    /// <remarks>
    /// 🔴 A ÚNICA COISA NESTE CONSOLE QUE CORTA SOZINHA, e ela existe porque o operador PEDIU
    /// explicitamente em 2026-08-11 ("vamos implementar isso para agendar nosso cronograma"). A regra
    /// da casa continua sendo sugerir e deixar decidir; aqui a decisão foi tomada uma vez, por comando,
    /// e vale pros lotes seguintes até ele desligar com um `teto N` qualquer.
    ///
    /// <para>É o "cronograma" sem cronograma: não há tabela de dias em lugar nenhum. Cada lote recalcula
    /// a partir do que AQUELE aparelho fez, então um chip que vai bem cresce e um que apanhou encolhe,
    /// sem ninguém manter planilha.</para>
    ///
    /// <para>Desconta o que já saiu HOJE, senão o segundo lote do dia recomeçaria a cota do zero — que
    /// é o mesmo defeito que o painel já teve e que custou uma revisão pra achar.</para>
    /// </remarks>
    private bool _tetoAuto;

    /// <summary>Marca da conta vista no último lote, e desde quando ela vale.</summary>
    /// <remarks>
    /// 🔴 O HISTÓRICO É DO APARELHO; O QUE O WHATSAPP PUNE É A CONTA. Todo arquivo deste console é
    /// indexado pelo serial do adb, e a classe que sugere volume se chama <c>ChipHistory</c> — mas nada
    /// sabia QUAL conta produziu aquele histórico. Enquanto ninguém troca, os dois coincidem e a
    /// diferença não aparece. No dia da troca, uma conta registrada hoje herdava 20 dias de maturidade e
    /// recebia sugestão de volume alto justamente nos primeiros dias, que é o período de risco máximo.
    /// É a única falha desta área que errava para MAIS.
    ///
    /// <para>Trocar o SIM sem registrar de novo não muda nada, e está certo: a reputação está no número
    /// registrado, não no chip da bandeja. Por isso a marca vem da conta, não do SIM.</para>
    ///
    /// <para>Quando o aparelho não sabe responder (<c>null</c>), nada é presumido — resta o
    /// <c>chip novo</c>, que é manual e existe justamente para esse caso.</para>
    /// </remarks>
    private string? _conta;
    private string? _chipDesde;

    /// <summary>Marca que a conta deste aparelho recomeçou hoje. O histórico anterior fica no CSV mas
    /// para de contar para o aquecimento.</summary>
    private void ChipNovo(string serial)
    {
        _chipDesde = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _conta = null;   // a próxima leitura registra a conta nova sem acusar troca de novo
        Salvar(serial);
        AnsiConsole.MarkupLine(
            $"[green]chip novo marcado em {_chipDesde}.[/] [grey]o aquecimento recomeça do zero: o que "
            + "está no CSV antes desta data continua gravado, mas não conta mais como histórico deste "
            + "chip. rode[/] chip novo [grey]só quando REGISTRAR outra conta no WhatsApp; trocar o SIM "
            + "sem registrar de novo mantém a mesma conta e o histórico continua valendo.[/]");
    }

    /// <summary>Confere se a conta registrada mudou desde o último lote. Silencioso quando não mudou ou
    /// quando o aparelho não sabe responder.</summary>
    private async Task ConferirContaAsync(string serial, CancellationToken ct)
    {
        var atual = await phone.IdentidadeDaContaAsync(ct);
        if (atual is null)
        {
            return;
        }
        if (_conta is null)
        {
            // Primeira vez que este console consegue ler a conta. Registra sem acusar troca: não há com
            // o que comparar, e chamar isso de "mudou" zeraria o histórico de todo mundo uma vez.
            _conta = atual;
            Salvar(serial);
            return;
        }
        if (string.Equals(_conta, atual, StringComparison.Ordinal))
        {
            return;
        }

        _conta = atual;
        _chipDesde = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Salvar(serial);
        AnsiConsole.MarkupLine(
            "[yellow]a conta registrada neste aparelho MUDOU desde o último lote.[/] [grey]o aquecimento "
            + "recomeça do zero: histórico de outra conta não diz nada sobre esta, e conta recém-"
            + "registrada tem risco máximo nos primeiros dias. o CSV antigo continua no disco.[/]");
    }

    private void Teto(string[] partes)
    {
        // 🔴 DEFINIR COTA APAGA A AGENDA, e é dito em voz alta. As duas respondem "quantos saem nesta
        // execução", e a regra escrita é que a agenda ganha: sem esta linha, digitar `teto 30` com uma
        // agenda de pé não faria nada, e o console mostraria a cota nova enquanto obedecia a agenda
        // velha. Estado que a tela mostra e o lote ignora é a pior das duas opções.
        void ApagarAgenda()
        {
            if (_agenda.Count == 0)
            {
                return;
            }
            AnsiConsole.MarkupLine(
                $"[yellow]a agenda de {_agenda.Count} etapa(s) foi apagada.[/] [grey]cota e agenda "
                + "respondem a mesma pergunta, e agora quem manda é a cota.[/]");
            _agenda.Clear();
        }

        if (partes.Length >= 2 && string.Equals(partes[1].Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            ApagarAgenda();
            _tetoAuto = true;
            _teto = 0;
            AnsiConsole.MarkupLine(
                "teto: [bold]automático[/] [grey]— a cada lote, a cota sai do histórico deste aparelho "
                + "(cresce em dia limpo, encolhe em dia com muita recusa) e desconta o que já saiu hoje. "
                + "o pré-voo mostra o número antes de você confirmar. para voltar ao manual, use[/] "
                + "teto <n>[grey].[/]");
            return;
        }

        if (partes.Length < 2 || !int.TryParse(partes[1], out var n) || n < 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]uso:[/] teto <n>   [grey]ou[/] teto auto   (atual: {TetoDescrito()})");
            AnsiConsole.MarkupLine(
                "[grey]quantas mandar NESTA execução; o resto fica na lista.[/] 0 [grey]= sem limite, "
                + "manda a lista inteira.[/] auto [grey]= deriva do histórico do chip a cada lote.[/]");
            return;
        }
        ApagarAgenda();
        _tetoAuto = false;
        _teto = n;
        AnsiConsole.MarkupLine(
            _teto == 0
                ? "teto: [bold]sem limite[/] [grey](manda a lista inteira)[/]."
                : $"cota por execução: [bold]{_teto}[/] [grey](o resto fica na lista)[/].");
    }

    /// <summary>A janela pelo menu: pergunta em vez de exigir a sintaxe do comando.</summary>
    /// <remarks>
    /// 🔴 GANHOU LINHA PRÓPRIA em 2026-08-20. Ela morava dentro do "blocos, pausa e janela", aparecia
    /// na coluna do estado e não era perguntada em lugar nenhum: para mexer no horário era preciso
    /// descobrir sozinho que existia um comando `janela 8 22`. Ajuste anunciado no painel e
    /// inalcançável pelo painel é pior que ajuste escondido, porque quem lê acredita que já pode mexer.
    /// </remarks>
    private void JanelaInterativa()
    {
        AnsiConsole.MarkupLine(
            $"[grey]hoje: {JanelaDescrita()}. fora dela o lote encerra e o resto fica na lista.[/]");
        AnsiConsole.Markup(
            "[grey]horário permitido, \"inicio fim\" em horas ([/]0 24[grey] = qualquer horário, "
            + "Enter mantém):[/] ");
        var horas = Console.ReadLine()?.Trim() ?? "";
        if (horas.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]mantido.[/]");
            return;
        }
        Janela(["janela", .. horas.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);

        // A agenda é lida em cima da janela: mudar uma sem olhar a outra é como uma etapa das 23h fica
        // marcada e nunca sai.
        if (_agenda.Count > 0)
        {
            MostrarAgenda();
        }
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
        var quarentena = 0;
        if (alvo is "contatos" or "tudo")
        {
            _contatos.Clear();
            // 🔴 A QUARENTENA VAI JUNTO, e é dito em voz alta. Ela guarda CONTATOS, então deixá-la de pé
            // depois de "limpar tudo" faria a mensagem mentir: a sessão continuaria com 23 números
            // dentro, invisíveis fora do menu, prontos pra reaparecer na planilha de outro dia e
            // parecerem lixo de origem desconhecida.
            quarentena = _suspensos.Count;
            _suspensos.Clear();
        }
        if (alvo is "textos" or "tudo")
        {
            _textos.Clear();
        }
        var recorte = quarentena == 0 ? "" : $" [grey](inclusive {quarentena} suspenso(s))[/]";
        AnsiConsole.MarkupLine($"limpo: [bold]{alvo.EscapeMarkup()}[/].{recorte}");
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
        // 🔴 SEM ShowRowSeparators(), e isso foi TESTADO E DESCARTADO em 2026-08-20. Um traço entre cada
        // opção resolvia no papel o que a linha em branco resolve por ausência, e na tela virou uma
        // grade: com 16 opções, o menu passou a ter mais borda que conteúdo, e a moldura competia com a
        // informação em vez de organizá-la. A linha em branco separa o suficiente e não desenha nada.
        t.AddColumn(new TableColumn("n").NoWrap());
        t.AddColumn(new TableColumn("o que é"));
        t.AddColumn(new TableColumn("agora"));

        // 🔴 A ORDEM DO TOPO É A ORDEM DE OPERAR, e não a ordem em que os ajustes foram nascendo. Quem
        // abre o console pela primeira vez lê a coluna de cima pra baixo e faz o lote: cola, grava,
        // confere, escreve, agenda, dispara. Os ajustes que se mexem de vez em quando desceram para o
        // bloco de baixo, porque ajuste no meio do fluxo faz o passo seguinte parecer opcional.
        //
        // 🔴 GRAVAR É O 2, e não uma conveniência lá embaixo: com o `segurar` ligado por padrão, o lote
        // pergunta à agenda do aparelho se cada número tem WhatsApp, e sem a lista gravada a agenda não
        // confirma ninguém, então o lote inteiro sai segurado. Ele não pode ser o 1 porque grava a
        // lista COLADA: sem os contatos não há o que gravar.
        //
        // Tudo numerado, inclusive o que já foi `g`, `c`, `b`, `d`, `segurar` e `bip`: tecla de tipos
        // diferentes na mesma coluna faz a mão parar para ler antes de escolher. A ÚNICA exceção é o
        // `enviar`, e ela é o ponto: ver mais abaixo.
        // UMA LINHA EM BRANCO ENTRE CADA OPÇÃO. Dezesseis linhas coladas viram um parágrafo, e num
        // parágrafo o olho não acha a terceira linha sem contar as anteriores. O menu fica mais alto por
        // causa disso, e é o preço combinado: ele é consultado item a item, não lido de ponta a ponta.
        //
        // O espaçamento uniforme apagaria os GRUPOS (o fluxo do lote, os ajustes, o disparo), que antes
        // eram desenhados justamente pela linha em branco. Por isso a divisão passou a ser dita com
        // todas as letras, numa linha de título, em vez de ficar implícita no espaço.
        var primeiraDaSecao = true;
        void Opcao(string tecla, string oQue, string agora)
        {
            if (!primeiraDaSecao)
            {
                t.AddEmptyRow();
            }
            primeiraDaSecao = false;
            t.AddRow($"[bold]{tecla}[/]", oQue, agora);
        }

        void Secao(string titulo)
        {
            if (t.Rows.Count > 0)
            {
                t.AddEmptyRow();
            }
            t.AddRow("", $"[grey]{titulo}[/]", "");
            primeiraDaSecao = true;
        }

        // O título do primeiro bloco saiu e VOLTOU no mesmo dia, a pedido do operador. O argumento para
        // tirar era que ele não separa nada (não há o que vir antes da primeira linha); o argumento que
        // o trouxe de volta é mais forte: sem ele, os três blocos de baixo têm nome e o de cima não, e
        // um rótulo faltando no primeiro grupo faz parecer que os títulos começam no meio da tela.
        Secao("o lote, na ordem");
        Opcao("1", "contatos",
            (_contatos.Count == 0 ? "[grey]vazio[/]" : $"[bold]{_contatos.Count}[/] na lista")
            // A quarentena aparece AQUI e não numa linha própria de propósito: quem lê "12 na lista"
            // precisa saber, no mesmo lugar, que havia mais e que eles não sumiram.
            + (_suspensos.Count == 0 ? "" : $" [grey]· {_suspensos.Count} suspenso(s)[/]"));
        // 🔴 NÃO PROMETE PRAZO, e a correção é de 2026-08-20: a linha dizia "espere 5 a 10 min pra
        // sincronizar", e o operador perguntou como é que se sabe que não pode ser mais. Não se sabe.
        // O que existe é UMA medição (2,5 a 7 min, 2026-07-23, neste parque de aparelhos), e ela não
        // manda no relógio do WhatsApp: quem sincroniza é a conta Google do celular, com a rede dele.
        // Número inventado com cara de regra é pior que nenhum número, porque quem esperou 10 min e
        // ainda vê tudo segurado conclui que o console está quebrado.
        Opcao("2", "gravar na agenda do aparelho",
            "[grey]sem enviar nada; o WhatsApp só reconhece depois de sincronizar[/]");
        Opcao("3", "conferir os números",
            "[grey]a forma de cada um: celular, legado ou fixo[/]");
        // 🔴 O {nome} APARECE NA COLUNA DO ESTADO, e não numa linha de instrução. A pergunta que ele
        // responde é operacional, não didática: contato SEM nome só sorteia template SEM {nome}, então
        // "3 template(s), 3 usam {nome}" com meia lista sem nome é um lote que vai parar no pré-voo.
        // Ver isso no painel é ver o problema antes de ele virar recusa.
        //
        // Com a lista vazia não há estado para mostrar, e é exatamente aí que a dica cabe sem poluir:
        // quem tem zero template é quem ainda não sabe que o token existe.
        var comNome = _textos.Count(t => t.Contains(TokenNome, StringComparison.OrdinalIgnoreCase));
        Opcao("4", "templates",
            _textos.Count == 0
                ? $"[grey]vazio (o texto pode ter[/] {TokenNome} [grey]ou ser fixo)[/]"
                : $"[bold]{_textos.Count}[/] template(s) "
                  + (comNome == 0
                      ? $"[grey]· nenhum usa {TokenNome}[/]"
                      : $"[grey]· {comNome} usa(m) {TokenNome}[/]"));
        // 🔴 QUANTOS E QUANDO SÃO A MESMA PERGUNTA, e por isso são uma linha só desde 2026-08-20.
        // Estavam em duas ("cota por execução" e "agenda de envios"), e quem lia via dois campos que
        // se contradiziam na cara: a cota dizia 25 e a agenda dizia "sem agenda (usa a cota)", o que
        // não explica nada a quem ainda não sabe que uma anula a outra. Uma opção com escolhas
        // exclusivas não tem como ficar em estado contraditório.
        Opcao("5", "quantos e quando enviar", QuantoDescrito());
        Opcao("6", "previa", "[grey]quem recebe qual texto, e com quais ajustes[/]");

        Secao("ajustes");
        Opcao("7", "ritmo entre mensagens", $"[bold]{RitmoDescrito()}[/]");
        Opcao("8", "janela permitida", $"[bold]{JanelaDescrita()}[/]");
        Opcao("9", "digitação humana",
            _digitacaoHumana
                ? "[bold]ligada[/] [grey](só ASCII)[/]"
                : "[grey]desligada[/] [green](aceita acento)[/]");
        // O nome do comando por extenso continua NA LINHA, na coluna do meio: "segurar" e "bip" eram a
        // própria tecla e sumiriam da tela ao virar número. Comando que não aparece no painel, na
        // prática, não existe.
        // 🔴 VERDE = O QUE ESTE ESTADO TE DÁ, e a cor é do PARÊNTESE, não do estado. Ligado nem sempre é
        // bom e desligado nem sempre é ruim: a digitação humana DESLIGADA é que aceita acento. Pintar o
        // "ligado" faria a coluna dizer que ligar é sempre o certo, que é falso em uma das três linhas.
        // Pintando só o ganho, o olho varre a coluna procurando verde e lê os três do mesmo jeito.
        Opcao("10", "segurar: não enviar sem a agenda confirmar",
            _segurarNaoConfirmados
                ? "[bold]ligado[/] [green](não gasta em número morto)[/]"
                : "[grey]desligado[/] [grey](só mede)[/]");
        Opcao("11", "bip: aviso sonoro por mensagem",
            _bip ? "[bold]ligado[/] [green](acompanha de ouvido)[/]" : "[grey]desligado[/]");

        // 🔴 ENVIAR NÃO TEM NÚMERO, e é a exceção consciente ao painel numerado. Ele é o único item
        // irreversível daqui: com um dígito, ficava a uma tecla de distância de um ajuste inofensivo, e
        // um "13" digitado no lugar de "12" abria o pré-voo do disparo. Escrever a palavra é uma
        // barreira que dígito nenhum oferece, e custa seis letras uma vez por lote. A regra do painel
        // numerado existe para a mão não parar antes de escolher; aqui parar é o que se quer.
        Secao("disparar");
        Opcao("enviar", "dispara o lote", "[grey]confere o aparelho, mostra o plano e PERGUNTA antes[/]");

        // O FIM DO PAINEL É O FIM DO DIA, na ordem em que ele acontece: dispara, lê o resultado, fecha.
        // A planilha ficava acima do disparo e quebrava essa leitura, porque ela é a única linha do
        // menu que só faz sentido DEPOIS de algo ter saído.
        //
        // 🔴 A QUARENTENA VIVE AQUI DENTRO desde 2026-08-20, e não numa linha própria: ela virou uma aba
        // da planilha, e duas portas para o mesmo dado obrigavam a abrir as duas para ter certeza. O que
        // NÃO podia sumir junto era devolver e descartar: a suspensão automática é a única coisa no
        // console que tira contato da fila sem ele ter recebido nada, e ela só se justifica por ser
        // reversível. Por isso esta opção pergunta, logo depois de gerar o arquivo.
        Secao("depois do lote");
        // 🔴 O RÓTULO ANUNCIAVA A PARTE REDUNDANTE. Ele dizia "gera o .xlsx e abre", que é exatamente o
        // que o fim do lote já faz sozinho, e o operador perguntou (com razão) para que serve a opção.
        // O que só existe aqui são duas coisas: DEVOLVER OU DESCARTAR a quarentena, que a geração
        // automática não pode perguntar porque roda no finally, inclusive depois de um Ctrl+C; e gerar
        // o relatório FORA de um lote, para quem abriu o console só pra olhar o histórico.
        Opcao("12", "planilha e quarentena",
            _suspensos.Count == 0
                ? "[grey]o .xlsx do aparelho, sem precisar disparar (no fim do lote ele sai sozinho)[/]"
                : $"[yellow]{_suspensos.Count} em quarentena: devolve ou descarta aqui[/]"
                  + " [grey]· gera o .xlsx junto[/]");
        Opcao("0", "sair", "[grey]fecha (tudo fica salvo)[/]");

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

    /// <summary>Gera a planilha do aparelho e, se houver quarentena, oferece devolver ou descartar.
    /// </summary>
    /// <remarks>
    /// 🔴 UMA PORTA SÓ, por decisão do operador em 2026-08-20. O `suspensos` era uma tela separada
    /// listando quem saiu da fila, e a planilha passou a ter a aba Suspensos com os mesmos números e
    /// mais o que o log sabe deles. Dois lugares mostrando o mesmo dado fazem quem confere abrir os
    /// dois, e é isso que esta fusão desfaz.
    /// <para>O que NÃO some junto é o devolver e o descartar: a suspensão automática é a única coisa no
    /// console que tira contato da fila sem ele ter recebido nada, e ela só se justifica por ser
    /// REVERSÍVEL. A pergunta vem DEPOIS de gerar o arquivo, de propósito: a decisão fica melhor com a
    /// aba aberta na frente, e o caso que a motiva (chip restrito negando todo mundo) só se reconhece
    /// olhando as tentativas.</para>
    /// </remarks>
    private void Planilha(string serial)
    {
        // Sem contexto de lote: o recorte vira o histórico inteiro, e a planilha diz isso na aba Resumo
        // em vez de deixar parecer que aqueles números são de hoje.
        GerarRelatorio(serial, null);
        Quarentena();
    }

    /// <summary>A pergunta da quarentena: devolve, descarta, ou deixa como está.</summary>
    private void Quarentena()
    {
        if (_suspensos.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]{_suspensos.Count} contato(s) em quarentena[/] [grey](aba[/] [bold]Suspensos[/] "
            + "[grey]da planilha, com a última tentativa de cada um). o app afirmou que estes números "
            + "não têm conta, e por isso eles saíram da fila de envio.[/]");
        MostrarAlguns(_suspensos.Count, 20,
            i => $"  [red]{_suspensos[i].Numero}[/] {(_suspensos[i].Nome ?? "").EscapeMarkup()}");

        AnsiConsole.Markup(
            "[bold]1[/] devolver todos para a lista  ·  [bold]2[/] descartar de vez  ·  "
            + "[bold]enter[/] deixar como está: ");
        switch (Console.ReadLine()?.Trim())
        {
            case "1":
                _contatos.AddRange(_suspensos);
                AnsiConsole.MarkupLine(
                    $"[green]{_suspensos.Count} devolvido(s).[/] [grey]a lista voltou a ter "
                    + $"{_contatos.Count} contato(s). se o app negar de novo, eles saem de novo.[/]");
                _suspensos.Clear();
                break;
            case "2":
                AnsiConsole.MarkupLine(
                    $"[grey]{_suspensos.Count} descartado(s). o log de envios continua guardando "
                    + "cada tentativa deles.[/]");
                _suspensos.Clear();
                break;
            default:
                AnsiConsole.MarkupLine("[grey]nada mudou.[/]");
                break;
        }
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
        // O NÚMERO DA OPÇÃO, e não só o nome do comando: este rodapé aparece logo abaixo do painel, e
        // ali a mão já está no teclado esperando um dígito. Mandar digitar "previa" no meio de uma tela
        // que só pede número obriga a trocar de modo por um item de leitura.
        MostrarAlguns(total, teto, i => $"  {linha(i)}", " (veja todos na opção 6, a previa)");
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

    /// <summary>Uma pergunta por número: o mínimo, depois o máximo. Enter mantém o que já vale.</summary>
    /// <remarks>
    /// 🔴 ERA UMA LINHA COM OS DOIS VALORES ("min max"), e não funcionava na prática: o operador
    /// relatou em 2026-08-20 que não conseguia entrar com os dois. A pergunta pedia uma sintaxe (dois
    /// números separados por espaço) numa tela que, em todo o resto do console, pede UM valor por vez.
    /// Quem digitasse só um número, ou separasse por vírgula, caía na mensagem de uso.
    /// <para>Com uma pergunta por número, a vírgula deixa de ser ambígua: sozinha numa linha, "2,5" só
    /// pode ser dois e meio. Na linha com dois valores, "2,5" tanto podia ser 2,5 minutos quanto "de 2
    /// a 5", e não havia como escolher sem adivinhar.</para>
    /// <para>Enter em qualquer uma das duas mantém o valor atual, que na primeira vez é o de fábrica.
    /// Quem não quer decidir não precisa: o padrão já é uma resposta.</para>
    /// </remarks>
    private void IntervaloInterativo()
    {
        AnsiConsole.MarkupLine(
            $"[grey]ritmo atual: [/][bold]{RitmoDescrito()}[/][grey]. a espera de cada mensagem é "
            + "sorteada entre os dois, para o lote não ter cara de máquina.[/]");

        AnsiConsole.Markup($"[grey]mínimo, em minutos (Enter mantém {EmMinutos(_min)}):[/] ");
        var minTexto = Console.ReadLine()?.Trim() ?? "";
        AnsiConsole.Markup($"[grey]máximo, em minutos (Enter mantém {EmMinutos(_max)}):[/] ");
        var maxTexto = Console.ReadLine()?.Trim() ?? "";

        if (minTexto.Length == 0 && maxTexto.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]mantido.[/]");
            return;
        }

        // Enter em UMA das duas mantém aquela e muda a outra: trocar só o máximo é o ajuste mais comum
        // de quem quer espalhar mais o lote sem mexer no piso.
        var novoMin = minTexto.Length == 0 ? _min : MinutosEmSegundos(minTexto);
        var novoMax = maxTexto.Length == 0 ? _max : MinutosEmSegundos(maxTexto);
        if (novoMin is not { } segMin || novoMax is not { } segMax)
        {
            AnsiConsole.MarkupLine(
                "[red]não entendi o número.[/] [grey]digite minutos, como[/] 3 [grey]ou[/] 2,5[grey]. "
                + "nada mudou.[/]");
            return;
        }
        if (segMax < segMin)
        {
            AnsiConsole.MarkupLine(
                $"[red]o máximo ({EmMinutos(segMax)} min) é menor que o mínimo ({EmMinutos(segMin)} min).[/] "
                + "[grey]nada mudou.[/]");
            return;
        }

        (_min, _max) = (segMin, segMax);
        AnsiConsole.MarkupLine($"ritmo entre mensagens: [bold]{RitmoDescrito()}[/].");
        AvisarRitmoCurto();
    }

    /// <summary>Liga e desliga a digitação caractere a caractere, e conta o que muda em troca.</summary>
    /// <remarks>
    /// 🔴 O CORPO SAIU DO SWITCH porque o mesmo botão tem duas entradas: o número do menu e a palavra
    /// <c>digitacao</c>. Duplicar as duas linhas de estado seria duplicar a que escreve em
    /// <see cref="PhoneOptions.HumanTyping"/>, e um dos lados esqueceria dela. O driver relê essa
    /// propriedade a cada envio: sem ela, o menu diria "desligada" e o aparelho continuaria digitando.
    /// </remarks>
    private void AlternarDigitacaoHumana()
    {
        _digitacaoHumana = !_digitacaoHumana;
        options.Value.HumanTyping = _digitacaoHumana;
        AnsiConsole.MarkupLine(_digitacaoHumana
            ? "[bold]digitação humana LIGADA:[/] digita caractere a caractere, o destinatário vê "
              + "\"digitando…\". [yellow]só ASCII: acento e emoji são barrados no pré-voo.[/]"
            : "[bold]digitação humana DESLIGADA:[/] o texto vai pronto pelo deep link, então "
              + "[green]aceita acento, emoji e quebra de linha[/]. "
              + "[yellow]em troca não há digitação, e o destinatário não vê \"digitando…\".[/]");
    }

    /// <summary>Liga e desliga a checagem prévia na agenda antes de gastar o disparo.</summary>
    private void AlternarSegurar()
    {
        _segurarNaoConfirmados = !_segurarNaoConfirmados;
        AnsiConsole.MarkupLine(_segurarNaoConfirmados
            ? "checagem prévia: [bold]ligada[/] [grey]— pergunta à agenda se o número tem "
              + "WhatsApp antes de gastar o disparo. quem a agenda não confirmar é "
              + "segurado e fica na lista.[/]"
            : "checagem prévia: [grey]desligada — tenta todo mundo e descobre abrindo a "
              + "conversa (gasta tentativa em número morto).[/]");
    }

    /// <summary>Liga e desliga o bip por mensagem, e toca uma amostra ao ligar.</summary>
    private async Task AlternarBipAsync()
    {
        _bip = !_bip;
        AnsiConsole.MarkupLine(_bip
            ? "bip a cada mensagem: [bold]ligado[/] [grey](agudo curto = saiu; dois graves "
              + "= não saiu). dá pra acompanhar o lote sem olhar a tela.[/]"
            : "bip a cada mensagem: [grey]desligado (lote silencioso).[/]");
        if (_bip)
        {
            await BiparAsync(true); // amostra na hora: som que ninguém ouviu não foi configurado.
        }
    }

    // ── Agenda de envios ─────────────────────────────────────────────────────────────────────────

    /// <summary>Cola a agenda igual se colam contatos e templates: uma etapa por linha.</summary>
    /// <remarks>
    /// 🔴 MESMO CONCEITO DA LISTA DE TEMPLATES, por pedido do operador em 2026-08-20: uma agenda é uma
    /// LISTA, não um ajuste. Antes, "quantos e a que horas" era o par teto + janela, que só sabe
    /// descrever UMA fatia por dia: "manda 50, entre 8h e 22h". Quem queria 50 de manhã e 30 à tarde
    /// não tinha como dizer isso, e a saída era voltar ao console no meio da tarde para reconfigurar.
    /// <para>Com a agenda, o console espera a hora e dispara a etapa sozinho. A previsão de término de
    /// cada etapa é o que fecha o ciclo: é ela que deixa marcar a etapa seguinte sem chutar.</para>
    /// </remarks>
    private void LerAgenda(bool somar)
    {
        if (!somar)
        {
            _agenda.Clear();
        }
        AnsiConsole.MarkupLine(
            "[grey]uma etapa por linha:[/] [bold]hora;quantos[/] [grey](espaço e vírgula também "
            + "separam). exemplos:[/] 08:00;50   ·   14h30 30   ·   19 20");
        AnsiConsole.MarkupLine(
            "[grey]para terminar:[/] [bold]Enter numa linha vazia[/] [grey](Enter duas vezes no fim). "
            + "também vale digitar[/] fim[grey].[/]");

        var aceitos = 0;
        var rejeitados = new List<string>();
        while (true)
        {
            var l = Console.ReadLine();
            if (FimDoBloco(l))
            {
                break;
            }
            var (item, erro) = ParseAgendamento(l!);
            if (item is null)
            {
                rejeitados.Add($"{l!.Trim()} → {erro}");
                continue;
            }
            _agenda.Add(item);
            aceitos++;
        }

        // Ordenada pela hora, sempre. A agenda é lida como uma linha do tempo, e uma etapa das 8h
        // depois de uma das 14h faria o operador conferir a ordem em vez de confiar nela.
        _agenda.Sort((a, b) => a.Hora.CompareTo(b.Hora));
        AnsiConsole.MarkupLine($"[green]{aceitos}[/] etapa(s) aceita(s); a agenda tem [bold]{_agenda.Count}[/].");
        MostrarAlguns(rejeitados.Count, 10, i => $"[red]rejeitada:[/] {rejeitados[i].EscapeMarkup()}", " rejeitada(s)");
        MostrarAgenda();
    }

    private void EditarAgendamento()
    {
        var n = 1;
        if (_agenda.Count > 1)
        {
            MostrarAgenda();
            AnsiConsole.Markup("[grey]qual etapa? (o número mostrado acima):[/] ");
            if (!int.TryParse(Console.ReadLine()?.Trim(), out n) || n < 1 || n > _agenda.Count)
            {
                AnsiConsole.MarkupLine("[red]não há essa etapa.[/] a tabela mostra a numeração.");
                return;
            }
        }
        AnsiConsole.MarkupLine(
            $"[grey]atual:[/] {_agenda[n - 1].Hora:HH\\:mm} · {_agenda[n - 1].Quantos} envio(s)");
        AnsiConsole.Markup("[grey]nova etapa (hora;quantos, Enter cancela):[/] ");
        var linha = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(linha))
        {
            AnsiConsole.MarkupLine("[grey]cancelado, nada mudou.[/]");
            return;
        }
        var (item, erro) = ParseAgendamento(linha);
        if (item is null)
        {
            AnsiConsole.MarkupLine($"[red]{erro}[/] nada mudou.");
            return;
        }
        _agenda[n - 1] = item;
        _agenda.Sort((a, b) => a.Hora.CompareTo(b.Hora));
        AnsiConsole.MarkupLine($"[green]etapa {n} atualizada.[/]");
        MostrarAgenda();
    }

    /// <summary>A agenda com a PREVISÃO DE TÉRMINO de cada etapa, mais o que ela atropela.</summary>
    /// <remarks>
    /// 🔴 A PREVISÃO É O PRODUTO desta tela, não a lista. "50 envios às 8h" não diz nada sobre quando
    /// dá pra marcar a próxima; "termina ~10:35" diz. O cálculo é o mesmo do pré-voo (ver
    /// <see cref="EsperaDe"/>), de propósito: dois cálculos diferentes para a mesma pergunta fariam a
    /// agenda e a confirmação do lote discordarem na cara do operador.
    /// <para>É ESTIMATIVA e a tela diz isso: conta o ritmo entre mensagens e as pausas de bloco, e não
    /// conta o tempo do envio em si (abrir conversa, digitar, tocar), que varia com o aparelho.</para>
    /// </remarks>
    private void MostrarAgenda()
    {
        if (_agenda.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[grey]sem agenda: o lote sai quando você mandar, respeitando a cota e a janela.[/]");
            return;
        }

        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("#");
        t.AddColumn("começa");
        t.AddColumn("envios");
        t.AddColumn("termina ~");
        t.AddColumn("observação");

        var fimAnterior = TimeOnly.MinValue;
        for (var i = 0; i < _agenda.Count; i++)
        {
            var (hora, quantos) = (_agenda[i].Hora, _agenda[i].Quantos);
            var fim = hora.Add(EsperaDe(quantos));
            var notas = new List<string>();

            // Atravessou a meia-noite: o Add dá a volta no relógio, e uma etapa que termina "às 01:20"
            // do dia seguinte não vai terminar coisa nenhuma, porque o console encerra fora da janela.
            if (fim < hora)
            {
                notas.Add("[red]passa da meia-noite[/]");
            }
            if (i > 0 && hora < fimAnterior)
            {
                notas.Add($"[yellow]a etapa {i} ainda estará rodando[/]");
            }
            if (ForaDaJanela(hora))
            {
                notas.Add($"[yellow]fora da janela ({JanelaDescrita()})[/]");
            }

            t.AddRow(
                $"[blue]{i + 1}[/]",
                $"[bold]{hora:HH\\:mm}[/]",
                quantos.ToString(CultureInfo.InvariantCulture),
                $"[bold]{fim:HH\\:mm}[/]",
                notas.Count == 0 ? "[grey]ok[/]" : string.Join(" · ", notas));
            fimAnterior = fim;
        }
        AnsiConsole.Write(t);

        var total = TotalAgendado();
        AnsiConsole.MarkupLine(
            $"[grey]total agendado:[/] [bold]{total}[/] [grey]envio(s) em {_agenda.Count} etapa(s). "
            + "a previsão conta o ritmo, e NÃO conta o tempo de cada envio, então o término real fica "
            + "um pouco depois.[/]");
        if (_contatos.Count > 0 && total > _contatos.Count)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]a agenda pede {total} envios e a lista tem {_contatos.Count} contato(s).[/] "
                + "[grey]as últimas etapas ficam sem quem mandar.[/]");
        }

        // 🔴 QUEM SOBREPÕE QUEM, DITO UMA VEZ E COM TODAS AS LETRAS. A tabela já marca cada etapa fora
        // da janela na coluna de observação, mas marca não é regra: quem lê "fora da janela" ao lado de
        // uma etapa não sabe se aquilo é aviso, impedimento, ou se a hora que ele acabou de escolher
        // AUMENTA a janela. Não aumenta. A janela é conferida antes de CADA mensagem, então uma etapa
        // marcada fora dela começa, bate na conferência e não manda nada.
        var foraDaJanela = _agenda.Count(a => ForaDaJanela(a.Hora));
        if (foraDaJanela > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{foraDaJanela} etapa(s) estão fora da janela ({JanelaDescrita()}) e NÃO vão "
                + "mandar nada.[/] [grey]a janela do[/] [bold]8[/] [grey]manda sobre a hora daqui: "
                + "marcar um horário aqui não abre a janela. abra o[/] [bold]8[/] [grey]ou mude a "
                + "hora da etapa.[/]");
        }
    }

    /// <summary>"Quantos e quando" numa pergunta só: sem limite, uma cota, ou etapas com hora.</summary>
    /// <remarks>
    /// 🔴 UMA OPÇÃO, TRÊS ESTADOS EXCLUSIVOS, e é isso que ela resolve. Cota e agenda eram duas linhas
    /// do menu, e o operador leu "cota 25" logo acima de "sem agenda (usa a cota)" e perguntou o que
    /// aquilo queria dizer. Com razão: eram dois campos descrevendo o MESMO limite, e a regra de qual
    /// vence estava escrita em letra miúda em vez de estar na forma da tela. Escolher aqui apaga o
    /// outro estado, então não existe mais combinação contraditória para explicar.
    /// <para>Sem limite continua sendo o padrão, e é o que "não quero agendar nada" significa: manda a
    /// lista inteira, no ritmo do ajuste 7, dentro da janela do 8.</para>
    /// </remarks>
    private void QuantoEQuando()
    {
        // Sem desmontar o markup do QuantoDescrito: as tags do Spectre ANINHAM, então basta fechar a
        // minha antes de emendar a dele. A versão anterior arrancava as tags com Replace, o que quebra
        // calado no dia em que a descrição ganhar uma cor que a lista de Replace não conhece.
        AnsiConsole.MarkupLine($"[grey]hoje:[/] {QuantoDescrito()}[grey].[/]");
        AnsiConsole.MarkupLine("  [bold]1[/] [grey]sem limite: manda a lista inteira de uma vez[/]");
        AnsiConsole.MarkupLine("  [bold]2[/] [grey]uma cota: quantos envios nesta execução, sem hora marcada[/]");
        AnsiConsole.MarkupLine("  [bold]3[/] [grey]agendar: etapas com hora e quantidade, com previsão de término[/]");
        AnsiConsole.MarkupLine("  [bold]4[/] [grey]automático: a cota sai do histórico deste chip, lote a lote[/]");
        AnsiConsole.Markup("[grey]escolha (Enter mantém):[/] ");

        switch (Console.ReadLine()?.Trim())
        {
            // 🔴 NÃO limpa a agenda AQUI: quem limpa é o Teto, e ele AVISA antes de limpar. Fazer o
            // Clear antes deixava o aviso mudo (ele só dispara com agenda de pé), e apagar 3 etapas
            // que alguém montou, sem uma linha na tela, é destruir trabalho em silêncio.
            case "1":
                Teto(["teto", "0"]);
                break;
            case "2":
                AnsiConsole.Markup("[grey]quantos envios nesta execução?[/] ");
                var quantos = Console.ReadLine()?.Trim() ?? "";
                // Zero aqui é engano, não "sem limite": quem quer a lista inteira acabou de ver a
                // linha 1. Aceitar zero calado devolveria a ambiguidade que esta tela desfez.
                if (quantos.Length == 0 || quantos == "0")
                {
                    AnsiConsole.MarkupLine(
                        "[grey]mantido. para mandar a lista inteira, escolha[/] [bold]1[/][grey].[/]");
                    return;
                }
                // Valida ANTES de delegar: o `Teto` responde com a sintaxe do comando por extenso
                // ("uso: teto <n>"), que é a resposta certa para quem digitou um comando e a errada
                // para quem está dentro de um menu e nunca viu esse comando.
                if (!int.TryParse(quantos, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cota)
                    || cota < 0)
                {
                    AnsiConsole.MarkupLine(
                        "[red]não entendi o número.[/] [grey]digite quantos envios, como[/] 25[grey]. "
                        + "nada mudou.[/]");
                    return;
                }
                Teto(["teto", cota.ToString(CultureInfo.InvariantCulture)]);
                break;
            case "3":
                switch (PerguntarModo(_agenda.Count, "etapa(s) agendada(s)"))
                {
                    case ModoLista.Editar: EditarAgendamento(); break;
                    case ModoLista.Cancelar: AnsiConsole.MarkupLine("[grey]cancelado.[/]"); break;
                    case var m: LerAgenda(somar: m == ModoLista.Acrescentar); break;
                }
                break;
            case "4":
                Teto(["teto", "auto"]);
                break;
            default:
                AnsiConsole.MarkupLine("[grey]mantido.[/]");
                break;
        }
    }

    /// <summary>O estado de "quantos e quando" em uma linha, para o menu.</summary>
    private string QuantoDescrito() =>
        _agenda.Count > 0
            ? AgendaDescrita()
            : _tetoAuto
                ? "[bold]automático[/] [grey](pelo histórico do chip)[/]"
                : _teto == 0
                    ? "[bold]sem limite[/] [grey](a lista inteira, de uma vez)[/]"
                    : $"cota de [bold]{_teto}[/] [grey]envio(s) nesta execução[/]";

    /// <summary>Quantos envios a agenda inteira pede.</summary>
    /// <remarks>
    /// 🔴 SOMA EM <c>long</c> E VOLTA CLAMPADA, e isso não é preciosismo: o <c>Sum</c> de <c>int</c> do
    /// LINQ é CHECKED, então duas etapas grandes o bastante não devolvem um número errado, elas
    /// levantam <c>OverflowException</c> — e esta soma é lida na hora de DESENHAR O MENU, fora de
    /// qualquer try. O console morreria ao redesenhar o painel, sem lote nenhum rodando, por causa de
    /// um número digitado numa etapa. Somar em long e clampar troca uma quebra por um valor absurdo
    /// visível na tela, que é o modo de falha certo aqui.
    /// </remarks>
    private int TotalAgendado()
    {
        var total = _agenda.Sum(a => (long)a.Quantos);
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>Uma linha só, para o menu.</summary>
    private string AgendaDescrita() =>
        _agenda.Count == 0
            ? "[grey]sem agenda (usa a cota)[/]"
            : $"[bold]{_agenda.Count}[/] etapa(s), [bold]{TotalAgendado()}[/] envio(s)"
              + $" [grey]· {_agenda[0].Hora:HH\\:mm} até ~{_agenda[^1].Hora.Add(EsperaDe(_agenda[^1].Quantos)):HH\\:mm}[/]";

    /// <summary>"08:00;50", "14h30 30", "19 20". Devolve o erro em português quando não dá.</summary>
    private static (Agendamento? Item, string? Erro) ParseAgendamento(string linha)
    {
        var partes = (linha ?? "")
            .Replace(';', ' ')
            .Replace(',', ' ')
            .Replace('\t', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length < 2)
        {
            return (null, "faltou a hora ou a quantidade. exemplo: 08:00;50");
        }
        if (ParseHora(partes[0]) is not { } hora)
        {
            return (null, $"não entendi a hora \"{partes[0]}\". use 08:00, 8h30 ou 19");
        }
        if (!int.TryParse(partes[1], out var quantos) || quantos <= 0)
        {
            return (null, $"\"{partes[1]}\" não é uma quantidade de envios");
        }
        return (new Agendamento(hora, quantos), null);
    }

    /// <summary>Aceita 8, 08, 8h, 8h30, 8:30 e 08:30. Hora sem minuto vale a hora cheia.</summary>
    private static TimeOnly? ParseHora(string texto)
    {
        var t = texto.Trim().ToLowerInvariant().Replace('h', ':').TrimEnd(':');
        if (!t.Contains(':', StringComparison.Ordinal))
        {
            t += ":00";
        }
        return TimeOnly.TryParse(t, CultureInfo.InvariantCulture, out var hora) ? hora : null;
    }

    /// <summary>Quanto tempo N envios levam SÓ DE ESPERA, no ritmo de agora.</summary>
    /// <remarks>
    /// Uma conta só, usada pela agenda e pelo pré-voo: dois cálculos para a mesma pergunta fariam as
    /// duas telas discordarem na cara do operador. NÃO conta o tempo do envio em si (abrir conversa,
    /// digitar, tocar), que varia com o aparelho, e as duas telas dizem isso.
    /// </remarks>
    private TimeSpan EsperaDe(int quantos) => EsperaDe(quantos, _min, _max);

    private TimeSpan EsperaDe(int quantos, double min, double max)
    {
        if (quantos <= 1)
        {
            return TimeSpan.Zero;
        }
        // N mensagens têm N-1 intervalos entre elas, e cada intervalo é sorteado entre min e max: a
        // média é o único número honesto para uma previsão.
        return TimeSpan.FromSeconds((quantos - 1) * ((min + max) / 2.0));
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
        ExplicarLinhaEmBranco();
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

    private static void Comandos()
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumn("comando").AddColumn("o que faz");
        t.AddRow("gravar [grey]| g[/]", "grava a lista na agenda do aparelho, SEM enviar nada");
        t.AddRow("contatos [grey]| contatos +[/]", "cola a lista (substitui | soma). formato: numero ou numero;nome");
        t.AddRow("textos [grey]| textos +[/]", $"cola os templates (substitui | soma). {TokenNome} vira o nome do contato");
        t.AddRow("[grey]  (na opção 4)[/] ..",
            "DOIS pontos numa linha sozinha viram uma linha em BRANCO dentro do texto; um ponto só encerra");
        t.AddRow("agenda [grey]| agenda +[/]", "cola as etapas do dia (substitui | soma). formato: hora;quantos");
        t.AddRow("previa [grey]| ver[/]", "simula quem receberia qual template, e mostra os ajustes do lote");
        t.AddRow("conferir [grey]| c[/]", "classifica cada número: celular, legado, fixo ou faltando o 9º dígito");
        t.AddRow("enviar", "pré-voo, plano, confirmação e disparo do lote");
        t.AddRow("status", "reconsulta o aparelho pelo adb");
        t.AddRow("intervalo <min> <max>", "MINUTOS entre um envio e o próximo (default 2,5 a 5; aceita 2,5)");
        // 🔴 O DEFAULT AQUI ESTAVA ERRADO e foi pego pelo operador em 2026-08-20: dizia "8 22", que é o
        // default do WarmupEngineOptions no servidor, não o deste console. A janela nasce ABERTA (0-24)
        // por decisão registrada no campo, justamente para o console poder rodar em qualquer horário.
        // Lista de referência que mente sobre um default é pior que lista incompleta: quem confere um
        // comportamento estranho contra ela conclui que o programa é que está errado.
        t.AddRow("janela <ini> <fim>", "horas em que é permitido enviar (default 0 24 = qualquer horário)");
        t.AddRow("teto <n> [grey]| teto auto[/]",
            "cota de ENVIOS desta execução, usada quando NÃO há agenda; auto deriva do histórico do chip");
        t.AddRow("chip novo", "você REGISTROU outra conta neste aparelho: o aquecimento recomeça do zero");
        t.AddRow("parar <n>", "falhas seguidas que interrompem o lote (default 0 = nunca interrompe)");
        t.AddRow("bip [grey]| som[/]", "liga/desliga o aviso sonoro (agudo = saiu, dois graves = não saiu)");
        t.AddRow("segurar", "liga/desliga NÃO enviar quando a agenda não confirma que o número tem WhatsApp");
        t.AddRow("digitacao", "liga/desliga digitar caractere a caractere (DESLIGADA por padrão; ligada só sai ASCII)");
        t.AddRow("planilha [grey]| relatorio | suspensos[/]",
            "gera o .xlsx (com a aba Suspensos), abre, e oferece devolver ou descartar a quarentena");
        t.AddRow("x [grey][[contato|texto]] [[n]][/]", "exclui UM item (pergunta se você não disser qual)");
        t.AddRow("limpar [grey][[contatos|textos|tudo]][/]", "esvazia o que você pedir");
        t.AddRow("acentos [grey]| semacento[/]", "tira os acentos de todos os templates (só faz falta com a digitação LIGADA)");
        t.AddRow("comandos [grey]| ajuda | ? | help[/]", "esta lista");
        t.AddRow("sair [grey]| exit | quit[/]", "fecha o console (a lista fica salva)");
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
                _contatos.Add(Desserializar(linha));
            }
            foreach (var linha in e.Suspensos ?? [])
            {
                _suspensos.Add(Desserializar(linha));
            }
            foreach (var linha in e.AgendaDeEnvios ?? [])
            {
                // Linha corrompida à mão no JSON não derruba o console nem apaga a agenda inteira:
                // entra o que dá pra entender, some o que não dá.
                if (ParseAgendamento(linha) is { Item: { } etapa })
                {
                    _agenda.Add(etapa);
                }
            }
            _agenda.Sort((a, b) => a.Hora.CompareTo(b.Hora));
            _textos.AddRange(e.Textos);
            (_min, _max, _digitacaoHumana) = (e.MinDelay, e.MaxDelay, e.DigitacaoHumana);
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
            if (e.SegurarNaoConfirmados is { } segurar)
            {
                _segurarNaoConfirmados = segurar;
            }
            if (e.TetoAuto is { } ta)
            {
                _tetoAuto = ta;
            }
            (_conta, _chipDesde) = (e.Conta, e.ChipDesde);
            // e.Bloco e e.PausaMin são LIDOS E IGNORADOS de propósito: o ajuste saiu do console em
            // 2026-08-20 e o campo fica no record só para sessão antiga não quebrar ao desserializar.
            // Só aceita o par se ele for coerente. Janela invertida gravada por um bug antigo deixaria
            // o console MUDO para sempre, e é o defeito que o HumanPhaseAutoSender já documenta.
            if (e.HoraInicio is { } hi && e.HoraFim is { } hf && hi >= 0 && hf <= 24 && hi < hf)
            {
                (_horaInicio, _horaFim) = (hi, hf);
            }

            // Sessão gravada antes dos padrões de 2026-08-20: os três toggles voltam ao padrão UMA vez,
            // com aviso. Ver Estado.Versao para o porquê de não dar pra ser mais esperto que isso.
            if (e.Versao is null)
            {
                (_digitacaoHumana, _segurarNaoConfirmados, _bip) = (false, true, true);
                AnsiConsole.MarkupLine(
                    "[yellow]os padrões mudaram e este aparelho foi ajustado uma vez:[/] [grey]digitação "
                    + "humana DESLIGADA (aceita acento), segurar LIGADO (não gasta em número morto), bip "
                    + "LIGADO. os três continuam em[/] [bold]9[/][grey],[/] [bold]10[/] [grey]e[/] "
                    + "[bold]11[/][grey], e a sua escolha a partir de agora manda.[/]");
            }


            // 🔴 O RITMO NOVO SÓ ALCANÇA QUEM NUNCA ESCOLHEU UM, e o teste de "nunca escolheu" é o valor
            // ser exatamente o padrão anterior. É a mesma ideia que o console já usa para o preenchimento
            // automático do teto: o valor em si responde a pergunta, sem um flag a mais para persistir.
            // Sobrescrever um ritmo escolhido à mão seria desfazer trabalho do operador em silêncio, que
            // é justamente o que a marca de versão existe para não fazer.
            if (e.Versao is null or < 3 && _min == MinPadrao && _max == MaxPadraoAntigo)
            {
                _max = MaxPadrao;
                AnsiConsole.MarkupLine(
                    $"[yellow]o ritmo de fábrica mudou para {RitmoDescrito()}[/] [grey]e este aparelho "
                    + "estava com o anterior, sem nunca ter escolhido um. mude no[/] [bold]7[/] "
                    + "[grey]quando quiser.[/]");
            }
            if (_contatos.Count > 0 || _textos.Count > 0)
            {
                var quarentena = _suspensos.Count == 0
                    ? ""
                    : $", {_suspensos.Count} suspenso(s)";
                AnsiConsole.MarkupLine(
                    $"[grey]sessão anterior restaurada: {_contatos.Count} contato(s), "
                    + $"{_textos.Count} template(s){quarentena}.[/]");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Estado é conveniência, não dado de verdade: se corromper, começa vazio em vez de travar.
            AnsiConsole.MarkupLine($"[grey]não deu pra restaurar a sessão anterior: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>Contato como uma linha só, "numero;nome". Usado pela lista e pela quarentena.</summary>
    /// <remarks>
    /// O nome NÃO é escapado, ao contrário do CSV: um nome com ';' embaralharia o round-trip. Fica como
    /// estava porque o formato já é o da lista e mudá-lo agora invalidaria toda sessão gravada — mas o
    /// par ida e volta mora aqui, junto, em vez de espalhado, pra o dia em que valer a pena consertar.
    /// </remarks>
    private static string Serializar(Contato c) => $"{c.Numero};{c.Nome ?? ""}";

    private static Contato Desserializar(string linha)
    {
        var campos = linha.Split(';', 2);
        return new Contato(campos[0], campos.Length > 1 && campos[1].Length > 0 ? campos[1] : null);
    }

    private void Salvar(string serial)
    {
        try
        {
            var e = new Estado
            {
                Contatos = [.. _contatos.Select(Serializar)],
                Suspensos = [.. _suspensos.Select(Serializar)],
                AgendaDeEnvios = [.. _agenda.Select(a => $"{a.Hora:HH\\:mm};{a.Quantos}")],
                Textos = [.. _textos],
                MinDelay = _min,
                MaxDelay = _max,
                Teto = _teto,
                PararEm = _pararEm,
                HoraInicio = _horaInicio,
                HoraFim = _horaFim,
                DigitacaoHumana = _digitacaoHumana,
                Bip = _bip,
                SegurarNaoConfirmados = _segurarNaoConfirmados,
                TetoAuto = _tetoAuto,
                Conta = _conta,
                ChipDesde = _chipDesde,
                Versao = VersaoDosPadroes,
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

    /// <summary>O CSV de envios deste aparelho. Um lugar só monta este caminho.</summary>
    /// <remarks>
    /// Três funções abrem o mesmo arquivo por perguntas diferentes (gravar, reler pro aquecimento, reler
    /// pro relatório). Com o caminho montado à mão em cada uma, renomear o arquivo ou mudar a pasta
    /// exigiria acertar as três, e acertar duas de três significa gravar num arquivo e ler de outro:
    /// o console passaria a dizer que ninguém nunca recebeu nada.
    /// </remarks>
    private static string CaminhoDoLog(string serial) =>
        Path.Combine(Pasta, $"envios-{Higienizar(serial)}.csv");

    private static string AbrirLog(string serial)
    {
        var caminho = CaminhoDoLog(serial);
        if (!File.Exists(caminho))
        {
            File.WriteAllText(
                caminho,
                "quando;serial;numero;nome;variante;enviado;entrega;erro;texto;contradito;abertura;causa\n",
                Encoding.UTF8);
        }
        return caminho;
    }

    /// <summary>Grava linha a linha, não no fim: se a janela morrer no meio do lote, o que já saiu
    /// continua registrado. Sem isto não há como saber quem já recebeu.</summary>
    /// <param name="contradito">A recusa foi desmentida pelo espelho da agenda: o app disse "sem conta"
    /// para um número que ele mesmo marca como usuário do WhatsApp.</param>
    /// <remarks>
    /// 🔴 A COLUNA NOVA ENTRA NO FIM, e isso não é preguiça. O <see cref="LerLog"/> lê por
    /// ÍNDICE (número em [2], enviado em [5]), e é ele que impede mandar duas vezes pra mesma pessoa
    /// entre execuções. Acrescentar no fim deixa todos os índices anteriores intactos, então CSV antigo
    /// continua sendo lido igual.
    /// <para>O cabeçalho de um arquivo que já existe NÃO é reescrito: este log é append-only de
    /// propósito (é o que faz ele sobreviver a queda de energia no meio do lote), e voltar pra
    /// reescrever a primeira linha trocaria essa propriedade por cosmética. Consequência aceita: em CSV
    /// criado antes desta mudança, a última coluna aparece sem nome no Excel.</para>
    /// </remarks>
    private static void Registrar(
        string caminho, string serial, Contato c, int variante, string texto, WhatsAppSendResult r,
        bool contradito)
    {
        try
        {
            // Três valores na coluna "enviado", não dois. "incerto" é o envio cujo toque aconteceu e não
            // deu pra confirmar: gravar isso como "nao" mentiria pro LerLog, que é a única
            // memória entre execuções, e a pessoa voltaria amanhã sem nenhum aviso de que talvez já
            // tenha recebido.
            var enviado = r.Sent ? "sim" : r.Uncertain ? "incerto" : "nao";
            var linha = string.Join(';',
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                Csv(serial), Csv(c.Numero), Csv(c.Nome ?? ""), variante.ToString(CultureInfo.InvariantCulture),
                enviado, Csv(r.DeliveryStatus ?? ""), Csv(r.Error ?? ""), Csv(texto),
                contradito ? "sim" : "",
                r.AbertoPeloRegistro ? "registro" : "numero",
                // 🔴 O NOME DA CONSTANTE, não o número dela. Renumerar o enum um dia não pode reescrever
                // o passado, e um log em que se lê "Timeout" vale mais que um em que se lê "10". A coluna
                // `erro` ao lado continua sendo a frase para humano; esta é o dado para agrupar.
                r.Causa.ToString());
            File.AppendAllText(caminho, linha + "\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[grey]falha ao gravar no log: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>Tudo que o pré-voo precisa saber do log, numa leitura só.</summary>
    /// <param name="JaEnviados">Quem já recebeu deste aparelho, em qualquer dia. Inclui os incertos.</param>
    /// <param name="TalvezReceberam">
    /// Subconjunto de <paramref name="JaEnviados"/>: os que ficaram INCERTOS e nunca tiveram uma entrega
    /// confirmada depois. São dois avisos diferentes. "já recebeu" pede uma decisão de campanha; "pode
    /// ter recebido" pede que alguém abra a conversa NO APARELHO e olhe, e é a única das duas que tem um
    /// jeito de virar certeza. Enquanto os dois saíam na mesma linha, a segunda nunca era feita.
    /// </param>
    /// <param name="DiasAtivos">Dias distintos com disparo.</param>
    /// <param name="UltimoFechado">Resumo do último dia FECHADO. null = só há o dia de hoje.</param>
    /// <param name="EnviadasHoje">Quanto já saiu hoje, para descontar da sugestão.</param>
    /// <param name="DiasDesdeUltimoDia">Dias corridos entre o último dia fechado e hoje. Sem lacuna
    /// nenhuma (disparou ontem) dá 1; 0 significa que não há dia fechado.</param>
    private sealed record ResumoDoLog(
        HashSet<string> JaEnviados,
        HashSet<string> TalvezReceberam,
        int DiasAtivos,
        DiaDoChip? UltimoFechado,
        int EnviadasHoje,
        int DiasDesdeUltimoDia);

    /// <summary>Lê o CSV do aparelho UMA vez e responde tudo que o pré-voo pergunta.</summary>
    /// <remarks>
    /// 🔴 ERAM DUAS PASSADAS COMPLETAS no mesmo arquivo, poucas linhas uma da outra: uma pro aviso de
    /// repetição, outra pro painel do chip. Cada uma parseava todas as linhas com
    /// <see cref="CamposCsv"/>, alocando lista e StringBuilder por linha — e este log CRESCE PARA
    /// SEMPRE, então o desperdício aumenta a cada lote.
    ///
    /// <para>Perguntas diferentes, mesma fonte, mesmo instante: é o caso clássico de unificar a
    /// leitura em vez de otimizar cada uma. Mesmo motivo do cache do <c>ContatoAsync</c> no leitor de
    /// agenda.</para>
    ///
    /// <para>Best-effort: log ilegível devolve resumo vazio, que é o desfecho conservador — sem aviso
    /// de repetição e com sugestão de chip novo.</para>
    /// </remarks>
    /// <param name="chipDesde">Data (yyyy-MM-dd) em que a conta atual começou, ou null. Dias anteriores
    /// entram no aviso de repetição, porque a PESSOA já recebeu, mas ficam fora do aquecimento, porque
    /// quem mandou foi outra conta. Os dois usos do mesmo arquivo têm perguntas diferentes.</param>
    private static ResumoDoLog LerLog(string serial, string? chipDesde)
    {
        var numeros = new HashSet<string>(StringComparer.Ordinal);
        var talvez = new HashSet<string>(StringComparer.Ordinal);
        var porDia = new Dictionary<string, (int Enviadas, int Recusadas, int Confirmadas)>(
            StringComparer.Ordinal);
        try
        {
            var caminho = CaminhoDoLog(serial);
            if (!File.Exists(caminho))
            {
                return new ResumoDoLog(numeros, talvez, 0, null, 0, 0);
            }
            foreach (var linha in File.ReadLines(caminho).Skip(1))
            {
                var campos = CamposCsv(linha);
                if (campos.Count < 7)
                {
                    continue;
                }
                // "incerto" entra JUNTO com "sim" nos dois usos. Pro aviso, a pergunta é "pode já ter
                // recebido?" e não "recebeu com certeza?". Pro volume, o que conta contra o chip é a
                // conversa ABERTA, e ela foi aberta do mesmo jeito.
                var saiu = campos[5] is "sim" or "incerto";
                if (saiu)
                {
                    numeros.Add(campos[2]);
                }
                // 🔴 A DÚVIDA É REGISTRADA E TAMBÉM DESFEITA, e a ordem entre as duas coisas é o ponto.
                // Um "sim" POSTERIOR ao incerto significa que a conversa foi reaberta e a mensagem saiu
                // de verdade: a partir daí não há mais o que conferir no aparelho, e manter o aviso
                // mandaria o operador procurar uma dúvida que já acabou. O log é lido em ordem
                // cronológica, então basta remover aqui.
                if (campos[5] == "incerto")
                {
                    talvez.Add(campos[2]);
                }
                else if (campos[5] == "sim")
                {
                    talvez.Remove(campos[2]);
                }

                // A coluna `quando` é ISO-8601, então os 10 primeiros caracteres já são a data —
                // parsear o timestamp inteiro só pra descartar a hora seria trabalho e um modo de
                // falha a mais.
                if (campos[0].Length < 10)
                {
                    continue;
                }
                var dia = campos[0][..10];

                // 🔴 SÓ O AQUECIMENTO É CORTADO. `numeros` acima já recebeu este contato, e de propósito:
                // quem recebeu de outra conta continua sendo alguém que JÁ RECEBEU, e mandar de novo é
                // gatilho de denúncia independente de qual chip mandou. O que muda de dono é a curva de
                // volume, não a memória de quem foi atendido.
                // Comparação por texto porque a data é ISO-8601, em que ordem alfabética é cronológica.
                if (chipDesde is not null && string.CompareOrdinal(dia, chipDesde) < 0)
                {
                    continue;
                }

                porDia.TryGetValue(dia, out var acc);
                if (saiu)
                {
                    acc.Enviadas++;
                    if (campos[6] is "delivered" or "read")
                    {
                        acc.Confirmadas++;
                    }
                }
                else
                {
                    acc.Recusadas++;
                }
                porDia[dia] = acc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log ilegível não pode impedir o envio; no pior caso o aviso de repetição não aparece.
            return new ResumoDoLog(numeros, talvez, 0, null, 0, 0);
        }

        if (porDia.Count == 0)
        {
            return new ResumoDoLog(numeros, talvez, 0, null, 0, 0);
        }

        // 🔴 HOJE É SEPARADO DO ÚLTIMO DIA FECHADO, e a distinção não é cosmética. Rodando um SEGUNDO
        // lote no mesmo dia, "o último dia" seria HOJE — e a sugestão cresceria sobre o que já saiu
        // hoje, encorajando dobrar o volume do dia.
        var agora = DateTime.Now;
        var hoje = agora.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var enviadasHoje = porDia.TryGetValue(hoje, out var doDiaDeHoje) ? doDiaDeHoje.Enviadas : 0;
        // Ordenação por texto vale porque a data é ISO-8601, em que ordem alfabética É ordem cronológica.
        var ultimo = porDia
            .Where(p => !string.Equals(p.Key, hoje, StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (KeyValuePair<string, (int Enviadas, int Recusadas, int Confirmadas)>?)p)
            .LastOrDefault();
        if (ultimo is not { } fechado)
        {
            return new ResumoDoLog(numeros, talvez, porDia.Count, null, enviadasHoje, 0);
        }

        // 🔴 QUANTOS DIAS FAZ, e não só qual foi o dia. Sem esta conta, quem some por um mês e volta
        // recebe sugestão de CRESCER sobre o dia de um mês atrás, porque o dado diz "limpo" e nada
        // pergunta quando. Silêncio longo seguido de pico é padrão punido por si só.
        var lacuna = DateTime.TryParseExact(
            fechado.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var data)
            ? Math.Max(0, (int)(agora.Date - data.Date).TotalDays)
            : 0;

        return new ResumoDoLog(
            numeros,
            talvez,
            porDia.Count,
            new DiaDoChip(fechado.Value.Enviadas, fechado.Value.Recusadas, fechado.Value.Confirmadas),
            enviadasHoje,
            lacuna);
    }

    /// <summary>O log inteiro deste aparelho, linha a linha, para o relatório.</summary>
    /// <remarks>
    /// 🔴 SEPARADO DO <see cref="LerLog"/> DE PROPÓSITO, apesar de os dois lerem o mesmo arquivo. O
    /// LerLog roda em TODO envio e alimenta o teto automático e o aviso de repetição: é caminho quente,
    /// e o que ele devolve decide quantas mensagens saem hoje. Este aqui só roda quando alguém pede
    /// relatório, e devolve tudo em memória porque a planilha precisa de tudo.
    ///
    /// <para>Juntar os dois numa passada só economizaria uma leitura de arquivo por lote (uma, não uma
    /// por mensagem) e em troca faria o caminho do aquecimento carregar a lista completa de envios de
    /// todos os dias. Não vale.</para>
    ///
    /// <para>Linha malformada é PULADA, não interrompe: o CSV é append-only e escrito durante lotes de
    /// horas, então uma linha truncada por queda de energia é resultado esperado, e ela não pode levar
    /// junto os meses de histórico que vêm depois dela.</para>
    /// </remarks>
    private static List<LinhaDeEnvio> LerLinhas(string serial)
    {
        var linhas = new List<LinhaDeEnvio>();
        var caminho = CaminhoDoLog(serial);
        if (!File.Exists(caminho))
        {
            return linhas;
        }

        try
        {
            foreach (var bruta in File.ReadLines(caminho).Skip(1))
            {
                var campos = CamposCsv(bruta);
                // O mesmo piso do LerLog: 7 colunas é o mínimo que dá pra interpretar. Acima disso cada
                // coluna é lida por índice SÓ se existir, que é como um CSV de antes da coluna `causa`
                // (11 colunas) continua abrindo ao lado de um de agora (12).
                if (campos.Count < 7
                    || RelatorioDeEnvios.Interpretar(campos[5]) is not { } resultado
                    || !DateTimeOffset.TryParse(
                        campos[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var quando))
                {
                    continue;
                }

                linhas.Add(new LinhaDeEnvio(
                    Quando: quando,
                    Numero: campos[2],
                    Nome: string.IsNullOrWhiteSpace(campos[3]) ? null : campos[3],
                    Variante: int.TryParse(campos[4], CultureInfo.InvariantCulture, out var v) ? v : 0,
                    Resultado: resultado,
                    Entrega: string.IsNullOrWhiteSpace(campos[6]) ? null : campos[6],
                    Erro: campos.Count > 7 && !string.IsNullOrWhiteSpace(campos[7]) ? campos[7] : null,
                    Texto: campos.Count > 8 ? campos[8] : "",
                    Contradito: campos.Count > 9 && campos[9] == "sim",
                    Abertura: campos.Count > 10 ? campos[10] : "",
                    Causa: RelatorioDeEnvios.LerCausa(campos.Count > 11 ? campos[11] : null)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mesma doutrina do LerLog: log ilegível não pode derrubar nada. No pior caso o relatório
            // sai com o que deu pra ler antes do erro.
            AnsiConsole.MarkupLine($"[grey]não deu pra ler o log inteiro: {ex.Message.EscapeMarkup()}[/]");
        }
        return linhas;
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
