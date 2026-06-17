namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Canal em memória que liga o endpoint (dispara uma coleta) ao worker (executa em
/// background). Single-process/localhost — não precisa de fila durável.</summary>
public interface IGroupCollectorChannel
{
    /// <summary>Enfileira um pedido de coleta. Retorna false se o canal estiver cheio (descartado).</summary>
    bool TryEnqueue(CollectRequest request);

    /// <summary>Stream de pedidos pro worker consumir. Completa quando o app encerra.</summary>
    IAsyncEnumerable<CollectRequest> DequeueAllAsync(CancellationToken ct);
}

/// <summary>Pedido de coleta. keyword null/vazio = modo variado (sem filtro de nicho).</summary>
public sealed record CollectRequest(string? Keyword);
