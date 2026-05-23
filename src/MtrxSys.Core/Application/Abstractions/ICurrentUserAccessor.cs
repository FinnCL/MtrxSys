namespace MtrxSys.Core.Application.Abstractions;

public interface ICurrentUserAccessor
{
    Guid? UserId { get; }
}
