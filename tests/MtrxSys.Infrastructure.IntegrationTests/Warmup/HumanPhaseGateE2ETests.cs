using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;

namespace MtrxSys.Infrastructure.IntegrationTests.Warmup;

/// <summary>
/// E2E da Fase Humana INTEIRA contra Postgres REAL: o HumanPhaseGate com os repositórios de
/// verdade, exercitando o mesmo IsBlockedAsync que o DispatchEngine chama a cada job.
///
/// O teste unitário do gate mocka o repositório e prova a REGRA; este prova que a regra sobrevive
/// ao banco — fuso, join, e o corte pela âncora.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class HumanPhaseGateE2ETests : IAsyncLifetime
{
    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _pg =
        new Testcontainers.PostgreSql.PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;

    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateOnly Cut = new(2026, 7, 14);

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

    private HumanPhaseGate Gate(DateOnly? effectiveFrom) =>
        new(new SystemStateRepository(_db),
            new HumanPhaseProgressRepository(_db),
            Options.Create(new HumanPhaseOptions
            {
                EffectiveFrom = effectiveFrom,
                MinDays = 2,
                MinPeople = 2,
                MinInbound = 1,
                MinOutbound = 1,
            }));

    private async Task AnchorChipAsync(DateOnly startedOn)
    {
        var repo = new SystemStateRepository(_db);
        var state = await repo.GetAsync(Ct);
        state.RestartWarmup(startedOn);
        await repo.UpdateAsync(state, Ct);
        await _db.SaveChangesAsync(Ct);
    }

    // Uma conversa de ida-e-volta num dado dia-Brasília.
    private async Task TalkAsync(string phone, DateOnly day, int outbound, int inbound)
    {
        var chatId = $"{phone}@c.us";
        var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.WaChatId == chatId, Ct);
        if (conv is null)
        {
            conv = Conversation.Create(Guid.NewGuid(), chatId, null, phone, false, DateTimeOffset.UtcNow);
            _db.Conversations.Add(conv);
            await _db.SaveChangesAsync(Ct);
        }
        // 15h UTC = meio-dia BRT: mesmo dia nos dois fusos, sem ambiguidade de fronteira.
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
        for (var i = 0; i < outbound; i++)
        {
            _db.ChatMessages.Add(ChatMessage.Create(
                Guid.NewGuid(), conv.Id, $"wa-{Guid.NewGuid()}", MessageDirection.Outbound, null, "oi", at));
        }
        for (var i = 0; i < inbound; i++)
        {
            _db.ChatMessages.Add(ChatMessage.Create(
                Guid.NewGuid(), conv.Id, $"wa-{Guid.NewGuid()}", MessageDirection.Inbound, null, "opa", at));
        }
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Chip_de_producao_nao_e_travado_nem_com_a_fase_ligada()
    {
        // A PROVA QUE PROTEGE OS 10 STACKS: chip ancorado ANTES do corte não entra na fase, mesmo
        // sem conversa nenhuma no banco. Se este teste cair, um deploy para a produção inteira.
        await AnchorChipAsync(Cut.AddDays(-30));

        (await Gate(Cut).IsBlockedAsync(Ct)).Should().BeFalse();
        (await Gate(Cut).GetSnapshotAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Recurso_desligado_nao_trava_chip_nenhum()
    {
        // EffectiveFrom null = o default do appsettings. Subir o código sem configurar não muda nada.
        await AnchorChipAsync(Cut.AddDays(1));

        (await Gate(null).IsBlockedAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Chip_novo_nasce_travado_e_abre_sozinho_ao_bater_as_duas_metas()
    {
        // O ciclo de vida inteiro da fase, contra banco real.
        var anchor = Cut.AddDays(1);
        await AnchorChipAsync(anchor);
        var gate = Gate(Cut);

        // Dia 0: chip pareado, nada conversado → travado.
        (await gate.IsBlockedAsync(Ct)).Should().BeTrue();

        // Dia 1: duas conversas de ida-e-volta, mas num dia só → ainda travado (falta dia).
        await TalkAsync("5571900000001", anchor, outbound: 2, inbound: 2);
        await TalkAsync("5571900000002", anchor, outbound: 2, inbound: 2);
        var mid = await gate.GetSnapshotAsync(Ct);
        mid!.QualifiedPeople.Should().Be(2);
        mid.ActiveDays.Should().Be(1);
        mid.DaysRemaining.Should().Be(1);
        (await gate.IsBlockedAsync(Ct)).Should().BeTrue();

        // Dia 2: mais um dia com atividade → as duas metas batem e o disparo abre SOZINHO.
        await TalkAsync("5571900000001", anchor.AddDays(1), outbound: 1, inbound: 1);
        var done = await gate.GetSnapshotAsync(Ct);
        done!.ActiveDays.Should().Be(2);
        done.Satisfied.Should().BeTrue();
        (await gate.IsBlockedAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Conversa_so_de_ida_nao_abre_o_disparo()
    {
        // O ponto da fase: 100 mensagens pra 2 pessoas que nunca responderam é exatamente o padrão
        // de robô que estamos evitando. Não vale como aquecimento.
        var anchor = Cut.AddDays(1);
        await AnchorChipAsync(anchor);
        await TalkAsync("5571900000001", anchor, outbound: 50, inbound: 0);
        await TalkAsync("5571900000002", anchor.AddDays(1), outbound: 50, inbound: 0);

        var snap = await Gate(Cut).GetSnapshotAsync(Ct);

        snap!.ActiveDays.Should().Be(2);          // dias, tem
        snap.QualifiedPeople.Should().Be(0);      // gente que respondeu, não
        (await Gate(Cut).IsBlockedAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Troca_de_chip_refaz_a_fase_ignorando_as_conversas_do_chip_anterior()
    {
        // Chip antigo cumpriu a fase; as conversas dele FICAM no banco (mesmo stack). Um chip novo
        // não pode herdar isso — senão nasceria "aquecido" sem nunca ter conversado.
        var oldAnchor = Cut.AddDays(1);
        await AnchorChipAsync(oldAnchor);
        await TalkAsync("5571900000001", oldAnchor, outbound: 2, inbound: 2);
        await TalkAsync("5571900000002", oldAnchor, outbound: 2, inbound: 2);
        await TalkAsync("5571900000001", oldAnchor.AddDays(1), outbound: 1, inbound: 1);
        (await Gate(Cut).IsBlockedAsync(Ct)).Should().BeFalse(); // chip antigo: liberado

        // Chip novo: RestartWarmup re-ancora → o progresso reseta sozinho, sem código extra.
        await AnchorChipAsync(oldAnchor.AddDays(10));

        (await Gate(Cut).IsBlockedAsync(Ct)).Should().BeTrue();
        (await Gate(Cut).GetSnapshotAsync(Ct))!.QualifiedPeople.Should().Be(0);
    }
}
