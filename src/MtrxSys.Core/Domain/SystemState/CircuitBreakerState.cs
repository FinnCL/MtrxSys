using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.SystemState;

public sealed class CircuitBreakerState : ValueObject
{
    public int ConsecutiveFailures { get; }
    public DateTimeOffset? OpenUntil { get; }

    public CircuitBreakerState(int consecutiveFailures, DateTimeOffset? openUntil)
    {
        ConsecutiveFailures = consecutiveFailures;
        OpenUntil = openUntil;
    }

    public static CircuitBreakerState Closed => new(0, null);

    public bool IsOpenAt(DateTimeOffset now) => OpenUntil.HasValue && OpenUntil.Value > now;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return ConsecutiveFailures;
        yield return OpenUntil;
    }
}
