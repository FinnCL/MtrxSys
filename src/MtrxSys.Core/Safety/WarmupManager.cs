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
    // Curva-padrão conservadora, usada se o appsettings não trouxer uma. Nunca "ilimitado":
    // um teto ausente anularia o aquecimento (o ponto todo é segurar o volume cedo).
    private static readonly int[] DefaultCurve = [10, 15, 25, 40, 50];

    public async Task<bool> CanSendAsync(CancellationToken ct)
    {
        var snap = await GetSnapshotAsync(ct);
        return snap.SentToday < snap.EffectiveLimit;
    }

    public async Task IncrementAsync(CancellationToken ct)
    {
        var today = Today();
        var index = await DayIndexAsync(today, ct);
        await counts.IncrementAsync(today, index, ct);
    }

    // Foto do aquecimento "agora": em que dia da curva estamos, o teto de hoje, quanto
    // já saiu e a curva inteira (pra UI mostrar o progresso e os próximos dias).
    public async Task<WarmupSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var today = Today();
        var state = await systemState.GetAsync(ct);
        var index = await DayIndexAsync(today, ct);
        var curve = opts.Value.Curve is { Length: > 0 } configured ? configured : DefaultCurve;
        var limit = index >= curve.Length ? curve[^1] : curve[index];
        var existing = await counts.GetAsync(today, ct);
        var sent = existing?.SentCount ?? 0;
        return new WarmupSnapshot(StartedOn(state, today), index, limit, sent, curve, state.BonusFor(today));
    }

    // Data de início (apenas para exibir na UI "iniciado em ..."). NÃO determina mais o
    // índice da curva — esse agora é calculado por dias REALMENTE usados (DayIndexAsync).
    private DateOnly StartedOn(SystemStateAggregate state, DateOnly today)
        => state.WarmupStartedOn ?? opts.Value.StartedOnUtc ?? today;

    // Avança a curva APENAS quando o chip foi de fato usado: conta dias ANTERIORES a hoje
    // com pelo menos 1 envio. Hoje não conta — assim a primeira mensagem do dia entra com
    // o teto do dia atual, e amanhã a curva sobe. Chip parado fica no mesmo nível.
    private async Task<int> DayIndexAsync(DateOnly today, CancellationToken ct)
        => await counts.CountActiveDaysBeforeAsync(today, ct);

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
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
