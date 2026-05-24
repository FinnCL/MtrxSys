using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Conversation?> GetByWaChatIdAsync(string waChatId, CancellationToken ct);
    /// <summary>Conversa não-grupo mais recente vinculada ao contato (usada pelo disparo
    /// pra cair na mesma conversa das respostas, evitando duplicar @c.us vs @lid).</summary>
    Task<Conversation?> GetByContactIdAsync(Guid contactId, CancellationToken ct);
    Task<IReadOnlyList<Conversation>> ListAsync(int limit, int offset, CancellationToken ct);
    Task AddAsync(Conversation conversation, CancellationToken ct);
    Task UpdateAsync(Conversation conversation, CancellationToken ct);
}
