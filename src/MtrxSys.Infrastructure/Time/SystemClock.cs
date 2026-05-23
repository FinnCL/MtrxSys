using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Time;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
