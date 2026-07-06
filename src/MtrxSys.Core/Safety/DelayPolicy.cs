using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

public sealed class DelayPolicy(IRandomSource rng, IOptions<DispatchOptions> opts)
{
    public TimeSpan NextDelay()
    {
        var o = opts.Value;
        var min = Math.Max(1, o.DelayMinSeconds);
        var max = Math.Max(min + 1, o.DelayMaxSeconds + 1);
        var seconds = rng.NextInt(min, max);
        return TimeSpan.FromSeconds(seconds);
    }

    // Cooldown curto (8-20s aleatório) entre check-exists de números PULADOS (inexistentes). Sem ele,
    // uma lista suja faria dezenas/centenas de consultas de validade em rajada — validação em massa é
    // sinal de bot (e rate-limit da própria API). Espaça as consultas sem ser tão lento quanto o delay
    // de envio (que só se aplica a quem REALMENTE recebe mensagem).
    public TimeSpan NextCheckCooldown()
    {
        var seconds = rng.NextInt(8, 21);
        return TimeSpan.FromSeconds(seconds);
    }
}
