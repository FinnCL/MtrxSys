using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Infrastructure.SharedLedger;
using Xunit;

namespace MtrxSys.Infrastructure.IntegrationTests.SharedLedger;

// Trava o contrato de segurança: com o recurso DESLIGADO (NoOp, o padrão), tudo é inerte e nunca
// lança — o disparo se comporta exatamente como se o registro compartilhado não existisse.
public sealed class NoOpSharedPhoneLedgerTests
{
    private const string Phone = "+5571999999999";
    private readonly NoOpSharedPhoneLedger _ledger = new();

    [Fact]
    public void Is_disabled_and_not_enforcing()
    {
        _ledger.IsEnabled.Should().BeFalse();
        _ledger.IsEnforcing.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatus_is_always_none()
        => (await _ledger.GetStatusAsync(Phone, CancellationToken.None)).Should().Be(SharedLedgerStatus.None);

    [Fact]
    public async Task GetSuppressed_is_always_empty()
        => (await _ledger.GetSuppressedAsync([Phone], CancellationToken.None)).Should().BeEmpty();

    [Fact]
    public async Task Marks_are_no_ops_and_never_throw()
    {
        var act = async () =>
        {
            await _ledger.MarkSentAsync(Phone, CancellationToken.None);
            await _ledger.MarkOptOutAsync(Phone, CancellationToken.None);
        };
        await act.Should().NotThrowAsync();
    }
}
