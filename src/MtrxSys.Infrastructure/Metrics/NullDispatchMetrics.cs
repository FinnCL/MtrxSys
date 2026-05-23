using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Metrics;

internal sealed class NullDispatchMetrics : IDispatchMetrics
{
    public void RecordSendSuccess(int delayMs, int typingMs) { }
    public void RecordSendFailure(string reason) { }
    public void RecordCircuitOpen() { }
    public void RecordWarmupBlocked() { }
}
