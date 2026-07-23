using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Dispatch;

/// <summary>E2E contra Postgres REAL da ORDEM da fila: um job <c>Retrying</c> JÁ VENCIDO é servido
/// ANTES de um <c>Pending</c> mais antigo. Prova também a tradução EF do <c>OrderBy(ternário)</c> em
/// <c>ORDER BY CASE WHEN …</c> com o value-converter do enum — que só falharia em runtime.
/// <para>
/// Por que essa ordem: no modo emulador o job é adiado (Retrying) DEPOIS de salvar o contato na agenda,
/// esperando o WhatsApp reconhecê-lo. Ordenando só por ScheduledAt, esse job caía atrás de toda a fila
/// Pending — que costuma estar agendada no passado — e o preparo nunca se pagava: com 125 contatos a
/// 90-240s cada, o 1º envio só sairia horas depois. Exige Docker.
/// </para></summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class DequeueOrderE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

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

    private async Task<Guid> SeedJobAsync(string phone, DateTimeOffset scheduledAt, DateTimeOffset? deferTo)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(phone).Value!,
            name: phone, groupTag: null, theme: null, optInAt: Now);
        _db.Contacts.Add(c);
        var job = DispatchJob.Schedule(Guid.NewGuid(), c.Id, Guid.NewGuid(), scheduledAt);
        if (deferTo is { } at)
        {
            job.Defer(at, "aguardando o WhatsApp reconhecer o contato");
        }
        _db.DispatchJobs.Add(job);
        await _db.SaveChangesAsync(Ct);
        return job.Id;
    }

    [Fact]
    public async Task Retrying_vencido_vem_antes_de_Pending_mais_antigo()
    {
        // Pending criado HORAS antes (o caso real: a fila nasce agendada no passado).
        var pendingAntigo = await SeedJobAsync("11955551001", Now.AddHours(-9), deferTo: null);
        // Retrying adiado há pouco, mas JÁ VENCIDO (o contato já teve tempo de sincronizar).
        var retryingVencido = await SeedJobAsync("11955551002", Now.AddHours(-9), deferTo: Now.AddMinutes(-1));

        var next = await new DispatchJobRepository(_db).DequeueNextPendingAsync(Now, Ct);

        next!.Id.Should().Be(retryingVencido,
            "trabalho já começado (contato salvo, sincronizando) termina antes de começar coisa nova");
        next.Id.Should().NotBe(pendingAntigo);
    }

    [Fact]
    public async Task Retrying_ainda_no_futuro_nao_e_servido()
    {
        var pendingAntigo = await SeedJobAsync("11955551011", Now.AddHours(-9), deferTo: null);
        await SeedJobAsync("11955551012", Now.AddHours(-9), deferTo: Now.AddMinutes(8)); // janela em curso

        var next = await new DispatchJobRepository(_db).DequeueNextPendingAsync(Now, Ct);

        next!.Id.Should().Be(pendingAntigo,
            "a preferência é só pra Retrying VENCIDO; enquanto a janela corre, a fila segue com os Pending");
    }

    [Fact]
    public async Task Entre_dois_Pending_o_mais_antigo_vem_primeiro()
    {
        var maisAntigo = await SeedJobAsync("11955551021", Now.AddHours(-9), deferTo: null);
        await SeedJobAsync("11955551022", Now.AddHours(-1), deferTo: null);

        var next = await new DispatchJobRepository(_db).DequeueNextPendingAsync(Now, Ct);

        next!.Id.Should().Be(maisAntigo, "a ordem por ScheduledAt continua valendo dentro do mesmo grupo");
    }
}
