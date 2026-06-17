using System.Threading.Channels;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>Canal de coleta em memória. Single-reader: o CollectorWorker. Bounded com descarte na
/// escrita — se já há coletas enfileiradas demais, ignora o excedente (o usuário só dispara de novo).</summary>
internal sealed class InMemoryGroupCollectorChannel : IGroupCollectorChannel
{
    private readonly Channel<CollectRequest> _channel = Channel.CreateBounded<CollectRequest>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public bool TryEnqueue(CollectRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<CollectRequest> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
