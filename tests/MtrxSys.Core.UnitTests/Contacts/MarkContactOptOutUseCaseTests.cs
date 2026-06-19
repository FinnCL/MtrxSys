using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Contacts;

public sealed class MarkContactOptOutUseCaseTests
{
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IContactStageChangeRepository _stageChanges = Substitute.For<IContactStageChangeRepository>();
    private readonly ISharedPhoneLedger _ledger = Substitute.For<ISharedPhoneLedger>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private MarkContactOptOutUseCase Build()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));
        return new MarkContactOptOutUseCase(
            _contacts, _stageChanges, _ledger, _uow, _clock,
            NullLogger<MarkContactOptOutUseCase>.Instance);
    }

    private static Contact Fresh() =>
        Contact.Create(Guid.NewGuid(), PhoneNumber.FromValidatedE164("+5511999998888"), "Maria", null, null, null);

    [Fact]
    public async Task NotFound_quando_contato_nao_existe_nao_marca_nada()
    {
        var id = Guid.NewGuid();
        _contacts.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((Contact?)null);

        (await Build().MarkAsync(id, CancellationToken.None)).Should().Be(OptOutResult.NotFound);

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _ledger.DidNotReceive().MarkOptOutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyOptedOut_e_idempotente_quando_ja_saiu()
    {
        var c = Fresh();
        c.OptOut(DateTimeOffset.UtcNow.AddDays(-1)); // já estava opt-out
        _contacts.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        (await Build().MarkAsync(c.Id, CancellationToken.None)).Should().Be(OptOutResult.AlreadyOptedOut);

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _ledger.DidNotReceive().MarkOptOutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marked_marca_optout_stage_Lost_registra_e_propaga_no_ledger()
    {
        var c = Fresh(); // Stage = Lead, OptOutAt = null
        _contacts.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        (await Build().MarkAsync(c.Id, CancellationToken.None)).Should().Be(OptOutResult.Marked);

        c.OptOutAt.Should().NotBeNull();
        c.Stage.Should().Be(ContactStage.Lost);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _ledger.Received(1).MarkOptOutAsync(c.Phone.E164, Arg.Any<CancellationToken>());
        await _stageChanges.Received(1).AddAsync(Arg.Any<ContactStageChange>(), Arg.Any<CancellationToken>());
    }
}
