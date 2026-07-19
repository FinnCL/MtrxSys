using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using NSubstitute;
using Xunit;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>Trava a fonte única do dedup cross-chip (FilterOutSuppressedAsync) — usada pelo disparo,
/// pela prévia e pelo reset do aquecimento. A tradução SQL do cross-chip em si é coberta pelos
/// E2E (NpgsqlSharedPhoneLedgerE2ETests); aqui garantimos o contrato do wrapper.</summary>
public sealed class SharedPhoneLedgerExtensionsTests
{
    private static Contact WithPhone(string e164) =>
        Contact.Create(Guid.NewGuid(), PhoneNumber.FromValidatedE164(e164), "x", null, null, null);

    private static readonly Contact A = WithPhone("+5511999990001");
    private static readonly Contact B = WithPhone("+5511999990002");

    [Fact]
    public async Task Observe_ou_Off_nao_suprime_ninguem_e_nem_consulta_o_registro()
    {
        var ledger = Substitute.For<ISharedPhoneLedger>();
        ledger.IsEnforcing.Returns(false);

        var kept = await ledger.FilterOutSuppressedAsync([A, B], CancellationToken.None);

        kept.Should().BeEquivalentTo(new[] { A, B });
        await ledger.DidNotReceiveWithAnyArgs().GetSuppressedAsync(default!, default);
    }

    [Fact]
    public async Task Enforce_remove_so_os_suprimidos_mantendo_os_demais()
    {
        var ledger = Substitute.For<ISharedPhoneLedger>();
        ledger.IsEnforcing.Returns(true);
        ledger.GetSuppressedAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ => new HashSet<string>(StringComparer.Ordinal) { B.Phone.E164 });

        var kept = await ledger.FilterOutSuppressedAsync([A, B], CancellationToken.None);

        kept.Should().ContainSingle().Which.Should().Be(A);
    }

    [Fact]
    public async Task Lista_vazia_e_no_op_sem_consultar_o_registro()
    {
        var ledger = Substitute.For<ISharedPhoneLedger>();
        ledger.IsEnforcing.Returns(true);

        var kept = await ledger.FilterOutSuppressedAsync([], CancellationToken.None);

        kept.Should().BeEmpty();
        await ledger.DidNotReceiveWithAnyArgs().GetSuppressedAsync(default!, default);
    }
}
