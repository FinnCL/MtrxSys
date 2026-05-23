using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

public sealed class DelayPolicy(IRandomSource rng, IOptions<DispatchOptions> opts)
{
    public TimeSpan NextDelay()
    {
        var o = opts.Value;
        var min = Math.Max(1, o.DelayMinSeconds);
        var max = Math.Max(min + 1, o.DelayMaxSeconds + 1);
        var seconds = rng.NextInt(min, max);
        return TimeSpan.FromSeconds(seconds);
    }
}
