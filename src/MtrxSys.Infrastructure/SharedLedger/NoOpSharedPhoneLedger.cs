using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.SharedLedger;

// Implementação usada quando o recurso está desligado (Mode = Off ou sem connection string).
// Tudo é no-op: o disparo se comporta exatamente como antes do registro compartilhado existir.
public sealed class NoOpSharedPhoneLedger : ISharedPhoneLedger
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public bool IsEnabled => false;
    public bool IsEnforcing => false;

    public Task<SharedLedgerStatus> GetStatusAsync(string phoneE164, CancellationToken ct)
        => Task.FromResult(SharedLedgerStatus.None);

    public Task<IReadOnlySet<string>> GetSuppressedAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct)
        => Task.FromResult(Empty);

    public Task MarkSentAsync(string phoneE164, CancellationToken ct) => Task.CompletedTask;

    public Task MarkOptOutAsync(string phoneE164, CancellationToken ct) => Task.CompletedTask;
}
