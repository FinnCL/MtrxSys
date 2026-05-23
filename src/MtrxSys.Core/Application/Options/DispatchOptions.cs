namespace MtrxSys.Core.Application.Options;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public string SessionId { get; set; } = "default";
    public int DelayMinSeconds { get; set; } = 45;
    public int DelayMaxSeconds { get; set; } = 75;
    public int TypingMinSeconds { get; set; } = 2;
    public int TypingMaxSeconds { get; set; } = 5;
    public double TypingJitter { get; set; } = 0.15;
}
