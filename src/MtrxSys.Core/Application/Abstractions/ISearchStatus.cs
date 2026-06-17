namespace MtrxSys.Core.Application.Abstractions;

/// <summary>
/// Guarda o ÚLTIMO motivo de falha da busca por nicho (ex.: "Serper recusou: limite/sem crédito"),
/// pra o painel mostrar POR QUE a busca parou em vez de só não trazer nada. Em memória, por processo
/// — é diagnóstico do momento, não histórico. Thread-safe.
/// </summary>
public interface ISearchStatus
{
    /// <summary>Define o último erro da busca; <c>null</c> limpa (chamar no sucesso).</summary>
    void SetLastError(string? error);

    /// <summary>Último erro reportado, ou null se a última busca foi ok.</summary>
    string? LastError { get; }
}
