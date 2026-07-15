using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.SystemState;

public sealed class SystemStateAggregate : Entity<int>
{
    public const int SingletonId = 1;

    // Sentinela usado quando o operador pausa os envios manualmente (botão "Parar envios").
    // Diferencia da pausa automática do circuit breaker, que grava o motivo da falha.
    public const string ManualPauseReason = "MANUAL";

    // Marca "liberar tudo hoje" (sem teto). int.MaxValue é grande o bastante pra nunca
    // ser atingido por um disparo real no dia.
    public const int UnlimitedBonus = int.MaxValue;

    public CircuitBreakerState Circuit { get; private set; } = CircuitBreakerState.Closed;
    public string? PausedReason { get; private set; }

    // Data (UTC) em que o aquecimento do chip começou. Quando null, o WarmupManager
    // cai no valor do appsettings (ou em "hoje"). Fica no banco — e não só na config —
    // pra poder ser reiniciada por clique (chip novo) sem editar arquivo nem reiniciar.
    public DateOnly? WarmupStartedOn { get; private set; }

    // Liberação manual do teto do aquecimento PARA UM DIA específico (decisão consciente
    // do operador no modal). Expira sozinha: só vale se WarmupOverrideDate == hoje.
    public DateOnly? WarmupOverrideDate { get; private set; }
    public int WarmupBonusToday { get; private set; }

    // Número (E.164) que o aquecimento está acompanhando. Serve pra detectar troca de chip:
    // se o número conectado mudar, o aquecimento reinicia sozinho (chip novo = frio de novo).
    public string? WarmupPhone { get; private set; }

    // UTC da última vez que o PRINCIPAL (emulador) esteve online — no pareamento ou num keep-alive.
    // Governa quando o próximo keep-alive é devido (janela de ~14 dias do WhatsApp, senão o companion
    // WAHA é deslogado). Null = nunca pareado (nada a manter vivo ainda).
    public DateTimeOffset? PhonePrimaryLastOnlineUtc { get; private set; }

    // Liga/desliga o MOTOR DE AQUECIMENTO DE CONVERSA (WarmupEngine) — o toggle do botão
    // "Iniciar/Parar Aquecimento" na UI. Persistido pra o aquecimento sobreviver a restart (é um
    // processo de dias). NÃO confunde com o teto diário de disparo (WarmupStartedOn etc.).
    public bool WarmupEngineEnabled { get; private set; }

    // Liga/desliga o ENVIO AUTOMÁTICO da Fase Humana (o robô conversando com o seu círculo pela via
    // do Chat). Persistido porque é um processo de dias e precisa sobreviver a restart — mesmo
    // motivo do WarmupEngineEnabled. Só tem efeito enquanto a fase se aplica e não fechou; depois
    // disso o remetente para sozinho, sem depender deste toggle.
    public bool HumanPhaseAutoSendEnabled { get; private set; }

    // Modo da aba "Celular" (toggle único da UI) — fonte da verdade do que a página renderiza.
    // Persistido pra sobreviver a restart e ao 502 do emulador (antes era derivado do container ligado).
    // Default WahaOnly: o modo mais simples/seguro (WAHA + aparelho real, sem depender do emulador).
    public PhoneDispatchMode DispatchMode { get; private set; } = PhoneDispatchMode.WahaOnly;

    public bool IsManuallyPaused => PausedReason == ManualPauseReason;

    /// <summary>O motor está enviando AGORA? = há job na fila e ninguém pausou.
    ///
    /// Existe pra responder uma pergunta que <see cref="IsManuallyPaused"/> sozinho NÃO responde:
    /// "não pausado" é o mesmo valor num sistema recém-subido (que nunca enviou nada) e num que
    /// está no meio de um envio. Quem distingue é a fila ter job pendente.
    ///
    /// Serve pra decidir se enfileirar deve pausar. Preparar uma fila nova PRECISA pausar (nada sai
    /// sem o operador mandar); somar contatos a uma fila que já está enviando NÃO pode pausar — ele
    /// já mandou, e pausar ali derrubava o envio em curso em silêncio.</summary>
    public bool IsSendingNow(bool queueHasPendingJobs) => queueHasPendingJobs && !IsManuallyPaused;

    private SystemStateAggregate() { }

    public static SystemStateAggregate CreateInitial()
    {
        return new SystemStateAggregate
        {
            Id = SingletonId,
            Circuit = CircuitBreakerState.Closed,
        };
    }

    public void UpdateCircuit(CircuitBreakerState newState) => Circuit = newState;

