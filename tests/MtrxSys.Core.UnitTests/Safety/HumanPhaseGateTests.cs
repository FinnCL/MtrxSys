using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Safety;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class HumanPhaseGateTests
{
    private static readonly DateOnly Cut = new(2026, 7, 14);

    private readonly ISystemStateRepository _state = Substitute.For<ISystemStateRepository>();
    private readonly IHumanPhaseProgressRepository _progress = Substitute.For<IHumanPhaseProgressRepository>();

    public HumanPhaseGateTests()
    {
        // Sem progresso por padrão: cada teste declara só o que importa pro caso.
        _progress.CountOutboundActiveDaysAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(0);
        _progress.ListConversationTalliesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        SetAnchor(null);
    }

    private HumanPhaseGate Build(HumanPhaseOptions? opts = null) =>
        new(_state, _progress, Options.Create(opts ?? Defaults()));

    // Metas pequenas e explícitas: 2 dias, 2 pessoas, 1 msg de cada lado. Os defaults reais
    // (3/5/3/3) são política de config, não regra — o gate só compara números.
    private static HumanPhaseOptions Defaults() => new()
    {
        EffectiveFrom = Cut,
        MinDays = 2,
        MinPeople = 2,
        MinInbound = 1,
        MinOutbound = 1,
    };

    // Marco do aquecimento no banco. null = ambiente que nunca ancorou.
    private void SetAnchor(DateOnly? startedOn)
    {
        var state = SystemStateAggregate.CreateInitial();
        if (startedOn is { } d)
        {
            state.RestartWarmup(d);
        }
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(state);
    }

    private void SetProgress(int activeDays, params (int Inbound, int Outbound)[] conversations)
    {
        _progress.CountOutboundActiveDaysAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(activeDays);
        _progress.ListConversationTalliesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(conversations
                .Select((c, i) => new ConversationTally(Guid.NewGuid(), $"p{i}", $"555{i}@c.us", c.Inbound, c.Outbound))
                .ToList());
    }

    // ── Proteção da produção. Estes três são os testes que impedem um deploy de parar os 10 stacks.

    [Fact]
    public async Task Does_not_block_when_feature_is_off()
    {
        // EffectiveFrom ausente = recurso desligado. É o DEFAULT, e o que garante que subir este
        // código sem configurar nada não muda absolutamente nada.
        SetAnchor(Cut.AddDays(1));
        var svc = Build(new HumanPhaseOptions { EffectiveFrom = null, MinDays = 99, MinPeople = 99 });

        (await svc.IsBlockedAsync(CancellationToken.None)).Should().BeFalse();
        (await svc.GetSnapshotAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Does_not_block_chip_anchored_before_the_cut()
    {
        // Chip que já estava rodando: marco anterior ao corte → a fase não se aplica, mesmo sem
        // nenhuma conversa. Sem isto, um deploy travaria o disparo de quem está em produção.
        SetAnchor(Cut.AddDays(-1));

        (await Build().IsBlockedAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Does_not_block_when_chip_has_no_anchor()
    {
        // Sem marco não dá pra afirmar que o chip é novo → não trava (lado seguro pra produção).
        SetAnchor(null);

        (await Build().IsBlockedAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Applies_to_chip_anchored_exactly_on_the_cut()
    {
        // Fronteira: o corte é INCLUSIVO (>=). Um chip pareado no próprio dia do deploy é novo.
        SetAnchor(Cut);

        (await Build().IsBlockedAsync(CancellationToken.None)).Should().BeTrue();
    }

    // ── A regra: evidência E dias.

    [Fact]
    public async Task Blocks_when_days_are_enough_but_people_are_not()
    {
        SetAnchor(Cut);
        SetProgress(activeDays: 5, (Inbound: 3, Outbound: 3));

        (await Build().IsBlockedAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_when_people_are_enough_but_days_are_not()
    {
        // Falar com 5 pessoas num dia só não é aquecimento — por isso os dias também contam.
        SetAnchor(Cut);
        SetProgress(activeDays: 1, (Inbound: 3, Outbound: 3), (Inbound: 3, Outbound: 3));

        (await Build().IsBlockedAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Opens_when_both_goals_are_met()
    {
        SetAnchor(Cut);
        SetProgress(activeDays: 2, (Inbound: 1, Outbound: 1), (Inbound: 1, Outbound: 1));

        (await Build().IsBlockedAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Conversation_without_inbound_does_not_qualify()
    {
        // É O PONTO DA FASE: conversa só de ida é exatamente o padrão de robô que queremos evitar.
        // Mandar 50 mensagens pra 2 pessoas que nunca responderam não abre o disparo.
        SetAnchor(Cut);
        SetProgress(activeDays: 9, (Inbound: 0, Outbound: 50), (Inbound: 0, Outbound: 50));

        var snap = await Build().GetSnapshotAsync(CancellationToken.None);

        snap!.QualifiedPeople.Should().Be(0);
        snap.Satisfied.Should().BeFalse();
    }

    [Fact]
    public async Task Conversation_without_outbound_does_not_qualify()
    {
        // Só receber também não vale: quem tem que aquecer o chip é o chip.
        SetAnchor(Cut);
        SetProgress(activeDays: 9, (Inbound: 50, Outbound: 0), (Inbound: 50, Outbound: 0));

        (await Build().GetSnapshotAsync(CancellationToken.None))!.QualifiedPeople.Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_anchors_the_progress_query_on_the_warmup_start()
    {
        // O progresso é contado DESDE o marco — não do histórico todo. É o que faz um chip novo no
        // mesmo stack recomeçar do zero em vez de herdar as conversas do chip anterior.
        var anchor = Cut.AddDays(3);
        SetAnchor(anchor);

        await Build().GetSnapshotAsync(CancellationToken.None);

        await _progress.Received(1).CountOutboundActiveDaysAsync(anchor, Arg.Any<CancellationToken>());
        await _progress.Received(1).ListConversationTalliesAsync(anchor, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Snapshot_reports_what_is_missing()
    {
        SetAnchor(Cut);
        SetProgress(activeDays: 1, (Inbound: 1, Outbound: 1));

        var snap = await Build().GetSnapshotAsync(CancellationToken.None);

        snap!.DaysRemaining.Should().Be(1);
        snap.PeopleRemaining.Should().Be(1);
        snap.StartedOn.Should().Be(Cut);
    }

    [Fact]
    public async Task GetAnchorIfApplies_does_not_touch_the_progress_repository()
    {
        // O caminho barato do latch (ver HumanPhaseTracker): decidir "a fase se aplica?" não pode
        // custar o group-by sobre chat_messages — é o que todo chip de produção paga a cada job.
        SetAnchor(Cut.AddDays(-1));

        var anchor = await Build().GetAnchorIfAppliesAsync(CancellationToken.None);

        anchor.Should().BeNull();
        await _progress.DidNotReceive().CountOutboundActiveDaysAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _progress.DidNotReceive().ListConversationTalliesAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
