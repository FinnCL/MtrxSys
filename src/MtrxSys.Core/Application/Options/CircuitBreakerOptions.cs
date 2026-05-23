namespace MtrxSys.Core.Application.Options;

public sealed class CircuitBreakerOptions
{
    public const string SectionName = "CircuitBreaker";

    public int FailureThreshold { get; set; } = 3;
    public int OpenDurationMinutes { get; set; } = 120;
}