    public void Pause(string reason) => PausedReason = reason;

    public void Resume() => PausedReason = null;

    // Marca que o primário apareceu online agora (reinicia a contagem dos ~14 dias). Chamado pelo
    // PhoneKeepAliveService ao confirmar WORKING no pareamento e a cada keep-alive.
    public void RecordPhonePrimaryOnline(DateTimeOffset whenUtc) => PhonePrimaryLastOnlineUtc = whenUtc;

    // Liga/desliga o motor de aquecimento de conversa (botão da UI).
    public void SetWarmupEngineEnabled(bool enabled) => WarmupEngineEnabled = enabled;

    // Liga/desliga o envio automático da Fase Humana (botão do card na aba Celular).
    public void SetHumanPhaseAutoSendEnabled(bool enabled) => HumanPhaseAutoSendEnabled = enabled;

    // Troca o modo da aba "Celular" (toggle único). Persistido pelo endpoint /api/phone/mode.
    public void SetDispatchMode(PhoneDispatchMode mode) => DispatchMode = mode;

    // Reinicia o aquecimento a partir de hoje — a curva volta ao dia 0. Usado ao
    // trocar de chip (número novo é "frio" de novo). Zera também qualquer liberação extra.
    public void RestartWarmup(DateOnly today)
    {
        WarmupStartedOn = today;
        WarmupOverrideDate = null;
        WarmupBonusToday = 0;
    }

    // Quanto de "extra" o operador liberou para HOJE (0 se a liberação é de outro dia/expirou).
    public int BonusFor(DateOnly today) => WarmupOverrideDate == today ? WarmupBonusToday : 0;

    // Libera +extra envios acima do teto da curva, só pra hoje. Chamadas no mesmo dia somam;
    // se a liberação anterior era de outro dia, recomeça a contagem do extra.
    public void ReleaseWarmupBonus(DateOnly today, int extra)
    {
        if (extra <= 0) return;
        var current = BonusFor(today);
        WarmupOverrideDate = today;
        WarmupBonusToday = current == UnlimitedBonus
            ? UnlimitedBonus
            : (int)Math.Min((long)current + extra, UnlimitedBonus);
    }

    // Libera o teto inteiro de hoje (disparar todos), sem limite. Expira à meia-noite.
    public void ReleaseWarmupAll(DateOnly today)
    {
        WarmupOverrideDate = today;
        WarmupBonusToday = UnlimitedBonus;
    }

    // Reconcilia o aquecimento com o número conectado. Retorna true se detectou troca de chip
    // (e portanto reiniciou o aquecimento). Leitura vazia/instável é ignorada de propósito —
    // resetar à toa só atrasaria (lado seguro), mas evitamos flap em reconexões.
    public bool ReconcileWarmupPhone(string? connectedPhone, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(connectedPhone))
        {
            return false;
        }
        if (WarmupPhone is null)
        {
            // Primeira vez que registramos o número: NÃO reinicia (preserva o início já
            // configurado/em andamento); só passa a acompanhar este chip. Mas ANCORA o marco se
            // ainda não houver: sem isso o 1º chip de um stack novo ficava com WarmupStartedOn
            // null pra sempre — e a fase humana, que só vale pra chip ancorado a partir do corte,
            // nunca se aplicaria justo a ele. Só preenche o vazio (??=): quem já tem marco
            // preserva o seu. Chegar aqui implica que nenhum chip foi visto ainda, logo não há
            // histórico de envio — então ancorar hoje não mexe no índice da curva (já era 0).
            WarmupStartedOn ??= today;
            WarmupPhone = connectedPhone;
            return false;
        }
        if (string.Equals(WarmupPhone, connectedPhone, StringComparison.Ordinal))
        {
            return false; // mesmo chip
        }
        // Número diferente → chip novo: reinicia o aquecimento e passa a acompanhar o novo.
        RestartWarmup(today);
        // Chip NOVO não herda o robô ligado: a primeira atividade de uma conta recém-pareada ser um
        // envio automático é o padrão que a Fase Humana existe pra evitar — ligar tem que ser uma
        // decisão consciente, por chip. Fica AQUI, e não no RestartWarmup, porque aquele também é
        // chamado pelo botão manual "reiniciar aquecimento" no MESMO chip: ali o operador só quer
        // recomeçar a curva, e desligar o robô junto pararia o aquecimento sem ninguém perceber.
        HumanPhaseAutoSendEnabled = false;
        WarmupPhone = connectedPhone;
        return true;
    }
}
