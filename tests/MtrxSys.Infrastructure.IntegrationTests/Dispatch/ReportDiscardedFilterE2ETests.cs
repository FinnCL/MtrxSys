using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Dispatch;

/// <summary>
/// E2E contra Postgres REAL: contatos descartados (soft delete) somem do "Resultado dos envios"
/// (ListReportAsync) E dos contadores do topo (GetStatsAsync) — os dois usam o mesmo filtro. Jobs de
/// contato ATIVO continuam aparecendo/contando. Prova a tradução EF do NOT-EXISTS e a consistência
/// lista×contadores. Exige Docker rodando.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class ReportDiscardedFilterE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

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

    private async Task<Contact> SeedContactWithSentJobAsync(string phone, string group)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(phone).Value!,
            name: phone, groupTag: group, theme: null, optInAt: Now);
        _db.Contacts.Add(c);
        var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, Guid.NewGuid(), Now);
        job.MarkSent($"wa-{phone}", Now);
        _db.DispatchJobs.Add(job);
        await _db.SaveChangesAsync(Ct);
        return c;
    }

    private async Task SeedContactWithPendingJobAsync(string phone, string? importedByPhone)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(phone).Value!,
            name: phone, groupTag: "G", theme: null, optInAt: Now, importedByPhone: importedByPhone);
        _db.Contacts.Add(c);
        _db.DispatchJobs.Add(DispatchJob.Schedule(Guid.NewGuid(), c.Id, Guid.NewGuid(), Now)); // Pending
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task PendingFromCurrentChip_conta_so_a_fila_do_chip_conectado()
    {
        var jobs = new DispatchJobRepository(_db);
        const string chipA = "+5511900000001";
        const string chipB = "+5511900000002";
        await SeedContactWithPendingJobAsync("11955551001", chipA);
        await SeedContactWithPendingJobAsync("11955551002", chipA);
        await SeedContactWithPendingJobAsync("11955551003", chipB); // outro chip → o motor pula

        var statsA = await jobs.GetStatsAsync(chipA, Ct);
        statsA.Pending.Should().Be(3, "os três estão na fila");
        statsA.PendingFromCurrentChip.Should().Be(2, "só a fila do chip conectado sai; a de outro chip é pulada");

        // Chip desconhecido (null) → conta todos como 'do chip' pra o gate da UI ficar OFF (igual ao motor,
        // que só aplica o gate por chip quando o chip é conhecido).
        var statsUnknown = await jobs.GetStatsAsync(null, Ct);
        statsUnknown.PendingFromCurrentChip.Should().Be(3);
    }

    [Fact]
    public async Task Contato_descartado_some_do_report_e_dos_contadores_mas_ativo_permanece()
    {
        var jobs = new DispatchJobRepository(_db);
        var contacts = new ContactRepository(_db);

        var active = await SeedContactWithSentJobAsync("11955550001", "Ativos");
        var toDiscard = await SeedContactWithSentJobAsync("11955550002", "Descartar");

        // Antes de descartar: os dois enviados aparecem e contam.
        var before = await jobs.GetStatsAsync(null, Ct);
        before.Sent.Should().Be(2);
        var reportBefore = await jobs.ListReportAsync(null, 1000, engagedOnly: false, Ct);
        reportBefore.Select(r => r.Phone).Should().Contain(new[] { active.Phone.E164, toDiscard.Phone.E164 });

        // Descarta (soft delete) o grupo do segundo — mesmo caminho da tela (ExecuteUpdate real).
        await contacts.DiscardByGroupTagAsync("Descartar", Now, Ct);

        // Depois: o descartado saiu do report E do contador; o ativo permanece nos dois.
        var after = await jobs.GetStatsAsync(null, Ct);
        after.Sent.Should().Be(1, "o job do contato descartado não conta mais");
        var reportAfter = await jobs.ListReportAsync(null, 1000, engagedOnly: false, Ct);
        reportAfter.Select(r => r.Phone).Should().ContainSingle().Which.Should().Be(active.Phone.E164);
    }
}
