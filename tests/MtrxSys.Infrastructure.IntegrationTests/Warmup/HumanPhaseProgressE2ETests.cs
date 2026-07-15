using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;

namespace MtrxSys.Infrastructure.IntegrationTests.Warmup;

/// <summary>
/// E2E das leituras da Fase Humana contra Postgres REAL — é o único lugar onde o SQL de fuso e o
/// join com conversations rodam de verdade (o teste unitário do HumanPhaseGate mocka o repositório).
/// Prova:
///   - o "dia ativo" é o de BRASÍLIA: 21h UTC já é o dia seguinte, e é aí que o LINQ/EF não ajudaria;
///   - grupo NÃO conta (a fase é conversa de pessoa pra pessoa);
///   - a âncora corta o histórico do chip anterior do mesmo stack.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class HumanPhaseProgressE2ETests : IAsyncLifetime
{
    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _pg =
        new Testcontainers.PostgreSql.PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;

    private static readonly CancellationToken Ct = CancellationToken.None;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = new MtrxDbContext(
            new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private async Task<Guid> NewConversationAsync(string waChatId, bool isGroup, string? title = null)
    {
        var id = Guid.NewGuid();
        _db.Conversations.Add(Conversation.Create(id, waChatId, null, title, isGroup, DateTimeOffset.UtcNow));
        await _db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task AddMessageAsync(Guid conversationId, MessageDirection direction, DateTimeOffset at)
    {
        _db.ChatMessages.Add(ChatMessage.Create(
            Guid.NewGuid(), conversationId, $"wa-{Guid.NewGuid()}", direction, null, "oi", at));
        await _db.SaveChangesAsync(Ct);
    }

    private HumanPhaseProgressRepository Repo() => new(_db);

    [Fact]
    public async Task Dia_ativo_usa_o_dia_de_Brasilia_e_nao_o_UTC()
    {
        // 2026-07-20 23:00 UTC = 20h BRT do MESMO dia 20.
        // 2026-07-21 01:00 UTC = 22h BRT do dia 20 AINDA — não é dia novo em Brasília.
        // Se a contagem usasse o dia UTC, isto daria 2 dias. Em Brasília é 1 só.
        var conv = await NewConversationAsync("5571900000001@c.us", isGroup: false);
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 23, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero));

        var days = await Repo().CountOutboundActiveDaysAsync(new DateOnly(2026, 7, 20), Ct);

        days.Should().Be(1);
    }

    [Fact]
    public async Task Dia_ativo_conta_dias_distintos_e_ignora_o_que_so_entrou()
    {
        // 3 dias-Brasília distintos de SAÍDA. O inbound do 4º dia não cria dia ativo: quem tem que
        // aquecer o chip é o chip — receber mensagem não é atividade dele.
        var conv = await NewConversationAsync("5571900000002@c.us", isGroup: false);
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 18, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 22, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Inbound, new DateTimeOffset(2026, 7, 23, 15, 0, 0, TimeSpan.Zero));

        (await Repo().CountOutboundActiveDaysAsync(new DateOnly(2026, 7, 20), Ct)).Should().Be(3);
    }

    [Fact]
    public async Task Ancora_corta_o_historico_do_chip_anterior()
    {
        // Mesmo stack, chip antigo: as conversas dele ficam no banco. Sem o corte pela âncora, um
        // chip novo nasceria com a fase humana JÁ cumprida pelo chip que saiu.
        var conv = await NewConversationAsync("5571900000003@c.us", isGroup: false);
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 1, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Inbound, new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero));

        (await Repo().CountOutboundActiveDaysAsync(new DateOnly(2026, 7, 20), Ct)).Should().Be(0);
        (await Repo().ListConversationTalliesAsync(new DateOnly(2026, 7, 20), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Placar_conta_os_dois_lados_por_conversa()
    {
        var conv = await NewConversationAsync("5571900000004@c.us", isGroup: false, title: "João");
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 16, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(conv, MessageDirection.Inbound, new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero));

        var tallies = await Repo().ListConversationTalliesAsync(new DateOnly(2026, 7, 20), Ct);

        tallies.Should().ContainSingle();
        tallies[0].ConversationId.Should().Be(conv);
        tallies[0].Title.Should().Be("João");
        // WaChatId é o que permite casar a conversa com a pessoa do círculo na UI.
        tallies[0].WaChatId.Should().Be("5571900000004@c.us");
        tallies[0].Outbound.Should().Be(2);
        tallies[0].Inbound.Should().Be(1);
    }

    [Fact]
    public async Task Grupo_nao_entra_no_placar()
    {
        // Conversa de grupo não aquece: a fase é sobre falar com PESSOAS. Sem este filtro, entrar
        // num grupo movimentado cumpriria a fase sozinho.
        var group = await NewConversationAsync("120363000000000000@g.us", isGroup: true, title: "Grupo");
        await AddMessageAsync(group, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(group, MessageDirection.Inbound, new DateTimeOffset(2026, 7, 20, 16, 0, 0, TimeSpan.Zero));

        (await Repo().ListConversationTalliesAsync(new DateOnly(2026, 7, 20), Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Placar_separa_as_conversas()
    {
        var a = await NewConversationAsync("5571900000005@c.us", isGroup: false, title: "A");
        var b = await NewConversationAsync("5571900000006@c.us", isGroup: false, title: "B");
        await AddMessageAsync(a, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 15, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(a, MessageDirection.Inbound, new DateTimeOffset(2026, 7, 20, 16, 0, 0, TimeSpan.Zero));
        await AddMessageAsync(b, MessageDirection.Outbound, new DateTimeOffset(2026, 7, 20, 17, 0, 0, TimeSpan.Zero));

        var tallies = await Repo().ListConversationTalliesAsync(new DateOnly(2026, 7, 20), Ct);

        tallies.Should().HaveCount(2);
        tallies.Single(t => t.ConversationId == a).Inbound.Should().Be(1);
        tallies.Single(t => t.ConversationId == b).Inbound.Should().Be(0);
    }
}
