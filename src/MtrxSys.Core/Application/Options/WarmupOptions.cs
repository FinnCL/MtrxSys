namespace MtrxSys.Core.Application.Options;

public sealed class WarmupOptions
{
    public const string SectionName = "Warmup";

    public int[] Curve { get; set; } = [20, 40, 80, 150, 250, 400, 500];
    public DateOnly? StartedOnUtc { get; set; }
}
