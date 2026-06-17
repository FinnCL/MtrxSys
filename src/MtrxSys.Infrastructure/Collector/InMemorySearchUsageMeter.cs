using System.Threading;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>Medidor de uso em memória (singleton): conta requisições ao motor de busca DESTE
/// ambiente. Reinício zera. Usado quando NÃO há banco compartilhado configurado — informativo.</summary>
internal sealed class InMemorySearchUsageMeter : ISearchUsageMeter
{
    private long _count;

    public Task RecordAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }

    public Task<long> GetCountAsync(CancellationToken ct) => Task.FromResult(Interlocked.Read(ref _count));
}
