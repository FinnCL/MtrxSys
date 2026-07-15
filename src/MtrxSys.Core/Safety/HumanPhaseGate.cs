using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Safety;

/// <summary>Fase Humana Estrita: trava o DISPARO nos primeiros dias de um chip novo, enquanto o
/// operador conversa à mão pela aba Chat. Vizinho do <see cref="WarmupManager"/> — mesma família
/// (anti-ban por volume), papéis diferentes: o WarmupManager diz QUANTO pode sair por dia; este diz
/// SE já pode sair alguma coisa.
///
/// NÃO É UM DEGRAU DA CURVA, e isso é deliberado. Codificar a fase como zeros em Warmup:Curve
/// TRAVARIA O CHIP PRA SEMPRE: o DayIndex vem de CountActiveDaysBeforeAsync, que conta dias com
/// sent_count > 0; com teto 0 o contador nunca sobe, o índice fica em 0 e curve[0]=0 → deadlock.
/// Como gate separado a curva não muda, o índice fica em 0 durante a fase e o 1º dia de automação
/// entra com curve[0] — que é o correto.
///
/// NÃO PERSISTE NADA. O estado "fase fechada" seria uma escrita em system_state vinda também do
/// Dispatcher — exatamente a armadilha documentada no DispatchEngine (a linha singleton tem token
/// de concorrência xmin; um conflito com a Api/webhook revertia junto o MarkSent e a MESMA mensagem
/// era reenviada). Aqui a fase é FUNÇÃO PURA dos dados e MONOTÔNICA (dias ativos e conversas
/// qualificadas só crescem; mensagem não é apagada), então nunca reabre e não precisa de marco
/// gravado. O custo de recomputar é resolvido por latch em memória em quem chama (ver
/// HumanPhaseTracker no Dispatcher).</summary>
public sealed class HumanPhaseGate(
    ISystemStateRepository systemState,
    IHumanPhaseProgressRepository progress,
    IOptions<HumanPhaseOptions> opts)
{
    /// <summary>O disparo deve ficar parado agora? False também quando a fase não se aplica —
    /// que é o caso de TODO chip anterior ao corte (a produção de hoje).</summary>
    public async Task<bool> IsBlockedAsync(CancellationToken ct)
    {
        var snap = await GetSnapshotAsync(ct);
        return snap is { Satisfied: false };
    }

    /// <summary>Âncora se a fase vale pra este chip, sem tocar em chat_messages — só a linha de
    /// estado (que o chamador normalmente já tem em cache no ciclo). Serve pra quem precisa decidir
    /// "vale a pena computar o progresso?" antes de pagar o group-by (ver HumanPhaseTracker).</summary>
    public async Task<DateOnly?> GetAnchorIfAppliesAsync(CancellationToken ct) =>
        AnchorIfApplies(await systemState.GetAsync(ct));

    /// <summary>Foto da fase, ou NULL quando ela não se aplica a este chip (recurso desligado, chip
    /// sem marco, ou chip anterior ao corte). Null é o sinal de "não existe fase aqui" — a UI usa
    /// pra não renderizar o card.</summary>
    public async Task<HumanPhaseSnapshot?> GetSnapshotAsync(CancellationToken ct)
    {
        var state = await systemState.GetAsync(ct);
        var anchor = AnchorIfApplies(state);
        if (anchor is not { } since)
        {
            return null;
        }

        var o = opts.Value;
        var activeDays = await progress.CountOutboundActiveDaysAsync(since, ct);
        var tallies = await progress.ListConversationTalliesAsync(since, ct);
        return new HumanPhaseSnapshot(
            StartedOn: since,
            ActiveDays: activeDays,
            MinDays: o.MinDays,
            MinPeople: o.MinPeople,
            MinInbound: o.MinInbound,
            MinOutbound: o.MinOutbound,
            Conversations: tallies);
    }

    /// <summary>Âncora (marco do aquecimento) se a fase vale pra este chip; null se não vale.
    ///
    /// É AQUI que a produção fica protegida, e cada condição tem um porquê:
    ///  - EffectiveFrom null → recurso desligado. É o DEFAULT: nenhum deploy pode parar os stacks.
    ///  - WarmupStartedOn null → sem marco, não dá pra saber se o chip é novo → não trava.
    ///  - marco ANTERIOR ao corte → chip que já estava rodando → não trava.
    /// Chip re-pareado depois do corte cai no RestartWarmup(today) e passa a valer — é o desejado:
    /// chip re-pareado é chip novo.</summary>
    private DateOnly? AnchorIfApplies(SystemStateAggregate state)
    {
        if (opts.Value.EffectiveFrom is not { } cut)
        {
            return null;
        }
        if (state.WarmupStartedOn is not { } started || started < cut)
        {
            return null;
        }
        return started;
    }
}

/// <summary>Estado da Fase Humana num instante. Conversations é o placar por pessoa (a lista de
/// checagem da UI); o gate em si só olha os agregados.</summary>
public sealed record HumanPhaseSnapshot(
    DateOnly StartedOn,
    int ActiveDays,
    int MinDays,
    int MinPeople,
    int MinInbound,
    int MinOutbound,
    IReadOnlyList<ConversationTally> Conversations)
{
    /// <summary>Conversas que já valem: exige os DOIS lados. Uma conversa só de ida é justamente o
    /// padrão que a fase existe pra evitar — o valor está na resposta do outro.</summary>
    public int QualifiedPeople =>
        Conversations.Count(c => c.Inbound >= MinInbound && c.Outbound >= MinOutbound);

    /// <summary>Evidência E dias — as duas coisas. Só dias deixaria um chip pareado e esquecido
    /// sair da fase sem nunca ter conversado.</summary>
    public bool Satisfied => ActiveDays >= MinDays && QualifiedPeople >= MinPeople;

    public int DaysRemaining => Math.Max(0, MinDays - ActiveDays);

    public int PeopleRemaining => Math.Max(0, MinPeople - QualifiedPeople);
}
