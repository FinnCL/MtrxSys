namespace MtrxSys.Core.Application.Options;

/// <summary>Configuração do MOTOR DE AQUECIMENTO DE CONVERSA (WarmupEngine) — NÃO confundir com o
/// <see cref="WarmupOptions"/> (rampa de teto diário de ENVIO do disparo). Aqui é o aquecimento de
/// ATIVIDADE: um POOL de contas conversa entre si (mão dupla) e entra em grupos legítimos, pra a conta
/// ganhar reputação de "usuário real" antes do disparo frio. Ligado/desligado é persistido no
/// system_state (toggle da UI); esta seção define o pool + o ritmo.</summary>
public sealed class WarmupEngineOptions
{
    public const string SectionName = "WarmupEngine";

    // Default de segurança do pool: só roda se o system_state.WarmupEngineEnabled estiver ligado E
    // houver >= 2 membros. Este flag é só o "permitido por config" (kill-switch de deploy).
    public bool Enabled { get; set; }

    // Membros do pool (contas controláveis). Cada um = 1 sessão WAHA alcançável (baseUrl+key próprios)
    // + o número E.164. Mão dupla precisa de >= 2. A BASE deve ter número(s) REAL(is)/estabelecido(s)
    // pra não virar cluster de fazenda de bots (ver o plano).
    public List<WarmupMemberOptions> Members { get; set; } = [];

    // Links de convite de grupos LEGÍTIMOS existentes. Cada membro (com JoinGroups=true) entra 1x por
    // grupo, PASSIVO (só recebe o tráfego — o motor NÃO posta pra estranhos).
    public List<string> GroupInviteLinks { get; set; } = [];

    // Rampa de conversas: no dia 0 cada membro envia até MessagesPerDayStart; sobe linearmente até
    // MessagesPerDayMax ao longo de RampDays. Idade não se fake — subir devagar é de propósito.
    // Default = rampa SUAVE de 3 dias (+2/dia): dia0=4, dia1=6, dia2=8, dia3+=10. Ajustável por config.
    public int MessagesPerDayStart { get; set; } = 4;
    public int MessagesPerDayMax { get; set; } = 10;
    public int RampDays { get; set; } = 3;

    // Só conversa dentro das horas ativas (horário de Brasília), pra não mandar mensagem às 4h.
    public int ActiveHourStart { get; set; } = 8;
    public int ActiveHourEnd { get; set; } = 22;

    // Intervalo aleatório entre uma conversa e a próxima (minutos) — espalha ao longo do dia.
    public int MinGapMinutes { get; set; } = 20;
    public int MaxGapMinutes { get; set; } = 90;

    // Simulação de digitação antes de cada mensagem (segundos).
    public int TypingMinSeconds { get; set; } = 2;
    public int TypingMaxSeconds { get; set; } = 6;

    // Pausa aleatória entre um turno e a resposta do outro lado (segundos) — parece leitura + digitação.
    public int ReplyDelayMinSeconds { get; set; } = 5;
    public int ReplyDelayMaxSeconds { get; set; } = 40;

    // Período do worker. Baixo o bastante pra reagir ao toggle; a cadência real é governada pelo gap.
    public int TickSeconds { get; set; } = 60;

    // "template" (banco de frases embutido, grátis) ou "ai" (futuro — LLM). Só "template" no MVP.
    public string ContentMode { get; set; } = "template";
}

/// <summary>Um membro do pool de aquecimento — uma conta WAHA controlável.</summary>
public sealed class WarmupMemberOptions
{
    public string Name { get; set; } = "";
    public string WahaBaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SessionId { get; set; } = "default";
    public string PhoneE164 { get; set; } = "";

    // Este membro deve entrar nos GroupInviteLinks? Ligue só pras contas que você QUER em grupos
    // (ex.: a conta sendo aquecida). Desligue pro seu número pessoal, se não quiser ele nos grupos.
    public bool JoinGroups { get; set; } = true;
}
