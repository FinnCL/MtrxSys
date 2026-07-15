using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Placar de uma conversa desde a âncora do aquecimento: quantas entraram e quantas saíram.
/// ConversationId serve pro atalho "abrir" da UI levar direto ao Chat; WaChatId é o que permite
/// casar a conversa com uma pessoa do círculo (via WahaChatIdentifier.TryExtractPhoneE164).</summary>
public sealed record ConversationTally(
    Guid ConversationId, string? Title, string WaChatId, int Inbound, int Outbound);

/// <summary>Leituras da Fase Humana sobre chat_messages. SÓ LEITURA — a fase é função pura dos
/// dados (ver HumanPhaseGate), então nada aqui escreve.</summary>
public interface IHumanPhaseProgressRepository
{
    /// <summary>Datas-Brasília DISTINTAS com ≥1 mensagem de saída desde a âncora (inclusive).
    /// Mede "dias em que o chip foi de fato usado" — o mesmo critério da curva.</summary>
    Task<int> CountOutboundActiveDaysAsync(DateOnly since, CancellationToken ct);

    /// <summary>Placar por conversa NÃO-GRUPO desde a âncora. Grupo não conta: a fase é sobre
    /// conversa de pessoa pra pessoa.</summary>
    Task<IReadOnlyList<ConversationTally>> ListConversationTalliesAsync(DateOnly since, CancellationToken ct);

    /// <summary>Carimbos crus (direção + instante) das conversas indicadas, desde a âncora. Serve ao
    /// envio automático, que precisa de coisas que um agregado não dá: quando foi a última saída
    /// (pro intervalo), quantas saíram HOJE (pro teto) e se a pessoa respondeu depois da última
    /// nossa (pra parar de insistir). O volume é pequeno por construção — a fase dura dias e o
    /// círculo tem poucas pessoas —, então sai uma query só e a conta é feita em memória, onde as
    /// regras ficam legíveis e testáveis.</summary>
    Task<IReadOnlyList<MessageStamp>> ListStampsForConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds, DateOnly since, CancellationToken ct);
}

public sealed record MessageStamp(Guid ConversationId, MessageDirection Direction, DateTimeOffset Timestamp);
