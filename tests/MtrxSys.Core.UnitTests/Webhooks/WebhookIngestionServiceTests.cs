using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Webhooks;

public sealed class WebhookIngestionServiceTests
{
    private readonly IConversationRepository _conversations = Substitute.For<IConversationRepository>();
    private readonly IChatMessageRepository _messages = Substitute.For<IChatMessageRepository>();
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IContactStageChangeRepository _stageChanges = Substitute.For<IContactStageChangeRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private WebhookIngestionService BuildService()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new DispatchOptions { SessionId = "default" });
        return new WebhookIngestionService(
            _conversations,
            _messages,
            _contacts,
            _stageChanges,
            _uow,
            _clock,
            new MtrxSys.Core.Validation.BrazilPhoneValidator(),
            opts,
            NullLogger<WebhookIngestionService>.Instance);
    }

    [Fact]
    public async Task Ingest_skips_unsupported_event_types()
    {
        var svc = BuildService();
        var evt = new WahaWebhookEvent("session.status", "default", null);

        await svc.IngestAsync(evt, CancellationToken.None);

        await _messages.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_skips_mismatched_session()
    {
        var svc = BuildService();
        var evt = new WahaWebhookEvent(
            "message",
            "other",
            new WahaMessagePayload("id1", 100, "5511@c.us", null, false, "hi", false, null, null));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _messages.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_skips_duplicate_message_id()
    {
        var svc = BuildService();
        var existing = ChatMessage.Create(
            Guid.NewGuid(), Guid.NewGuid(), "wa-1",
            MessageDirection.Inbound, "+5511", "old",
            DateTimeOffset.UtcNow);
        _messages.GetByWaMessageIdAsync("wa-1", Arg.Any<CancellationToken>()).Returns(existing);

        var evt = new WahaWebhookEvent(
            "message",
            "default",
            new WahaMessagePayload("wa-1", 100, "5511@c.us", null, false, "again", false, null, null));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _messages.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_auto_creates_contact_and_conversation_for_inbound_1on1()
    {
        var svc = BuildService();
        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _contacts.GetByPhoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Contact?)null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);

        var evt = new WahaWebhookEvent(
            "message",
            "default",
            new WahaMessagePayload("wa-new", 1700000000, "5511999998888@c.us", null, false, "olá", false, null, null));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _contacts.Received(1).AddAsync(
            Arg.Is<Contact>(c => c.Phone.E164 == "+5511999998888"),
            Arg.Any<CancellationToken>());
        // Mensagem recebida promove o contato de "Novo" (Lead) para "Respondeu" (Qualified).
        await _stageChanges.Received(1).AddAsync(
            Arg.Is<ContactStageChange>(c => c.FromStage == ContactStage.Lead && c.ToStage == ContactStage.Qualified),
            Arg.Any<CancellationToken>());
        await _conversations.Received(1).AddAsync(
            Arg.Is<Conversation>(c => c.WaChatId == "5511999998888@c.us" && !c.IsGroup),
            Arg.Any<CancellationToken>());
        await _messages.Received(1).AddAsync(
            Arg.Is<ChatMessage>(m =>
                m.WaMessageId == "wa-new" &&
                m.Direction == MessageDirection.Inbound &&
                m.AuthorPhone == "+5511999998888" &&
                m.Body == "olá"),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_inbound_optout_keyword_opts_out_and_marks_lost()
    {
        var svc = BuildService();
        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        _contacts.GetByPhoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Contact?)null);

        Contact? captured = null;
        _contacts.When(x => x.AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<Contact>());

        var evt = new WahaWebhookEvent(
            "message",
            "default",
            new WahaMessagePayload("wa-out", 1700000000, "5511999990000@c.us", null, false, "SAIR", false, null, null));

        await svc.IngestAsync(evt, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.OptOutAt.Should().NotBeNull();
        captured.Stage.Should().Be(ContactStage.Lost);
        await _stageChanges.Received(1).AddAsync(
            Arg.Is<ContactStageChange>(c => c.ToStage == ContactStage.Lost),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_group_message_uses_participant_for_author()
    {
        var svc = BuildService();
        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);

        var evt = new WahaWebhookEvent(
            "message",
            "default",
            new WahaMessagePayload(
                "wa-grp", 1700000000, "120363@g.us", null, false, "oi grupo",
                false, null, "5511777776666@c.us"));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _contacts.DidNotReceive().AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
        await _conversations.Received(1).AddAsync(
            Arg.Is<Conversation>(c => c.WaChatId == "120363@g.us" && c.IsGroup && c.ContactId == null),
            Arg.Any<CancellationToken>());
        await _messages.Received(1).AddAsync(
            Arg.Is<ChatMessage>(m => m.AuthorPhone == "+5511777776666"),
            Arg.Any<CancellationToken>());
    }
}
