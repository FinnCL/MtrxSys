namespace MtrxSys.Core.Application.Abstractions;

/// <summary>
/// Contador de requisições feitas ao motor de busca — pra exibir o consumo no painel (ex.: acompanhar
/// a faixa grátis do Serper). Assíncrono porque a implementação compartilhada (entre os 10 ambientes)
/// grava num banco; a local é em memória. É INFORMATIVO: o número oficial/cobrado é o do provedor.
/// </summary>
public interface ISearchUsageMeter
{
    /// <summary>Registra UMA requisição feita ao motor de busca. Nunca lança (fail-open).</summary>
    Task RecordAsync(CancellationToken ct);

    /// <summary>Total de requisições contabilizadas. Nunca lança (fail-open → 0 em falha).</summary>
    Task<long> GetCountAsync(CancellationToken ct);
}
