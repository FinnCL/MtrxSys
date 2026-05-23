using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Safety;

public sealed class CircuitBreaker(ISystemStateRepository state, IClock clock, IOptions<CircuitBreakerOptions> opts)
{
    public async Task<bool> IsOpenAsync(CancellationToken ct)
    {
        var s = await state.GetAsync(ct);
        return s.Circuit.IsOpenAt(clock.UtcNow);
    }

    public async Task RecordSuccessAsync(CancellationToken ct)
    {
        var s = await state.GetAsync(ct);
        if (s.Circuit.ConsecutiveFailures == 0)
        {
            return;
        }
        s.UpdateCircuit(new CircuitBreakerState(0, null));
        await state.UpdateAsync(s, ct);
    }

    public async Task RecordFailureAsync(string reason, CancellationToken ct)
    {
        var o = opts.Value;
        var s = await state.GetAsync(ct);
        var newCount = s.Circuit.ConsecutiveFailures + 1;
        DateTimeOffset? openUntil = newCount >= o.FailureThreshold
            ? clock.UtcNow.AddMinutes(o.OpenDurationMinutes)
            : s.Circuit.OpenUntil;
        s.UpdateCircuit(new CircuitBreakerState(newCount, openUntil));
        if (openUntil is not null && s.PausedReason is null)
        {
            s.Pause(reason);
        }
        await state.UpdateAsync(s, ct);
    }
}
