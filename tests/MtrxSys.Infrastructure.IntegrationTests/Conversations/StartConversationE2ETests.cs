using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Conversations;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.SharedLedger;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Conversations;

/// <summary>
/// E2E de "iniciar conversa pelo Chat" contra Postgres REAL (Testcontainers), WAHA mockado. Prova
/// que as travas do 1º contato valem: número que não tem WhatsApp e contato em opt-out são barrados
/// ANTES de qualquer envio; e que um início válido materializa Contact + Conversation + ChatMessage
/// (Outbound) — a peça que deixa o aquecimento manual visível pro HumanPhaseGate.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001",
    Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync, que o analisador não reconhece.")]
public sealed class StartConversationE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private readonly IWahaClient _waha = Substitute.For<IWahaClient>();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var options = new DbContextOptionsBuilder<MtrxDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;
        _db = new MtrxDbContext(options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    }

    private StartConversationUseCase Build(ISharedPhoneLedger? ledger = null, SessionReadinessTracker? readiness = null) =>
        new(_waha,
            new ContactRepository(_db),
            new ConversationRepository(_db),
            new ChatMessageRepository(_db),
            ledger ?? new NoOpSharedPhoneLedger(),
            readiness ?? SettledTracker(),
            Options.Create(new DispatchOptions { SessionId = "default" }),
            new FixedClock(),
            new UnitOfWork(_db),
            NullLogger<StartConversationUseCase>.Instance);

    // Sessão assentada há muito (WORKING desde 1h antes do FixedClock) → passa a janela de settle (120s).
    private static SessionReadinessTracker SettledTracker()
    {
        var t = new SessionReadinessTracker();
        t.MarkWorking(new DateTimeOffset(2026, 7, 16, 11, 0, 0, TimeSpan.Zero));
        return t;
    }

    [Fact]
    public async Task Numero_sem_whatsapp_e_barrado_e_nada_e_criado()
    {
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WahaSessionStatus.Working);
        _waha.CheckNumberExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WahaNumberCheck(Exists: false, ChatId: null));

        var result = await Build().RunAsync("11988887777", null, "Oi!", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(StartConversationOutcome.NumberNotOnWhatsApp);
        await _waha.DidNotReceive().SendTextAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _db.ChangeTracker.Clear();
        (await _db.Contacts.CountAsync()).Should().Be(0, "número inexistente não vira contato");
        (await _db.Conversations.CountAsync()).Should().Be(0);
        (await _db.ChatMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Contato_em_opt_out_e_barrado_sem_enviar()
    {
        var contact = Contact.Create(
            Guid.NewGuid(), _phones.Validate("11977776666").Value!,
            name: "Saiu", groupTag: "Avulsos", theme: null,
            optInAt: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        contact.OptOut(new FixedClock().UtcNow);
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        await repo.AddAsync(contact, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();

        // Sessão/número OK: o opt-out é checado DEPOIS deles (a forma canônica do check-exists é a que
        // casa o contato). O bloqueio ainda ocorre antes de qualquer envio.
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WahaSessionStatus.Working);
        _waha.CheckNumberExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WahaNumberCheck(Exists: true, ChatId: "5511977776666@c.us"));

        var result = await Build().RunAsync("11977776666", null, "Oi de novo!", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(StartConversationOutcome.OptedOut);
        await _waha.DidNotReceive().SendTextAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _db.ChangeTracker.Clear();
        (await _db.ChatMessages.CountAsync()).Should().Be(0, "opt-out não pode receber mensagem");
        (await _db.Conversations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Opt_out_em_outro_chip_e_barrado_sem_enviar()
    {
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WahaSessionStatus.Working);
        _waha.CheckNumberExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WahaNumberCheck(Exists: true, ChatId: "5511966665555@c.us"));
        var ledger = Substitute.For<ISharedPhoneLedger>();
        ledger.IsEnforcing.Returns(true);
        ledger.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SharedLedgerStatus.OptedOut);

        var result = await Build(ledger).RunAsync("11966665555", null, "Oi!", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(StartConversationOutcome.OptedOut);
        await _waha.DidNotReceive().SendTextAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _db.ChangeTracker.Clear();
        (await _db.ChatMessages.CountAsync()).Should().Be(0);
        (await _db.Contacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sessao_recem_conectada_barra_o_envio()
    {
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WahaSessionStatus.Working);
        // Assentou AGORA (WORKING = FixedClock) → dentro da janela de 120s → deve barrar.
        var fresh = new SessionReadinessTracker();
        fresh.MarkWorking(new FixedClock().UtcNow);

        var result = await Build(readiness: fresh).RunAsync("11999998888", null, "oi", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(StartConversationOutcome.SessionNotSettled);
        await _waha.DidNotReceive().SendTextAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _db.ChangeTracker.Clear();
        (await _db.ChatMessages.CountAsync()).Should().Be(0, "sessão recém-conectada não envia");
    }

    [Fact]
    public async Task Inicio_valido_cria_contato_conversa_e_mensagem_outbound()
    {
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WahaSessionStatus.Working);
        _waha.CheckNumberExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WahaNumberCheck(Exists: true, ChatId: "5511999998888@c.us"));
        _waha.SendTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("wamid.TEST");

        var result = await Build().RunAsync("11999998888", null, "Oi, tudo bem?", CancellationToken.None);

        result.Success.Should().BeTrue();
        // Envia pro chatId canônico devolvido pela checagem (resolve o 9º dígito).
        await _waha.Received(1).SendTextAsync(
            "default", "5511999998888@c.us", "Oi, tudo bem?", Arg.Any<CancellationToken>());

        _db.ChangeTracker.Clear();
        var contact = await _db.Contacts.SingleAsync(c => c.Phone.E164 == "+5511999998888");
        contact.Stage.Should().Be(ContactStage.Lead);

        var conversation = await _db.Conversations.SingleAsync(c => c.ContactId == contact.Id);
        conversation.IsGroup.Should().BeFalse();

        var message = await _db.ChatMessages.SingleAsync(m => m.ConversationId == conversation.Id);
        message.Direction.Should().Be(MessageDirection.Outbound);
        message.Body.Should().Be("Oi, tudo bem?");
        message.WaMessageId.Should().Be("wamid.TEST");
    }
}
