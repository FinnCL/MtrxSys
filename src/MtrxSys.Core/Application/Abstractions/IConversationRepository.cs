using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Conversation?> GetByWaChatIdAsync(string waChatId, CancellationToken ct);
    Task<IReadOnlyList<Conversation>> ListAsync(int limit, int offset, CancellationToken ct);
    Task AddAsync(Conversation conversation, CancellationToken ct);
    Task UpdateAsync(Conversation conversation, CancellationToken ct);
}
