using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.SystemState;

public sealed class DailySendCount : Entity<DateOnly>
{
    public int SentCount { get; private set; }
    public int WarmupDayIndex { get; private set; }

    private DailySendCount() { }

    public static DailySendCount Create(DateOnly dateUtc, int warmupDayIndex)
    {
        return new DailySendCount
        {
            Id = dateUtc,
            SentCount = 0,
            WarmupDayIndex = warmupDayIndex,
        };
    }
}
