using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

public interface IChatMessageRepository
{
    Task<ChatMessage?> GetByWaMessageIdAsync(string waMessageId, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> ListByConversationAsync(Guid conversationId, int limit, int offset, CancellationToken ct);
    /// <summary>Corpo + horário das mensagens RECEBIDAS de VÁRIAS conversas, numa consulta só.
    /// Existe pro <c>OptOutReconciler</c> não fazer uma query por contato (N+1). Projeção pura
    /// (sem tracking, sem entidade): quem chama só precisa decidir se o texto é um pedido de saída.</summary>
    Task<IReadOnlyList<InboundMessageRef>> ListInboundByConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds, CancellationToken ct);
    Task AddAsync(ChatMessage message, CancellationToken ct);
}

/// <summary>O mínimo de uma mensagem recebida pra classificar opt-out: de qual conversa, o texto e quando.</summary>
public sealed record InboundMessageRef(Guid ConversationId, string Body, DateTimeOffset Timestamp);
