using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Webhooks;

public sealed class OptOutReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IConversationRepository _conversations = Substitute.For<IConversationRepository>();
    private readonly IChatMessageRepository _messages = Substitute.For<IChatMessageRepository>();
    private readonly IContactStageChangeRepository _stageChanges = Substitute.For<IContactStageChangeRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISharedPhoneLedger _ledger = Substitute.For<ISharedPhoneLedger>();

    private OptOutReconciler Build()
    {
        _clock.UtcNow.Returns(Now);
        return new OptOutReconciler(
            _contacts, _conversations, _messages, _stageChanges, _uow, _clock, _ledger,
            NullLogger<OptOutReconciler>.Instance);
    }

    // Monta o cenário: um contato ativo com uma conversa cujo histórico tem UM "sair" no instante dado.
    private Contact ArrangeContactWithOptOutMessage(DateTimeOffset messageAt)
    {
        var contact = Contact.Create(
            Guid.NewGuid(), PhoneNumber.FromValidatedE164("+557191184916"), "Fulano", null, null, null);
        var conversation = Conversation.Create(
            Guid.NewGuid(), "557191184916@c.us", contact.Id, null, isGroup: false, Now.AddDays(-30));
        var msg = ChatMessage.Create(
            Guid.NewGuid(), conversation.Id, "wamid.1", MessageDirection.Inbound, "+557191184916",
            "sair", messageAt);

        _contacts.ListByFilterAsync(Arg.Any<ContactFilter>(), Arg.Any<CancellationToken>())
            .Returns([contact]);
        _conversations.ListIndividualByContactIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new ContactConversationRef(contact.Id, conversation.Id, conversation.CreatedAt)]);
        _messages.ListInboundByConversationsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new InboundMessageRef(conversation.Id, msg.Body, msg.Timestamp)]);
        return contact;
    }

    [Fact]
    public async Task Reconcilia_quando_o_sair_ficou_sem_classificacao()
    {
        var contact = ArrangeContactWithOptOutMessage(Now.AddDays(-2));

        var result = await Build().ReconcileAsync(CancellationToken.None);

        result.Count.Should().Be(1);
        contact.OptOutAt.Should().NotBeNull();
        contact.Stage.Should().Be(ContactStage.Lost);
        await _ledger.Received(1).MarkOptOutAsync(contact.Phone.E164, Arg.Any<CancellationToken>());
    }

    // REGRESSÃO: o "Reativar" não grudava. O reconciliador reencontrava a MESMA mensagem antiga no
    // histórico e devolvia o contato pra "Saiu" no ciclo seguinte do auto-sync — pra sempre.
    [Fact]
    public async Task Nao_reverte_reativacao_manual_com_o_sair_antigo()
    {
        var contact = ArrangeContactWithOptOutMessage(Now.AddDays(-2));
        contact.OptOut(Now.AddDays(-2));
        contact.Reactivate(Now.AddDays(-1)); // operador religou DEPOIS da mensagem

        var result = await Build().ReconcileAsync(CancellationToken.None);

        result.Count.Should().Be(0);
        contact.OptOutAt.Should().BeNull();
        contact.Stage.Should().Be(ContactStage.Lead);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _ledger.DidNotReceive().MarkOptOutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // O contrário also vale: um "sair" NOVO, posterior à reativação, tem que voltar a suprimir.
    [Fact]
    public async Task Reaplica_optout_quando_o_sair_e_posterior_a_reativacao()
    {
        var contact = ArrangeContactWithOptOutMessage(Now.AddHours(-1));
        contact.OptOut(Now.AddDays(-5));
        contact.Reactivate(Now.AddDays(-2)); // reativado ANTES da mensagem nova

        var result = await Build().ReconcileAsync(CancellationToken.None);

        result.Count.Should().Be(1);
        contact.OptOutAt.Should().Be(Now);
        contact.Stage.Should().Be(ContactStage.Lost);
    }
}
