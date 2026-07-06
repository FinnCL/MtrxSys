using FluentAssertions;
using MtrxSys.Core.Application.Warmup;

namespace MtrxSys.Core.UnitTests.Warmup;

public sealed class WarmupRampTests
{
    [Theory]
    [InlineData(4, 10, 3, 0, 4)]   // dia 0 = start
    [InlineData(4, 10, 3, 1, 6)]   // +2 (rampa suave de 3 dias)
    [InlineData(4, 10, 3, 2, 8)]   // +2
    [InlineData(4, 10, 3, 3, 10)]  // platô no max ao chegar em rampDays
    [InlineData(4, 10, 3, 9, 10)]  // muito depois = max
    [InlineData(4, 10, 3, -1, 4)]  // dia negativo é tratado como 0
    [InlineData(4, 20, 0, 2, 20)]  // rampDays<=0 = max direto
    [InlineData(5, 5, 3, 1, 5)]    // start==max = constante
    public void LimitFor_matches_expected(int start, int max, int rampDays, int dayIndex, int expected)
    {
        WarmupRamp.LimitFor(start, max, rampDays, dayIndex).Should().Be(expected);
    }

    [Fact]
    public void LimitFor_never_exceeds_max_nor_drops_below_start()
    {
        for (var day = 0; day <= 10; day++)
        {
            var limit = WarmupRamp.LimitFor(4, 10, 3, day);
            limit.Should().BeInRange(4, 10);
        }
    }
}
