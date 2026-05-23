namespace MtrxSys.Core.Application.Abstractions;

public interface IDispatchMetrics
{
    void RecordSendSuccess(int delayMs, int typingMs);
    void RecordSendFailure(string reason);
    void RecordCircuitOpen();
    void RecordWarmupBlocked();
}
