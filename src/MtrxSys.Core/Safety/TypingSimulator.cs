using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Safety;

public sealed class TypingSimulator(IWahaClient waha, IRandomSource rng, IOptions<DispatchOptions> opts)
{
    public async Task<int> SimulateAsync(string sessionId, string toPhoneOrChatId, string text, CancellationToken ct)
    {
        var o = opts.Value;
        var baseSeconds = Math.Clamp(text.Length / 25.0, o.TypingMinSeconds, o.TypingMaxSeconds);
        var jitter = (rng.NextDouble() * 2.0 - 1.0) * o.TypingJitter;
        var seconds = Math.Max(0.5, baseSeconds * (1 + jitter));
        var durationMs = (int)(seconds * 1000);

        await waha.StartTypingAsync(sessionId, toPhoneOrChatId, ct);
        try
        {
            await Task.Delay(durationMs, ct);
        }
        finally
        {
            try
            {
                await waha.StopTypingAsync(sessionId, toPhoneOrChatId, CancellationToken.None);
            }
#pragma warning disable CA1031
            catch
            {
                // best-effort cleanup
            }
#pragma warning restore CA1031
        }
        return durationMs;
    }
}
