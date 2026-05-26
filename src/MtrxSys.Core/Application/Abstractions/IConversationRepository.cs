using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Conversation?> GetByWaChatIdAsync(string waChatId, CancellationToken ct);
    /// <summary>Conversa não-grupo mais recente vinculada ao contato (usada pelo disparo
    /// pra cair na mesma conversa das respostas, evitando duplicar @c.us vs @lid).</summary>
    Task<Conversation?> GetByContactIdAsync(Guid contactId, CancellationToken ct);
    /// <summary>Lista conversas filtradas por status (awaitingReply/responded/optedOut, ou null=todas)
    /// e por busca (nome/telefone/título), paginada no servidor. Escala sem teto — só uma página por vez.</summary>
    Task<IReadOnlyList<Conversation>> ListByStatusAsync(string? status, string? search, int limit, int offset, CancellationToken ct);
    /// <summary>Contagem por status (respeitando a busca), para os números das abas sem carregar tudo.</summary>
    Task<ConversationStatusCounts> CountByStatusAsync(string? search, CancellationToken ct);
    Task AddAsync(Conversation conversation, CancellationToken ct);
    Task UpdateAsync(Conversation conversation, CancellationToken ct);
    /// <summary>Conversas individuais (não-grupo) sem contato vinculado — órfãs a religar ao contato.</summary>
    Task<IReadOnlyList<Conversation>> ListUnlinkedIndividualAsync(CancellationToken ct);
}

public sealed record ConversationStatusCounts(int AwaitingReply, int Responded, int OptedOut, int All);
