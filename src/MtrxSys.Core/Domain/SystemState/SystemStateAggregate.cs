using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.SystemState;

public sealed class SystemStateAggregate : Entity<int>
{
    public const int SingletonId = 1;

    // Sentinela usado quando o operador pausa os envios manualmente (botão "Parar envios").
    // Diferencia da pausa automática do circuit breaker, que grava o motivo da falha.
    public const string ManualPauseReason = "MANUAL";

    public CircuitBreakerState Circuit { get; private set; } = CircuitBreakerState.Closed;
    public string? PausedReason { get; private set; }

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
}
