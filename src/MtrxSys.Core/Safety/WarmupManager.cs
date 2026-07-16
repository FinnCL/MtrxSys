using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Safety;

public sealed class WarmupManager(
    IDailySendCountsRepository counts,
    ISystemStateRepository systemState,
    IClock clock,
    IOptions<WarmupOptions> opts)
{
    // Fallback, usado se o appsettings não trouxer curva. Nunca "ilimitado": um teto ausente
    // anularia o aquecimento (o ponto todo é segurar o volume cedo).
    //
    // IGUAL à do appsettings de propósito: divergir faria quem lê o código concluir a curva errada
    // (a que VALE é a do appsettings — Api e Dispatcher, os dois).
    //
    // O índice conta DIAS COM ENVIO, não dias de calendário: dia sem disparo não consome a curva.
    // É o que deixa esta curva encaixar no cronograma sem gambiarra — os dias de conversa humana e
    // de aquecimento cruzado (que não disparam) não gastam degrau, então o 1º dia de disparo pega o
    // índice 0 sozinho. Sobe até o platô de 200/dia.
    //
    // A CURVA ABRE EM 5, E ISSO TEM HISTÓRIA — não é chute. Em 2026-07-15 um chip novo
    // (+557191072835) foi RESTRINGIDO pelo WhatsApp na 4ª mensagem do 1º dia. A curva abria em 15 ali;
    // o cronograma que a originou previa 15 só no dia 8 do chip, DEPOIS de uma semana de conversa
    // humana e maturação cruzada. Aplicar 15 no dia 1 de um chip frio foi o erro, e ele custou o chip.
    // Baixou pra 3 (fallback de chip frio) e depois pra este 5: a estratégia agora é AQUECER O CHIP 3
    // DIAS NA MÃO, no aparelho físico, ANTES do 1º disparo do sistema — então o índice 0 não cai mais
    // num chip gelado, e o 5 reconhece isso SEM apostar a conta (3 dias na mão não compram o salto pra
    // 15, que supunha 8 dias + maturação cruzada).
    //
    // O QUE O 5 ASSUME, e a curva NÃO consegue verificar: que o aquecimento manual de fato aconteceu.
    // Ela indexa por dias COM ENVIO PELO SISTEMA, então "dia 1 de chip frio que dispara" e "dia 4 de
    // chip aquecido na mão" são o MESMO índice 0 pra ela. Se o aquecimento for pulado, o 5 vira número
    // alto num chip morno-pra-frio — o mesmo erro que custou o chip, menor. Não subir daqui sem o
    // aquecimento garantido.
    //
    // A ressalva honesta, pra ninguém tratar estes números como fórmula: 4 mensagens não é volume,
    // é PADRÃO. Conta criada no dia, ZERO mensagens recebidas, e a 1ª atividade sendo textos quase
    // iguais pra quem nunca escreveu pra ela — isso é assinatura de bot com 4, com 3 ou com 1. Se a
    // causa for essa (e a evidência aponta pra lá), nenhuma curva salva: o que salva é ter conversa
    // com RESPOSTA antes de automatizar, que é o que o HumanPhaseGate faz e estava desligado.
    private static readonly int[] DefaultCurve =
        [5, 8, 12, 15, 15, 20, 20, 25, 25, 30, 35, 35, 45, 45, 55, 55, 70, 70, 85, 85, 100, 100, 120, 120, 150, 150, 180, 200];

    public async Task<bool> CanSendAsync(CancellationToken ct)
    {
        var snap = await GetSnapshotAsync(ct);
        return snap.SentToday < snap.EffectiveLimit;
    }

    public async Task IncrementAsync(CancellationToken ct)
    {
        var today = Today();
        var state = await systemState.GetAsync(ct);
        var index = await DayIndexAsync(AnchorDate(state), today, ct);
        await counts.IncrementAsync(today, index, ct);
    }

    // Foto do aquecimento "agora": em que dia da curva estamos, o teto de hoje, quanto
    // já saiu e a curva inteira (pra UI mostrar o progresso e os próximos dias).
    public async Task<WarmupSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var today = Today();
        var state = await systemState.GetAsync(ct);
        var index = await DayIndexAsync(AnchorDate(state), today, ct);
        var curve = opts.Value.Curve is { Length: > 0 } configured ? configured : DefaultCurve;
        var limit = index >= curve.Length ? curve[^1] : curve[index];
        var existing = await counts.GetAsync(today, ct);
        var sent = existing?.SentCount ?? 0;
        return new WarmupSnapshot(StartedOn(state, today), index, limit, sent, curve, state.BonusFor(today));
    }

    // Data de início (exibida na UI "iniciado em ...") E âncora do índice: dias ativos ANTES dela
    // não contam, então RestartWarmup (troca de chip) volta a curva ao Dia 0. Null = ambiente que
    // nunca reiniciou → MinValue conta todo o histórico (comportamento anterior preservado).
    private DateOnly StartedOn(SystemStateAggregate state, DateOnly today)
        => state.WarmupStartedOn ?? opts.Value.StartedOnUtc ?? today;

    private DateOnly AnchorDate(SystemStateAggregate state)
        => state.WarmupStartedOn ?? opts.Value.StartedOnUtc ?? DateOnly.MinValue;

    // Avança a curva APENAS quando o chip foi de fato usado: conta dias ativos em [since, today).
    // Hoje não conta — a 1ª mensagem do dia entra com o teto do dia atual, e amanhã a curva sobe.
    // Chip parado fica no mesmo nível; `since` (marco do restart) impede herdar histórico antigo.
    private async Task<int> DayIndexAsync(DateOnly since, DateOnly today, CancellationToken ct)
        => await counts.CountActiveDaysBeforeAsync(since, today, ct);

    private DateOnly Today() => IClock.ToBrasiliaDate(clock.UtcNow);
}

// Estado do aquecimento num instante. DayIndex é base-0 (dia 0 = primeiro dia).
// TodayLimit é o teto da CURVA; BonusToday é a liberação manual do operador pra hoje.
public sealed record WarmupSnapshot(
    DateOnly StartedOn, int DayIndex, int TodayLimit, int SentToday, int[] Curve, int BonusToday)
{
    // "Disparar todos" — liberação sem teto pra hoje.
    public bool UnlimitedToday => BonusToday >= int.MaxValue;

    // Teto que realmente vale agora: curva + extra liberado (ou ilimitado).
    public int EffectiveLimit => UnlimitedToday
        ? int.MaxValue
        : (int)Math.Min((long)TodayLimit + BonusToday, int.MaxValue);

    public int Remaining => UnlimitedToday ? int.MaxValue : Math.Max(0, EffectiveLimit - SentToday);

    // Bateu o teto efetivo e ainda há intenção de mandar mais? (gatilho do modal na UI)
    public bool AtCap => !UnlimitedToday && SentToday >= EffectiveLimit;

    // Teto do dia seguinte (pra UI: "amanhã sobe para X"); null se não há curva.
    public int? NextLimit => Curve.Length == 0
        ? null
        : DayIndex + 1 >= Curve.Length ? Curve[^1] : Curve[DayIndex + 1];

    // Teto final, quando a curva estabiliza.
    public int PlateauLimit => Curve.Length == 0 ? int.MaxValue : Curve[^1];
}
