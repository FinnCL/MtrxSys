using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Conversation?> GetByWaChatIdAsync(string waChatId, CancellationToken ct);
    /// <summary>Conversa não-grupo mais recente vinculada ao contato (usada pelo disparo
    /// pra cair na mesma conversa das respostas, evitando duplicar @c.us vs @lid).</summary>
    Task<Conversation?> GetByContactIdAsync(Guid contactId, CancellationToken ct);
    /// <summary>Referências das conversas individuais (não-grupo) de VÁRIOS contatos, numa consulta só.
    /// Versão em lote do <see cref="GetByContactIdAsync"/> pro <c>OptOutReconciler</c> não fazer uma
    /// query por contato (N+1). Devolve TODAS as conversas de cada contato — a escolha de qual usar
    /// fica com quem chama, que deve aplicar o mesmo critério do método singular (a de atividade mais
    /// recente). PROJEÇÃO, não entidade: rodando sobre a base inteira, materializar Conversation
    /// carregaria título e prévia da última mensagem (até 280 chars) de cada contato ativo à toa.</summary>
    Task<IReadOnlyList<ContactConversationRef>> ListIndividualByContactIdsAsync(
        IReadOnlyCollection<Guid> contactIds, CancellationToken ct);
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

/// <summary>Conversa individual de um contato, reduzida ao necessário pra escolher UMA por contato.
/// <paramref name="LastActivityAt"/> é <c>LastMessageAt ?? CreatedAt</c> — o mesmo critério de
/// ordenação do <see cref="IConversationRepository.GetByContactIdAsync"/>.</summary>
public sealed record ContactConversationRef(Guid ContactId, Guid ConversationId, DateTimeOffset LastActivityAt);
