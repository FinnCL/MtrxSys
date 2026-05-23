namespace MtrxSys.Core.Domain.Common;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
