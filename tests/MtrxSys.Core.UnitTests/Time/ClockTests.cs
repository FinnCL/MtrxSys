using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Core.UnitTests.Time;

// Trava a fronteira do "dia operacional" do anti-ban em Brasília (UTC-3). O contador de disparos
// deve virar à MEIA-NOITE DE BRASÍLIA (= 03:00 UTC), não à meia-noite UTC (= 21:00 de Brasília) —
// senão uma sessão noturna cruzaria o reset e deixaria estourar até 2x o teto na mesma noite.
public sealed class ClockTests
{
    [Theory]
    // Meia-noite UTC ainda é o dia ANTERIOR em Brasília (21h).
    [InlineData("2026-01-02T00:00:00Z", "2026-01-01")]
    // Um instante antes da virada de Brasília (23:59:59 BRT) — ainda dia 1.
    [InlineData("2026-01-02T02:59:59Z", "2026-01-01")]
    // 03:00 UTC == 00:00 BRT: vira o dia em Brasília.
    [InlineData("2026-01-02T03:00:00Z", "2026-01-02")]
    // Meio-dia UTC (09h BRT): mesmo dia nos dois fusos.
    [InlineData("2026-01-02T12:00:00Z", "2026-01-02")]
    public void ToBrasiliaDate_rolls_over_at_brazilian_midnight(string instantUtc, string expectedDate)
    {
        var instant = DateTimeOffset.Parse(instantUtc, System.Globalization.CultureInfo.InvariantCulture);

        IClock.ToBrasiliaDate(instant).Should().Be(DateOnly.Parse(expectedDate, System.Globalization.CultureInfo.InvariantCulture));
    }
}
