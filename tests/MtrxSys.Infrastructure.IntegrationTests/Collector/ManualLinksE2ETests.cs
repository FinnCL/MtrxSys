using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Groups;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// E2E da entrada manual de links contra Postgres REAL: cola links → deduplica → cria como Found →
/// valida na hora via join-info (vivo→Resolved, morto→Invalid). WAHA mockado por código.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class ManualLinksE2ETests : IAsyncLifetime
{
    private const string CodeVivo = "AAAAAAAAAAAAAAAAAAAA";
    private const string CodeMorto = "BBBBBBBBBBBBBBBBBBBB";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
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

    private CollectGroupLinksUseCase BuildUseCase()
    {
        // Validador (página pública) mockado por código: CodeVivo → vivo; CodeMorto → morto.
        var validator = Substitute.For<IWhatsAppInviteValidator>();
        validator.CheckAsync(CodeVivo, Arg.Any<CancellationToken>())
            .Returns(new InviteCheck(true, "Grupo Marketing BR"));
        validator.CheckAsync(CodeMorto, Arg.Any<CancellationToken>())
            .Returns(new InviteCheck(false, null));

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));

        return new CollectGroupLinksUseCase(
            Substitute.For<IGroupLinkSource>(),
            Substitute.For<IGroupLinkSearchSource>(),
            validator,
            new GroupLinkRepository(_db),
            new UnitOfWork(_db),
            clock,
            Substitute.For<IRandomSource>(),
            Options.Create(new CollectorOptions()));
    }

    [Fact]
    public async Task Cola_links_valida_na_hora_e_persiste_so_os_vivos_como_resolved()
    {
        var lines = new[]
        {
            $"https://chat.whatsapp.com/{CodeVivo}",
            $"chat.whatsapp.com/{CodeMorto}",
            $"https://chat.whatsapp.com/{CodeVivo}", // duplicado dentro do próprio input
        };

        var r = await BuildUseCase().AddManualAsync(lines, "marketing", CancellationToken.None);

        r.Added.Should().Be(2, "o duplicado dentro do input é deduplicado");
        r.Live.Should().Be(1);
        r.Dead.Should().Be(1);
        r.Duplicates.Should().Be(0);

        _db.ChangeTracker.Clear();
        var vivo = await _db.GroupLinks.SingleAsync(g => g.InviteCode == CodeVivo);
        vivo.Status.Should().Be(GroupLinkStatus.Resolved);
        vivo.GroupName.Should().Be("Grupo Marketing BR");
        vivo.SourceChannel.Should().Be("manual");
        vivo.MatchedKeyword.Should().Be("marketing");

        var morto = await _db.GroupLinks.SingleAsync(g => g.InviteCode == CodeMorto);
        morto.Status.Should().Be(GroupLinkStatus.Invalid);
    }

    [Fact]
    public async Task Rodar_de_novo_conta_como_duplicado()
    {
        var lines = new[] { $"https://chat.whatsapp.com/{CodeVivo}" };
        await BuildUseCase().AddManualAsync(lines, null, CancellationToken.None);
        _db.ChangeTracker.Clear();

        var r = await BuildUseCase().AddManualAsync(lines, null, CancellationToken.None);

        r.Added.Should().Be(0);
        r.Duplicates.Should().Be(1);
        (await _db.GroupLinks.CountAsync()).Should().Be(1);
    }
}
