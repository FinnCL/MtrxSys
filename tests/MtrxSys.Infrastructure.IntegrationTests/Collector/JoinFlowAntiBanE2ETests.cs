using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Safety;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// E2E do caminho ANTI-BAN da entrada contra Postgres REAL, replicando o miolo do endpoint
/// POST /links/{code}/join. Agora o contador é PERSISTENTE (joined_at no banco), então prova:
///   - entrada conta e a próxima é bloqueada dentro do intervalo;
///   - convite inválido (4xx) NÃO conta (sem joined_at);
///   - PERSISTÊNCIA: um novo DbContext (simula restart) ainda enxerga a entrada — o teto não zera.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class JoinFlowAntiBanE2ETests : IAsyncLifetime
{
    private const string Session = "default";
    private static readonly DateTimeOffset T0 = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _pg =
        new Testcontainers.PostgreSql.PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = new MtrxDbContext(new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private sealed class JoinStub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static JoinThrottle Throttle(int maxPerDay = 15) =>
        new(Options.Create(new CollectorOptions { MaxJoinsPerDay = maxPerDay }));

    private static WahaClient Waha(HttpStatusCode status, string body) =>
        new(new HttpClient(new JoinStub(status, body)) { BaseAddress = new Uri("http://waha.test/") }, Options.Create(new WahaOptions()));

    private async Task SeedResolvedAsync(string code)
    {
        var repo = new GroupLinkRepository(_db);
        var link = GroupLink.Create(Guid.NewGuid(), code, $"https://chat.whatsapp.com/{code}", "serper", "bet", T0);
        link.Resolve("Grupo BR", null, T0);
        await repo.AddAsync(link, CancellationToken.None);
        await new UnitOfWork(_db).SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
    }

    // Espelha o endpoint: contagem/última entrada vêm do BANCO; sucesso carimba joined_at.
    private async Task<string> TryJoinAsync(JoinThrottle throttle, WahaClient waha, string code, DateTimeOffset now)
    {
        var repo = new GroupLinkRepository(_db);
        var uow = new UnitOfWork(_db);
        var link = await repo.GetByCodeAsync(code, CancellationToken.None);
        var joinsToday = await repo.CountJoinedSinceAsync(JoinThrottle.StartOfTodayBrazil(now), CancellationToken.None);
        var last = await repo.MaxJoinedAtAsync(CancellationToken.None);
        if (!throttle.Check(joinsToday, last, now).Allowed)
        {
            return "blocked";
        }
        WahaGroup joined;
        try
        {
            joined = await waha.JoinGroupByInviteAsync(Session, link!.InviteCode, CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            link!.MarkInvalid(now);
            await repo.UpdateAsync(link, CancellationToken.None);
            await uow.SaveChangesAsync(CancellationToken.None);
            return "invalid";
        }
        link!.MarkJoined(joined.Id, now);
        await repo.UpdateAsync(link, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
        return "joined";
    }

    [Fact]
    public async Task Entrada_conta_e_a_proxima_no_intervalo_e_bloqueada()
    {
        var throttle = Throttle();
        var waha = Waha(HttpStatusCode.OK, """{ "id": "120363111@g.us", "name": "Grupo BR" }""");
        await SeedResolvedAsync("AAAAAAAAAAAA");
        await SeedResolvedAsync("BBBBBBBBBBBB");

        (await TryJoinAsync(throttle, waha, "AAAAAAAAAAAA", T0)).Should().Be("joined");
        (await TryJoinAsync(throttle, waha, "BBBBBBBBBBBB", T0.AddSeconds(10))).Should().Be("blocked");
        // Passado o teto máximo do intervalo (300s), libera.
        (await TryJoinAsync(throttle, waha, "BBBBBBBBBBBB", T0.AddSeconds(301))).Should().Be("joined");

        var repo = new GroupLinkRepository(_db);
        (await repo.CountJoinedSinceAsync(JoinThrottle.StartOfTodayBrazil(T0), CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task Convite_invalido_4xx_nao_conta()
    {
        var throttle = Throttle();
        var waha = Waha(HttpStatusCode.BadRequest, "{}");
        await SeedResolvedAsync("CCCCCCCCCCCC");

        (await TryJoinAsync(throttle, waha, "CCCCCCCCCCCC", T0)).Should().Be("invalid");

        var repo = new GroupLinkRepository(_db);
        (await repo.CountJoinedSinceAsync(JoinThrottle.StartOfTodayBrazil(T0), CancellationToken.None))
            .Should().Be(0, "uma entrada que falhou não é carimbada (joined_at fica null)");
    }

    [Fact]
    public async Task Contador_persiste_apos_restart_simulado()
    {
        var throttle = Throttle();
        var waha = Waha(HttpStatusCode.OK, """{ "id": "120363222@g.us", "name": "Grupo BR" }""");
        await SeedResolvedAsync("DDDDDDDDDDDD");
        (await TryJoinAsync(throttle, waha, "DDDDDDDDDDDD", T0)).Should().Be("joined");

        // "Restart": novo DbContext no MESMO banco, contador novo (sem estado em memória).
        await using var db2 = new MtrxDbContext(
            new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options);
        var repo2 = new GroupLinkRepository(db2);
        var joinsToday = await repo2.CountJoinedSinceAsync(JoinThrottle.StartOfTodayBrazil(T0), CancellationToken.None);
        var last = await repo2.MaxJoinedAtAsync(CancellationToken.None);

        joinsToday.Should().Be(1, "a entrada persistiu — o teto NÃO zera num restart");
        Throttle().GetStatus(joinsToday, last, T0.AddSeconds(301)).JoinsToday.Should().Be(1);
    }
}
