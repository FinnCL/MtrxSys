using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.SharedLedger;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Contacts;

/// <summary>
/// E2E do opt-out por LINK contra Postgres REAL (Testcontainers): prova a cadeia que o endpoint
/// /sair executa de ponta a ponta — token assinado (como o dispatcher monta ao compor) →
/// verificação → <see cref="MarkContactOptOutUseCase"/> → contato marcado opt-out + estágio
/// "Descartado" + auditoria, na persistência real; idempotente; token inválido não resolve.
/// O único hop não coberto é a camada HTTP/HTML do endpoint (o projeto não tem WebApplicationFactory).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class OptOutLinkE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();

    private static readonly OptOutLinkSigner Signer = new(
        Options.Create(new JwtOptions { SigningKey = "e2e-signing-key-com-pelo-menos-32-caracteres" }));
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = new MtrxDbContext(new DbContextOptionsBuilder<MtrxDbContext>()
            .UseNpgsql(_pg.GetConnectionString()).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private MarkContactOptOutUseCase BuildUseCase() =>
        new(new ContactRepository(_db),
            new ContactStageChangeRepository(_db),
            new NoOpSharedPhoneLedger(),
            new UnitOfWork(_db),
            new FixedClock(),
            NullLogger<MarkContactOptOutUseCase>.Instance);

    private async Task<Contact> SeedContactAsync(string phone)
    {
        var c = Contact.Create(
            Guid.NewGuid(), _phones.Validate(phone).Value!, "Fulano", "Avulsos", null,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await new ContactRepository(_db).AddAsync(c, CancellationToken.None);
        await new UnitOfWork(_db).SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
        return c;
    }

    [Fact]
    public async Task Token_do_link_verifica_marca_optout_e_descarta_no_banco()
    {
        var contact = await SeedContactAsync("11988887777");

        // 1) Dispatcher monta o link: assina o token pro contato (validade longa).
        var token = Signer.Sign(contact.Id, Now.AddDays(90));

        // 2) Endpoint /sair: verifica o token e resolve o contato.
        Signer.TryVerify(token, Now, out var contactId).Should().BeTrue();
        contactId.Should().Be(contact.Id);

        // 3) POST /sair/confirm → marca opt-out na persistência real.
        (await BuildUseCase().MarkAsync(contactId, CancellationToken.None)).Should().Be(OptOutResult.Marked);

        _db.ChangeTracker.Clear();
        var saved = await _db.Contacts.SingleAsync(c => c.Id == contact.Id);
        saved.OptOutAt.Should().NotBeNull("o clique no link deve registrar o opt-out");
        saved.Stage.Should().Be(ContactStage.Lost, "opt-out move pra 'Descartado'");

        var change = await _db.Set<ContactStageChange>().SingleOrDefaultAsync(s => s.ContactId == contact.Id);
        change.Should().NotBeNull("a transição Lead→Descartado deve ser auditada");
        change!.ToStage.Should().Be(ContactStage.Lost);
    }

    [Fact]
    public async Task Segundo_clique_no_link_e_idempotente()
    {
        var contact = await SeedContactAsync("11977776666");
        var token = Signer.Sign(contact.Id, Now.AddDays(90));
        Signer.TryVerify(token, Now, out var contactId).Should().BeTrue();

        (await BuildUseCase().MarkAsync(contactId, CancellationToken.None)).Should().Be(OptOutResult.Marked);
        _db.ChangeTracker.Clear();
        (await BuildUseCase().MarkAsync(contactId, CancellationToken.None)).Should().Be(OptOutResult.AlreadyOptedOut);

        _db.ChangeTracker.Clear();
        var changes = await _db.Set<ContactStageChange>().CountAsync(s => s.ContactId == contact.Id);
        changes.Should().Be(1, "o 2º clique não registra nova transição");
    }

    [Fact]
    public async Task Token_expirado_ou_adulterado_nao_resolve()
    {
        var contact = await SeedContactAsync("11966665555");

        var expired = Signer.Sign(contact.Id, Now.AddMinutes(-1));
        Signer.TryVerify(expired, Now, out _).Should().BeFalse("token expirado não vale");

        var valid = Signer.Sign(contact.Id, Now.AddDays(90));
        var tampered = valid[..^2] + (valid[^1] == 'A' ? "BB" : "AA");
        Signer.TryVerify(tampered, Now, out _).Should().BeFalse("assinatura adulterada não vale");
    }
}
