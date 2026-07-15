namespace MtrxSys.Core.Application.Options;

/// <summary>Fase Humana Estrita: os primeiros dias de um chip novo, em que o DISPARO fica travado e
/// o operador conversa à mão com pessoas reais pela aba Chat. Chip frio é quando o WhatsApp olha
/// mais de perto — ter template automatizado como PRIMEIRA atividade da conta é assinatura de bot.
/// A fase abre sozinha quando há evidência medida (gente que respondeu) E dias suficientes.</summary>
public sealed class HumanPhaseOptions
{
    public const string SectionName = "HumanPhase";

    // Corte do rollout. NULL = recurso DESLIGADO — é o default de propósito: os stacks já em
    // produção não podem parar de disparar por causa de um deploy. A fase só vale pra chip cujo
    // marco (WarmupStartedOn) é DESTA data em diante, ou seja, pareado/re-pareado após o corte.
    public DateOnly? EffectiveFrom { get; set; }

    // Mínimo de dias COM ATIVIDADE de saída (não dias de calendário): chip parado não amadurece,
    // mesmo invariante que o WarmupManager já aplica à curva.
    public int MinDays { get; set; } = 3;

    // Quantas conversas distintas precisam qualificar.
    public int MinPeople { get; set; } = 5;

    // O que faz uma conversa qualificar. Exigir os DOIS lados é o ponto da fase: uma conversa só
    // de ida é exatamente o padrão que estamos tentando evitar — o valor está na resposta.
    public int MinInbound { get; set; } = 3;
    public int MinOutbound { get; set; } = 3;

    public HumanPhaseAutoSendOptions AutoSend { get; set; } = new();
}

/// <summary>Envio AUTOMÁTICO pro círculo durante a Fase Humana — o "aquecer com meus contatos
/// salvos" sem trabalho manual. Vai pelo caminho do CHAT (waha.SendTextAsync direto), NÃO pelo
/// disparo: assim não toca no ledger compartilhado (que marcaria os seus contatos como "já enviado"
/// nos 10 ambientes, PARA SEMPRE, queimando justo quem aquece o próximo chip), nem em LastSentAt,
/// nem em Stage. Por isso mandar várias mensagens pra mesma pessoa é livre aqui — não há trava a
/// isentar.
///
/// Não dá pra burlar a fase com isto: o gate exige INBOUND por pessoa, e robô produz saída, não
/// resposta. Se seus amigos não responderem de verdade, a fase não fecha.</summary>
public sealed class HumanPhaseAutoSendOptions
{
    // Frases de abertura/continuação, com spintax. Vazio por padrão DE PROPÓSITO: o binder do .NET
    // ANEXA a arrays já populados (não substitui), então um default não-vazio se concatenaria com o
    // do appsettings. Vazio → o remetente usa DefaultPhrases (mesmo padrão do WarmupManager.Curve).
    public string[] Phrases { get; set; } = [];

    // Janela ativa (hora de Brasília). Mandar mensagem de madrugada é sinal de robô — mesmo critério
    // que o WarmupEngineOptions já usa no aquecimento de conversa.
    public int ActiveHourStart { get; set; } = 8;
    public int ActiveHourEnd { get; set; } = 22;

    // Intervalo entre mensagens PARA A MESMA PESSOA. Longo de propósito: conversa humana respira.
    public int MinGapMinutes { get; set; } = 25;
    public int MaxGapMinutes { get; set; } = 90;

    public int MaxPerPersonPerDay { get; set; } = 4;

    // Quantas mensagens seguidas SEM resposta antes de parar de insistir com aquela pessoa. É a
    // trava mais importante daqui: insistir com quem não responde é exatamente o padrão de robô que
    // a fase existe pra evitar — e um humano também simplesmente pararia.
    public int MaxUnansweredInARow { get; set; } = 2;
}
