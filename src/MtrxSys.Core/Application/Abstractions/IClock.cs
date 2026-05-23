namespace MtrxSys.Core.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly TodayUtc => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}
