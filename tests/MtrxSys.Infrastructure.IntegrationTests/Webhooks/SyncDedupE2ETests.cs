using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Webhooks;

/// <summary>
/// E2E do SYNC contra Postgres REAL (WAHA mockado): prova as correções da falha de chave duplicada.
///   - uma mensagem DUPLICADA (mesmo wa_message_id na rodada) NÃO derruba mais o lote (dedup por
///     rodada + save por chat);
///   - um chat que FALHA não impede os outros (save por chat + DiscardChanges isola e auto-cura).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class SyncDedupE2ETests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
    private const string ChatA = "5511987651234@c.us";
    private const string ChatB = "5521987654321@c.us";

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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private WhatsAppSyncService Build(IWahaClient waha) =>
        new(waha, new ConversationRepository(_db), new ChatMessageRepository(_db), new ContactRepository(_db),
            new UnitOfWork(_db), new FixedClock(), new BrazilPhoneValidator(),
            Options.Create(new DispatchOptions { SessionId = "default" }), NullLogger<WhatsAppSyncService>.Instance);

    private static WahaChat Chat(string id, string name) => new(id, name, "oi", T0, false);
    private static WahaMessage Msg(string id, string chatId, string body) => new(id, chatId, false, chatId, body, T0);

    [Fact]
    public async Task Mensagem_duplicada_na_rodada_nao_derruba_o_lote()
    {
        var waha = Substitute.For<IWahaClient>();
        waha.ListChatsOverviewAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WahaChat> { Chat(ChatA, "Ana"), Chat(ChatB, "Bruno") });
        waha.GetChatMessagesAsync(Arg.Any<string>(), ChatA, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WahaMessage> { Msg("MSG1", ChatA, "oi"), Msg("MSG1", ChatA, "oi") }); // DUPLICATA
        waha.GetChatMessagesAsync(Arg.Any<string>(), ChatB, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WahaMessage> { Msg("MSG2", ChatB, "ola") });

        var result = await Build(waha).SyncAsync(50, CancellationToken.None);

        result.Failures.Should().BeEmpty("a duplicata é tratada, não vira falha");
        result.MessagesImported.Should().Be(2);
        _db.ChangeTracker.Clear();
        (await _db.ChatMessages.CountAsync(m => m.WaMessageId == "MSG1")).Should().Be(1, "a duplicata não duplica");
        (await _db.ChatMessages.CountAsync(m => m.WaMessageId == "MSG2")).Should().Be(1, "o outro chat entrou normal");
    }

    [Fact]
    public async Task Chat_que_falha_nao_impede_os_outros()
    {
        var waha = Substitute.For<IWahaClient>();
        waha.ListChatsOverviewAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WahaChat> { Chat(ChatA, "Ana"), Chat(ChatB, "Bruno") });
        waha.GetChatMessagesAsync(Arg.Any<string>(), ChatA, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("WAHA caiu nesse chat"));
        waha.GetChatMessagesAsync(Arg.Any<string>(), ChatB, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<WahaMessage> { Msg("MSG2", ChatB, "ola") });

        var result = await Build(waha).SyncAsync(50, CancellationToken.None);

        result.Failures.Should().ContainSingle().Which.Should().Contain(ChatA);
        _db.ChangeTracker.Clear();
        (await _db.ChatMessages.CountAsync(m => m.WaMessageId == "MSG2")).Should().Be(1, "o chat bom entrou apesar do ruim falhar");
        (await _db.Conversations.CountAsync(c => c.WaChatId == ChatA)).Should().Be(0, "o pendente do chat que falhou foi DESCARTADO");
        (await _db.Conversations.CountAsync(c => c.WaChatId == ChatB)).Should().Be(1);
    }
}
