using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Contacts;

/// <summary>Prova, contra Postgres REAL, que a regra "engajado" (fonte única <c>ContactStages</c>)
/// traduz e funciona nos dois caminhos EF: o filtro <c>EngagedOnly</c> do disparo e o
/// <c>ClearLastSentForEngagedAsync</c> do reset do aquecimento. Em especial, garante que o EF traduz
/// <c>!ContactStages.NonEngaged.Contains(c.Stage)</c> em SQL (NOT IN) com o value-converter do enum —
/// senão a unificação da regra teria virado erro de runtime no caminho anti-ban.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class ContactEngagedFilterE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var options = new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options;
        _db = new MtrxDbContext(options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private async Task<Contact> SeedAsync(string digits, ContactStage stage, bool sent = false)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(digits).Value!, name: null,
            groupTag: null, theme: null, optInAt: T0);
        if (stage != ContactStage.Lead)
        {
            c.ChangeStage(stage, T0);
        }
        if (sent)
        {
            c.RegisterSend(T0);
        }
        await new ContactRepository(_db).AddAsync(c, CancellationToken.None);
        await new UnitOfWork(_db).SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
        return c;
    }

    [Fact]
    public async Task EngagedOnly_traz_so_quem_respondeu_ou_avancou()
    {
        await SeedAsync("11999990001", ContactStage.Lead);      // Novo — fora
        await SeedAsync("11999990002", ContactStage.Qualified); // Respondeu — entra
        await SeedAsync("11999990003", ContactStage.Lost);      // Descartado — fora
        await SeedAsync("11999990004", ContactStage.Won);       // Cliente — entra

        var engaged = await new ContactRepository(_db)
            .ListByFilterAsync(new ContactFilter(EngagedOnly: true), CancellationToken.None);

        engaged.Select(c => c.Stage).Should()
            .BeEquivalentTo(new[] { ContactStage.Qualified, ContactStage.Won });
    }

    [Fact]
    public async Task ClearLastSentForEngaged_zera_so_engajados()
    {
        var lead = await SeedAsync("11999990011", ContactStage.Lead, sent: true);
        var qualified = await SeedAsync("11999990012", ContactStage.Qualified, sent: true);
        var lost = await SeedAsync("11999990013", ContactStage.Lost, sent: true);

        var cleared = await new ContactRepository(_db).ClearLastSentForEngagedAsync(CancellationToken.None);

        cleared.Should().Be(1, "só o engajado (Qualified) é liberado");
        _db.ChangeTracker.Clear();
        (await _db.Contacts.SingleAsync(c => c.Id == qualified.Id)).LastSentAt.Should().BeNull();
        (await _db.Contacts.SingleAsync(c => c.Id == lead.Id)).LastSentAt.Should().NotBeNull("Novo não é liberado");
        (await _db.Contacts.SingleAsync(c => c.Id == lost.Id)).LastSentAt.Should().NotBeNull("Descartado não é liberado");
    }
}
