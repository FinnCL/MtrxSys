using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

/// <summary>
/// Trava anti-ban da ENTRADA em grupo — PURA e baseada no estado PERSISTIDO (entradas do dia + última
/// entrada, vindos do banco via joined_at). Sem estado em memória: o teto diário e o intervalo
/// SOBREVIVEM a restart/crash. O teto zera à MEIA-NOITE DE BRASÍLIA. O intervalo entre entradas é
/// determinístico a partir da última entrada (varia por entrada, mas é recomputável após restart).
/// </summary>
public sealed class JoinThrottle(IOptions<CollectorOptions> opts)
{
    // Brasil é UTC-3 (sem horário de verão desde 2019). O "dia" do teto é o dia de Brasília.
    private static readonly TimeSpan BrazilOffset = TimeSpan.FromHours(-3);

    /// <summary>Início do dia ATUAL no horário de Brasília, expresso em UTC — janela do teto diário.</summary>
    public static DateTimeOffset StartOfTodayBrazil(DateTimeOffset nowUtc)
    {
        var nowBr = nowUtc.ToOffset(BrazilOffset);
        var startBr = new DateTimeOffset(nowBr.Year, nowBr.Month, nowBr.Day, 0, 0, 0, BrazilOffset);
        return startBr.ToUniversalTime();
    }

    /// <summary>Pode entrar agora? Decide a partir do estado persistido (não muta nada).</summary>
    public JoinDecision Check(int joinsToday, DateTimeOffset? lastJoinedAt, DateTimeOffset now)
    {
        var max = Math.Max(1, opts.Value.MaxJoinsPerDay);
        if (joinsToday >= max)
        {
            return new JoinDecision(false, 0, $"Limite diário de {max} entradas atingido. Zera à meia-noite (horário de Brasília).");
        }
        var wait = WaitSeconds(lastJoinedAt, now);
        if (wait > 0)
        {
            return new JoinDecision(false, wait, $"Aguarde {wait}s antes de entrar em outro grupo (anti-ban).");
        }
        return new JoinDecision(true, 0, null);
    }

    /// <summary>Snapshot pro painel (entradas hoje / teto / restantes / espera). Não muta.</summary>
    public JoinThrottleStatus GetStatus(int joinsToday, DateTimeOffset? lastJoinedAt, DateTimeOffset now)
    {
        var max = Math.Max(1, opts.Value.MaxJoinsPerDay);
        return new JoinThrottleStatus(joinsToday, max, Math.Max(0, max - joinsToday), WaitSeconds(lastJoinedAt, now));
    }

    private int WaitSeconds(DateTimeOffset? lastJoinedAt, DateTimeOffset now)
    {
        if (lastJoinedAt is not { } last)
        {
            return 0;
        }
        var need = TimeSpan.FromSeconds(IntervalSeconds(last));
        var elapsed = now - last;
        return elapsed < need ? (int)Math.Ceiling((need - elapsed).TotalSeconds) : 0;
    }

    // Intervalo mínimo (segundos) até a próxima entrada, DETERMINÍSTICO a partir da última entrada:
    // varia dentro de [min, max] conforme o instante da entrada — recomputável após restart (não
    // depende de estado em memória), mantendo o espaçamento "imprevisível" do anti-ban.
    private int IntervalSeconds(DateTimeOffset lastJoinedAt)
    {
        var o = opts.Value;
        var min = Math.Max(1, o.JoinMinIntervalSeconds);
        var max = Math.Max(min, o.JoinMaxIntervalSeconds);
        var span = max - min + 1;
        var seed = (int)(((lastJoinedAt.ToUnixTimeSeconds() % span) + span) % span);
        return min + seed;
    }
}

public sealed record JoinDecision(bool Allowed, int RetryAfterSeconds, string? Reason);

/// <summary>Estado da trava de entrada pro painel: entradas hoje, teto diário, quantas restam e
/// segundos até a próxima liberação (0 = pode entrar agora).</summary>
public sealed record JoinThrottleStatus(int JoinsToday, int MaxPerDay, int Remaining, int WaitSeconds);
