using MtrxSys.Core.Domain.Messages;

namespace MtrxSys.Core.Application.Abstractions;

public interface IMessageTemplateRepository
{
    Task<IReadOnlyList<MessageTemplate>> ListActiveBySlotAsync(MessageSlot slot, CancellationToken ct);
    Task<IReadOnlyList<MessageTemplate>> ListAllAsync(CancellationToken ct);
    Task<MessageTemplate?> GetByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(MessageTemplate template, CancellationToken ct);
}
