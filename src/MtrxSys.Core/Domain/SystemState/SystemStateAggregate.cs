using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.SystemState;

public sealed class SystemStateAggregate : Entity<int>
{
    public const int SingletonId = 1;

    public CircuitBreakerState Circuit { get; private set; } = CircuitBreakerState.Closed;
    public string? PausedReason { get; private set; }

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
