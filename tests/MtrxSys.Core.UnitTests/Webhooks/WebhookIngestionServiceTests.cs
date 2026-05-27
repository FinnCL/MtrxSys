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
    private readonly IWahaClient _waha = Substitute.For<IWahaClient>();
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
            _waha,
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
        // Resposta normal (não "sair") NÃO dispara confirmação de saída.
        await _waha.DidNotReceive().SendTextAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task Ingest_optout_via_lid_resolves_phone_and_opts_out()
    {
        var svc = BuildService();
        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        _contacts.GetByPhoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Contact?)null);
        _waha.ResolveLidToPhoneE164Async("default", "91672436301905@lid", Arg.Any<CancellationToken>())
            .Returns("+5511921404487");

        Contact? captured = null;
        _contacts.When(x => x.AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<Contact>());

        var evt = new WahaWebhookEvent(
            "message",
            "default",
            new WahaMessagePayload("wa-lid-sair", 1700000000, "91672436301905@lid", null, false, "SAIR", false, null, null));

        await svc.IngestAsync(evt, CancellationToken.None);

        // Resolveu o LID pro telefone real e marcou opt-out + Descartado.
        captured.Should().NotBeNull();
        captured!.Phone.E164.Should().Be("+5511921404487");
        captured.OptOutAt.Should().NotBeNull();
        captured.Stage.Should().Be(ContactStage.Lost);
        // E enviou a confirmação de saída (uma vez) pro chat de origem.
        await _waha.Received(1).SendTextAsync("default", "91672436301905@lid", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_skips_outbound_echo_when_core_already_persisted()
    {
        // O disparo já gravou a mensagem usando o "core" do id. O eco do WAHA chega serializado
        // por @lid e com sufixo "_out" — deve ser reconhecido como duplicado pelo core e ignorado
        // (antes os ids divergiam e a mensagem aparecia em dobro no chat).
        var svc = BuildService();
        const string core = "3EB0FBA09A783EF71B9EED";
        var existing = ChatMessage.Create(
            Guid.NewGuid(), Guid.NewGuid(), core,
            MessageDirection.Outbound, null, "oi", DateTimeOffset.UtcNow);
        _messages.GetByWaMessageIdAsync(core, Arg.Any<CancellationToken>()).Returns(existing);

        var evt = new WahaWebhookEvent(
            "message.any",
            "default",
            new WahaMessagePayload(
                Id: $"true_157239574847645@lid_{core}_out",
                Timestamp: 1700000000, From: "557193477235@c.us", To: "557186576422@c.us",
                FromMe: true, Body: "oi", HasMedia: false, Media: null, Participant: null));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _messages.DidNotReceive().AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_outbound_echo_via_lid_records_under_recipient_not_own_number()
    {
        // Regressão: o eco de um disparo chega com FromMe=true e o REMETENTE (nosso próprio número)
        // serializado como @lid. A troca pro destinatário só tratava @c.us, então o código resolvia
        // o @lid do nosso número e gravava a mensagem enviada numa "conversa com o próprio número".
        // A conversa de saída tem que ser SEMPRE o destinatário (To), nunca o remetente.
        var svc = BuildService();
        const string core = "3EB0AB12CD34EF56";
        // Eco ainda não de-duplicado (corrida: o registro proativo do disparo ainda não commitou).
        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        _conversations.GetByContactIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        // Se o @lid do remetente fosse resolvido, cairia no NOSSO número — exatamente o bug.
        _waha.ResolveLidToPhoneE164Async("default", "157239574847645@lid", Arg.Any<CancellationToken>())
            .Returns("+557193477235");

        var evt = new WahaWebhookEvent(
            "message.any",
            "default",
            new WahaMessagePayload(
                Id: $"true_157239574847645@lid_{core}_out",
                Timestamp: 1700000000,
                From: "157239574847645@lid",  // nosso número, oculto como @lid
                To: "557186576422@c.us",       // destinatário do disparo
                FromMe: true, Body: "oi", HasMedia: false, Media: null, Participant: null));

        await svc.IngestAsync(evt, CancellationToken.None);

        // Não pode resolver/atribuir pelo remetente (nós): a conversa de saída é o destinatário.
        await _waha.DidNotReceive().ResolveLidToPhoneE164Async(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // A conversa criada é a do destinatário (To), não a "comigo mesmo".
        await _conversations.Received(1).AddAsync(
            Arg.Is<Conversation>(c => c.WaChatId == "557186576422@c.us"),
            Arg.Any<CancellationToken>());
        // A mensagem enviada continua sendo gravada (no lugar certo).
        await _messages.Received(1).AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_outbound_group_message_stays_in_group_conversation()
    {
        // Mensagem NOSSA (FromMe) num grupo: o From já é o id do grupo, não o nosso número. A regra
        // de "trocar pro To" vale só pra conversa individual; em grupo, sem To, o eco seria descartado.
        // Deve ser gravado na conversa do próprio grupo.
        var svc = BuildService();
        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);

        var evt = new WahaWebhookEvent(
            "message.any",
            "default",
            new WahaMessagePayload(
                Id: "true_120363041234567890@g.us_3EB0AA_out",
                Timestamp: 1700000000,
                From: "120363041234567890@g.us",  // o grupo é o próprio chat
                To: null,                          // grupo costuma vir sem To
                FromMe: true, Body: "promo", HasMedia: false, Media: null, Participant: null));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _conversations.Received(1).AddAsync(
            Arg.Is<Conversation>(c => c.WaChatId == "120363041234567890@g.us" && c.IsGroup),
            Arg.Any<CancellationToken>());
        await _messages.Received(1).AddAsync(Arg.Any<ChatMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_reuses_existing_contact_conversation_instead_of_creating_duplicate()
    {
        // Mensagem chega por um chatId ainda não visto, mas o contato já tem conversa (ex.: criada
        // pelo disparo via @c.us). Deve reaproveitar essa conversa, não criar uma 2ª pro mesmo
        // contato.
        var svc = BuildService();
        var phone = new MtrxSys.Core.Validation.BrazilPhoneValidator().NormalizeTrusted("+5511999998888");
        var contact = Contact.Create(Guid.NewGuid(), phone, "Fulano", null, null, _clock.UtcNow);
        var existingConv = Conversation.Create(
            Guid.NewGuid(), "5511999998888@c.us", contact.Id, "Fulano", isGroup: false, createdAt: _clock.UtcNow);

        _messages.GetByWaMessageIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ChatMessage?)null);
        _contacts.GetByPhoneAsync("+5511999998888", Arg.Any<CancellationToken>()).Returns(contact);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Conversation?)null);
        _conversations.GetByContactIdAsync(contact.Id, Arg.Any<CancellationToken>()).Returns(existingConv);

        var evt = new WahaWebhookEvent(
            "message",
            "default",
            new WahaMessagePayload("wa-reply", 1700000000, "5511999998888@c.us", null, false, "voltei", false, null, null));

        await svc.IngestAsync(evt, CancellationToken.None);

        await _conversations.DidNotReceive().AddAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>());
        await _messages.Received(1).AddAsync(
            Arg.Is<ChatMessage>(m => m.ConversationId == existingConv.Id && m.Body == "voltei"),
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
