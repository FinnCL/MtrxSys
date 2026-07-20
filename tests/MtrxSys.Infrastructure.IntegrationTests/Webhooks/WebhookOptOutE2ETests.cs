using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Dispatch;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.SharedLedger;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Webhooks;

/// <summary>
/// E2E do opt-out contra Postgres REAL (Testcontainers): exercita o caminho que é CÓDIGO NOSSO e
/// engine-independente — webhook de "SAIR" → ingestão → resolve telefone → marca opt-out + estágio
/// "Descartado" + grava mensagem inbound. O WAHA é mockado (a confirmação de saída não envia nada);
/// o único hop que isto NÃO cobre é a entrega real do webhook pelo WAHA (precisa de mensagem real).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001",
    Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync, que o analisador não reconhece.")]
public sealed class WebhookOptOutE2ETests : IAsyncLifetime
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
        public DateTimeOffset UtcNow => new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
    }

    private WebhookIngestionService BuildService() =>
        new(new ConversationRepository(_db),
            new ChatMessageRepository(_db),
            new ContactRepository(_db),
            new ContactStageChangeRepository(_db),
            _waha,
            new UnitOfWork(_db),
            new FixedClock(),
            _phones,
            new NoOpSharedPhoneLedger(),
            new SendAuditRepository(_db),
            Substitute.For<IResponderDispatchEnqueuer>(),
            Options.Create(new DispatchOptions { SessionId = "default" }),
            NullLogger<WebhookIngestionService>.Instance);

    private static WahaWebhookEvent InboundFrom(string chatId, string body, string id) =>
        new(WahaEvents.Message, "default",
            new WahaMessagePayload(
                Id: id, Timestamp: 1749643200, From: chatId, To: null, FromMe: false,
                Body: body, HasMedia: null, Media: null, Participant: null, NotifyName: "Teste"));

    [Fact]
    public async Task Sair_marca_contato_como_opt_out_e_descartado()
    {
        // Contato já disparado (Novo/Lead, sem opt-out).
        var contact = Contact.Create(
            Guid.NewGuid(), _phones.Validate("11988887777").Value!,
            name: "Fulano", groupTag: "Avulsos", theme: null,
            optInAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        await repo.AddAsync(contact, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();

        await BuildService().IngestAsync(
            InboundFrom("5511988887777@c.us", "SAIR", "sair-e2e"), CancellationToken.None);

        _db.ChangeTracker.Clear();
        var saved = await _db.Contacts.SingleAsync(c => c.Phone.E164 == "+5511988887777");
        saved.OptOutAt.Should().NotBeNull("o 'SAIR' deve registrar o opt-out");
        saved.Stage.Should().Be(ContactStage.Lost, "opt-out move pra 'Descartado'");

        var msg = await _db.ChatMessages
            .OrderByDescending(m => m.Timestamp)
            .FirstOrDefaultAsync(m => m.Body == "SAIR");
        msg.Should().NotBeNull("a mensagem de entrada deve ser gravada");
        msg!.Direction.Should().Be(MessageDirection.Inbound);
    }

    [Fact]
    public async Task Resposta_comum_marca_respondeu_sem_opt_out()
    {
        var contact = Contact.Create(
            Guid.NewGuid(), _phones.Validate("11977776666").Value!,
            name: null, groupTag: "Avulsos", theme: null,
            optInAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        await repo.AddAsync(contact, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();

        await BuildService().IngestAsync(
            InboundFrom("5511977776666@c.us", "Oi, quero saber mais sobre", "resp-e2e"), CancellationToken.None);

        _db.ChangeTracker.Clear();
        var saved = await _db.Contacts.SingleAsync(c => c.Phone.E164 == "+5511977776666");
        saved.OptOutAt.Should().BeNull("resposta comum NÃO é opt-out");
        saved.Stage.Should().Be(ContactStage.Qualified, "quem respondeu sai de 'Novo' pra 'Respondeu'");
    }

    // E2E da frase com acento: o projeto roda InvariantGlobalization=true (string.Normalize é no-op),
    // então o opt-out só funciona porque o detector remove acento sem ICU. Sem isso, "não quero
    // receber" não casaria e o contato continuaria recebendo — este teste cobre o fluxo real.
    [Fact]
    public async Task Frase_com_acento_marca_opt_out()
    {
        var contact = Contact.Create(
            Guid.NewGuid(), _phones.Validate("11966665555").Value!,
            name: "Acentuado", groupTag: "Avulsos", theme: null,
            optInAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        await repo.AddAsync(contact, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();

        await BuildService().IngestAsync(
            InboundFrom("5511966665555@c.us", "não quero receber mais mensagens", "acento-e2e"),
            CancellationToken.None);

        _db.ChangeTracker.Clear();
        var saved = await _db.Contacts.SingleAsync(c => c.Phone.E164 == "+5511966665555");
        saved.OptOutAt.Should().NotBeNull("frase de saída com acento deve registrar opt-out");
        saved.Stage.Should().Be(ContactStage.Lost, "opt-out move pra 'Descartado'");
    }

    // E2E do falso-positivo: "para" é preposição. Um lead interessado respondendo "é para hoje?"
    // NÃO pode ser descadastrado — isso seria supressão global e irreversível de quem quer comprar.
    [Fact]
    public async Task Preposicao_para_em_resposta_curta_nao_marca_opt_out()
    {
        var contact = Contact.Create(
            Guid.NewGuid(), _phones.Validate("11955554444").Value!,
            name: "Interessado", groupTag: "Avulsos", theme: null,
            optInAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        await repo.AddAsync(contact, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();

        await BuildService().IngestAsync(
            InboundFrom("5511955554444@c.us", "é para hoje?", "para-e2e"), CancellationToken.None);

        _db.ChangeTracker.Clear();
        var saved = await _db.Contacts.SingleAsync(c => c.Phone.E164 == "+5511955554444");
        saved.OptOutAt.Should().BeNull("'para' como preposição NÃO é opt-out");
        saved.Stage.Should().Be(ContactStage.Qualified, "lead interessado vira 'Respondeu', não 'Descartado'");
    }
}
