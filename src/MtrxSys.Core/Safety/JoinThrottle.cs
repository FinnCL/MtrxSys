using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

/// <summary>
/// Trava anti-ban da ENTRADA em grupo: garante um intervalo mínimo (aleatório) entre joins e um
/// teto diário. A entrada já é manual (clique a clique); isto impede o "afobamento" que viraria
/// entrada em massa. Estado em memória (singleton) — localhost, single-process; reinício zera.
/// </summary>
public sealed class JoinThrottle(IRandomSource rng, IOptions<CollectorOptions> opts)
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastJoinAt;
    private DateOnly _countDay;
    private int _countToday;
    private int _currentIntervalSeconds;

    /// <summary>Pode entrar em um grupo agora? Não muta estado — só consulta.</summary>
    public JoinDecision Check(DateTimeOffset now)
    {
        lock (_gate)
        {
            var o = opts.Value;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var count = _countDay == today ? _countToday : 0;
            if (count >= Math.Max(1, o.MaxJoinsPerDay))
            {
                return new JoinDecision(false, 0, $"Limite diário de {o.MaxJoinsPerDay} entradas atingido. Tente amanhã.");
            }
            if (_lastJoinAt is { } last)
            {
                var need = TimeSpan.FromSeconds(_currentIntervalSeconds);
                var elapsed = now - last;
                if (elapsed < need)
                {
                    var wait = (int)Math.Ceiling((need - elapsed).TotalSeconds);
                    return new JoinDecision(false, wait, $"Aguarde {wait}s antes de entrar em outro grupo (anti-ban).");
                }
            }
            return new JoinDecision(true, 0, null);
        }
    }

    /// <summary>Registra uma entrada bem-sucedida: incrementa o contador do dia e sorteia o
    /// próximo intervalo mínimo.</summary>
    public void RegisterJoin(DateTimeOffset now)
    {
        lock (_gate)
        {
            var o = opts.Value;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (_countDay != today)
            {
                _countDay = today;
                _countToday = 0;
            }
            _countToday++;
            _lastJoinAt = now;
            var min = Math.Max(1, o.JoinMinIntervalSeconds);
            var max = Math.Max(min + 1, o.JoinMaxIntervalSeconds + 1);
            _currentIntervalSeconds = rng.NextInt(min, max);
        }
    }
}

public sealed record JoinDecision(bool Allowed, int RetryAfterSeconds, string? Reason);
