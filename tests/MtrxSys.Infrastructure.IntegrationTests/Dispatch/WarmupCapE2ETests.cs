using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;

namespace MtrxSys.Infrastructure.IntegrationTests.Dispatch;

/// <summary>
/// E2E do teto diário do warm-up contra Postgres REAL, exercitando o mesmo par gate→incremento
/// que o DispatchEngine usa (CanSendAsync / IncrementAsync / GetSnapshotAsync) com WarmupManager,
/// DailySendCountsRepository e SystemStateRepository de verdade. Prova ponta a ponta:
///   - o "dia" do contador é o de BRASÍLIA: não zera à meia-noite UTC (21h BRT), só à 00h BRT;
///   - o contador PERSISTE (um restart não fura o teto);
///   - a curva avança por dias ATIVOS persistidos (1 envio/dia conta como dia usado).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class WarmupCapE2ETests : IAsyncLifetime
{
    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _pg =
        new Testcontainers.PostgreSql.PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = NewContext();
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private MtrxDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private static WarmupManager Warmup(MtrxDbContext db, IClock clock, params int[] curve) =>
        new(new DailySendCountsRepository(db),
            new SystemStateRepository(db),
            clock,
            Options.Create(new WarmupOptions { Curve = curve }));

    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Teto_usa_o_dia_de_Brasilia_e_nao_zera_a_meia_noite_UTC()
    {
        var clock = new FakeClock();
        var warmup = Warmup(_db, clock, 3); // teto fixo de 3/dia

        // 20:30 BRT de 17/06 (23:30 UTC). Estoura o teto do dia brasileiro.
        clock.UtcNow = new DateTimeOffset(2026, 6, 17, 23, 30, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            (await warmup.CanSendAsync(Ct)).Should().BeTrue($"o {i + 1}º envio ainda cabe no teto de 3");
            await warmup.IncrementAsync(Ct);
        }
        (await warmup.CanSendAsync(Ct)).Should().BeFalse("bateu 3/3 no dia brasileiro 17/06");

        // 22:00 BRT do MESMO 17/06 — mas já 01:00 UTC do dia 18. A meia-noite UTC passou.
        // Sob a lógica antiga (UTC) o contador zeraria aqui e deixaria disparar de novo: O FURO.
        clock.UtcNow = new DateTimeOffset(2026, 6, 18, 1, 0, 0, TimeSpan.Zero);
        (await warmup.CanSendAsync(Ct)).Should().BeFalse(
            "ainda é 17/06 em Brasília — o teto NÃO pode zerar à meia-noite UTC");
        (await warmup.GetSnapshotAsync(Ct)).SentToday.Should().Be(3, "mesmo dia brasileiro, contador preservado");

        // 00:30 BRT de 18/06 (03:30 UTC) — virou o dia em Brasília. Agora sim zera.
        clock.UtcNow = new DateTimeOffset(2026, 6, 18, 3, 30, 0, TimeSpan.Zero);
        (await warmup.CanSendAsync(Ct)).Should().BeTrue("novo dia brasileiro: o teto reabre");
        (await warmup.GetSnapshotAsync(Ct)).SentToday.Should().Be(0, "contador do novo dia começa do zero");
    }

    [Fact]
    public async Task Contador_persiste_e_a_curva_avanca_por_dias_ativos()
    {
        var clock = new FakeClock();
        var warmup = Warmup(_db, clock, 2, 4, 7); // curva sobe 2 → 4 → 7

        // Dois dias ATIVOS anteriores (1 envio em cada), em horário sem ambiguidade de fuso.
        clock.UtcNow = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        await warmup.IncrementAsync(Ct);
        clock.UtcNow = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        await warmup.IncrementAsync(Ct);

        // Hoje (17/06): 2 dias ativos antes → DayIndex 2 → teto curva[2] = 7.
        clock.UtcNow = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        var snap = await warmup.GetSnapshotAsync(Ct);
        snap.DayIndex.Should().Be(2, "dois dias usados antes de hoje");
        snap.TodayLimit.Should().Be(7, "curva[2]");
        snap.SentToday.Should().Be(0, "nada enviado hoje ainda");

        // Dispara até bater o teto, como o DispatchEngine faria no loop.
        var sent = 0;
        while (await warmup.CanSendAsync(Ct) && sent < 50)
        {
            await warmup.IncrementAsync(Ct);
            sent++;
        }
        sent.Should().Be(7, "o gate parou exatamente no teto do dia");

        // "Restart": contexto NOVO no mesmo banco (sem estado em memória). O teto não pode furar.
        await using var db2 = NewContext();
        var warmup2 = Warmup(db2, clock, 2, 4, 7);
        var snap2 = await warmup2.GetSnapshotAsync(Ct);
        snap2.SentToday.Should().Be(7, "o contador do dia persistiu — restart não zera o teto");
        (await warmup2.CanSendAsync(Ct)).Should().BeFalse("ainda no teto após o restart");
    }
}
