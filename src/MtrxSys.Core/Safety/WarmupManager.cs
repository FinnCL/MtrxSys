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
    private static readonly int[] DefaultCurve = [10, 15, 25, 40, 60, 80, 100];

    public async Task<bool> CanSendAsync(CancellationToken ct)
    {
        var snap = await GetSnapshotAsync(ct);
        return snap.SentToday < snap.EffectiveLimit;
    }

    public async Task IncrementAsync(CancellationToken ct)
    {
        var today = Today();
        var state = await systemState.GetAsync(ct);
        await counts.IncrementAsync(today, DayIndex(state, today), ct);
    }

    // Foto do aquecimento "agora": em que dia da curva estamos, o teto de hoje, quanto
    // já saiu e a curva inteira (pra UI mostrar o progresso e os próximos dias).
    public async Task<WarmupSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var today = Today();
        var state = await systemState.GetAsync(ct);
        var index = DayIndex(state, today);
        var curve = opts.Value.Curve is { Length: > 0 } configured ? configured : DefaultCurve;
        var limit = index >= curve.Length ? curve[^1] : curve[index];
        var existing = await counts.GetAsync(today, ct);
        var sent = existing?.SentCount ?? 0;
        return new WarmupSnapshot(StartedOn(state, today), index, limit, sent, curve, state.BonusFor(today));
    }

    // Data de início efetiva: o que estiver gravado no banco (reiniciável por clique)
    // tem prioridade; senão, o appsettings; em último caso, "hoje" (dia 0).
    private DateOnly StartedOn(SystemStateAggregate state, DateOnly today)
        => state.WarmupStartedOn ?? opts.Value.StartedOnUtc ?? today;

    private int DayIndex(SystemStateAggregate state, DateOnly today)
        => Math.Max(0, today.DayNumber - StartedOn(state, today).DayNumber);

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
