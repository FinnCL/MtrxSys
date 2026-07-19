using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Dispatch;

/// <summary>E2E contra Postgres REAL: <c>DeleteNonEngagedPendingAsync</c> (limpeza da fila na fase de
/// aquecimento) remove da FILA (Pending/Retrying) só quem NÃO engajou, mantendo respondedores na fila
/// E todo o histórico (Enviada). Prova a tradução EF do EXISTS correlacionado + <c>stage IN (...)</c>
/// dentro de um ExecuteDelete — o padrão novo que, sem prova, poderia virar erro de runtime. Exige Docker.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class WarmingQueueCleanupE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

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

    private async Task<Guid> SeedJobAsync(string phone, ContactStage stage, bool sent)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(phone).Value!,
            name: phone, groupTag: null, theme: null, optInAt: Now);
        if (stage != ContactStage.Lead)
        {
            c.ChangeStage(stage, Now);
        }
        _db.Contacts.Add(c);
        var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, Guid.NewGuid(), Now);
        if (sent)
        {
            job.MarkSent($"wa-{phone}", Now);
        }
        _db.DispatchJobs.Add(job);
        await _db.SaveChangesAsync(Ct);
        return job.Id;
    }

    [Fact]
    public async Task Limpa_da_fila_so_os_nao_respondedores_mantendo_respondedor_e_historico()
    {
        var respondedorNaFila = await SeedJobAsync("11955550001", ContactStage.Qualified, sent: false);
        var novoNaFila = await SeedJobAsync("11955550002", ContactStage.Lead, sent: false);       // apaga
        var descartadoNaFila = await SeedJobAsync("11955550003", ContactStage.Lost, sent: false); // apaga
        var novoEnviado = await SeedJobAsync("11955550004", ContactStage.Lead, sent: true);       // histórico: fica

        var removed = await new DispatchJobRepository(_db).DeleteNonEngagedPendingAsync(Ct);

        removed.Should().Be(2, "só os dois não-respondedores NA FILA saem");
        _db.ChangeTracker.Clear();
        var remaining = await _db.DispatchJobs.Select(j => j.Id).ToListAsync(Ct);
        remaining.Should().BeEquivalentTo(new[] { respondedorNaFila, novoEnviado });
        remaining.Should().NotContain(novoNaFila, "Novo na fila é removido no aquecimento");
        remaining.Should().NotContain(descartadoNaFila, "Descartado na fila é removido no aquecimento");
    }

    [Fact]
    public async Task Reset_apaga_o_historico_dos_engajados_mas_mantem_a_fila_e_os_nao_engajados()
    {
        var engajadoHistorico = await SeedJobAsync("11955550011", ContactStage.Qualified, sent: true);  // apaga
        var engajadoNaFila = await SeedJobAsync("11955550012", ContactStage.Won, sent: false);           // fila: fica
        var novoHistorico = await SeedJobAsync("11955550013", ContactStage.Lead, sent: true);            // não-engajado: fica

        var removed = await new DispatchJobRepository(_db).DeleteEngagedHistoryAsync(Ct);

        removed.Should().Be(1, "só o histórico (Enviada) do engajado sai");
        _db.ChangeTracker.Clear();
        var remaining = await _db.DispatchJobs.Select(j => j.Id).ToListAsync(Ct);
        remaining.Should().BeEquivalentTo(new[] { engajadoNaFila, novoHistorico });
        remaining.Should().NotContain(engajadoHistorico, "o Enviada de ontem do respondedor é limpo");
    }

    [Fact]
    public async Task Report_na_fase_mostra_so_respondedores_mas_fora_da_fase_mostra_todos()
    {
        await SeedJobAsync("11955550021", ContactStage.Qualified, sent: true);   // respondedor
        await SeedJobAsync("11955550022", ContactStage.Lead, sent: true);        // não respondeu (Enviada legado)
        var pulado = await SeedJobAsync("11955550023", ContactStage.Lead, sent: false);
        // Simula um job PULADO pelo motor na fase (não-respondedor que estava na fila).
        var puladoJob = await _db.DispatchJobs.FirstAsync(j => j.Id == pulado, Ct);
        puladoJob.MarkSkipped("aquecimento");
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();

        var repo = new DispatchJobRepository(_db);

        var naFase = await repo.ListReportAsync(null, 1000, engagedOnly: true, Ct);
        naFase.Select(r => r.Phone).Should().ContainSingle()
            .Which.Should().Be("+5511955550021", "na fase a tabela mostra só quem respondeu");

        var foraDaFase = await repo.ListReportAsync(null, 1000, engagedOnly: false, Ct);
        foraDaFase.Should().HaveCount(3, "fora da fase mostra todos, como antes");
    }
}
