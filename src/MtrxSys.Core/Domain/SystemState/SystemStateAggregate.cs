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

    public bool IsManuallyPaused => PausedReason == ManualPauseReason;

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
            // configurado/em andamento); só passa a acompanhar este chip.
            WarmupPhone = connectedPhone;
            return false;
        }
        if (string.Equals(WarmupPhone, connectedPhone, StringComparison.Ordinal))
        {
            return false; // mesmo chip
        }
        // Número diferente → chip novo: reinicia o aquecimento e passa a acompanhar o novo.
        RestartWarmup(today);
        WarmupPhone = connectedPhone;
        return true;
    }
}
