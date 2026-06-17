using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Groups;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Infrastructure.Collector;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// E2E da busca por nicho contra Postgres REAL (Testcontainers): CollectGroupLinksUseCase com a
/// fonte SearXNG real. O handler falso roteia: o /search (JSON) lista uma PÁGINA; a fonte VISITA o
/// corpo dessa página e extrai os convites → dedup contra o banco → persiste como GroupLink (Found).
/// O WAHA é mockado como "parado" (pula o enriquecimento). O único hop NÃO coberto é a rede real.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class SearxngSearchCollectE2ETests : IAsyncLifetime
{
    private const string CodeA = "AAAAAAAAAAAAAAAAAAAA";
    private const string CodeB = "BBBBBBBBBBBBBBBBBBBB";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private MtrxDbContext _db = null!;

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

    // Roteia por URL: /search → JSON listando a página; a página → HTML com os convites no corpo.
    private sealed class RouteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var s = request.RequestUri!.ToString();
            string body;
            if (s.Contains("searxng.test/search"))
            {
                body = """
                { "results": [ { "url": "http://pages.test/lista", "title": "Grupos", "content": "lista" } ] }
                """;
            }
            else if (s.Contains("pages.test/lista"))
            {
                body = $"""<a href="https://chat.whatsapp.com/{CodeA}">a</a> chat.whatsapp.com/{CodeB}""";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            });
        }
    }

    private CollectGroupLinksUseCase BuildUseCase()
    {
        var collectorOpts = new CollectorOptions { SearxngBaseUrl = "http://searxng.test", MaxResultsPerSearch = 30 };
        var http = new HttpClient(new RouteHandler());
        var search = new SearxngSearchSource(
            http, Options.Create(collectorOpts), NullLogger<SearxngSearchSource>.Instance);

        // Validador (página pública) mockado: todos os achados são VIVOS, com nome BR.
        var validator = Substitute.For<IWhatsAppInviteValidator>();
        validator.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InviteCheck(true, "Grupo Bet BR"));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));

        return new CollectGroupLinksUseCase(
            Substitute.For<IGroupLinkSource>(), // Telegram não deve ser usado quando há nicho + SearXNG
            search,
            validator,
            new GroupLinkRepository(_db),
            new UnitOfWork(_db),
            clock,
            Substitute.For<IRandomSource>(),
            Options.Create(collectorOpts));
    }

    [Fact]
    public async Task Busca_por_nicho_persiste_links_e_deduplica_na_segunda_rodada()
    {
        var first = await BuildUseCase().ExecuteAsync("bet", CancellationToken.None);

        first.NewLinks.Should().Be(2);

        _db.ChangeTracker.Clear();
        var rows = await _db.GroupLinks.OrderBy(g => g.InviteCode).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.InviteCode).Should().BeEquivalentTo([CodeA, CodeB]);
        rows.Should().OnlyContain(r => r.Status == GroupLinkStatus.Resolved, "validados como vivos");
        rows.Should().OnlyContain(r => r.GroupName == "Grupo Bet BR");
        rows.Should().OnlyContain(r => r.MatchedKeyword == "bet");
        rows.Should().OnlyContain(r => r.SourceChannel == "searxng");

        _db.ChangeTracker.Clear();
        var second = await BuildUseCase().ExecuteAsync("bet", CancellationToken.None);

        second.NewLinks.Should().Be(0, "os mesmos códigos já existem no banco");
        (await _db.GroupLinks.CountAsync()).Should().Be(2);
    }
}
