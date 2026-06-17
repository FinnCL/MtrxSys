using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>Último erro da busca, em memória (singleton). Diagnóstico do momento — não persiste.</summary>
internal sealed class InMemorySearchStatus : ISearchStatus
{
    private volatile string? _lastError;

    public void SetLastError(string? error) => _lastError = string.IsNullOrWhiteSpace(error) ? null : error;

    public string? LastError => _lastError;
}
