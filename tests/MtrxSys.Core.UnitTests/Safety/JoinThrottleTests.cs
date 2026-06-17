using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>
/// JoinThrottle agora é PURA e baseada no estado persistido (entradas hoje + última entrada, vindos
/// do banco). Sem estado em memória → sobrevive a restart. Aqui: teto diário, intervalo entre
/// entradas e a janela do dia (meia-noite de Brasília).
/// </summary>
public sealed class JoinThrottleTests
{
    // 17/06/2026 12:00Z = 09:00 em Brasília (UTC-3).
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static JoinThrottle Build(CollectorOptions? opts = null) =>
        new(Options.Create(opts ?? new CollectorOptions()));

    [Fact]
    public void Sem_entradas_libera_e_mostra_teto_cheio()
    {
        var t = Build();

        t.Check(0, null, Now).Allowed.Should().BeTrue();
        var s = t.GetStatus(0, null, Now);
        s.JoinsToday.Should().Be(0);
        s.MaxPerDay.Should().Be(15);
        s.Remaining.Should().Be(15);
        s.WaitSeconds.Should().Be(0);
    }

    [Fact]
    public void Teto_diario_atingido_bloqueia()
    {
        var t = Build(new CollectorOptions { MaxJoinsPerDay = 2 });

        var d = t.Check(2, Now.AddMinutes(-30), Now);

        d.Allowed.Should().BeFalse();
        d.Reason.Should().Contain("Limite diário");
        t.GetStatus(2, Now.AddMinutes(-30), Now).Remaining.Should().Be(0);
    }

    [Fact]
    public void Dentro_do_intervalo_bloqueia_e_informa_a_espera()
    {
        // Intervalo entre 120 e 300s. Última entrada há 10s → ainda no intervalo.
        var t = Build();
        var last = Now.AddSeconds(-10);

        var d = t.Check(1, last, Now);

        d.Allowed.Should().BeFalse();
        d.RetryAfterSeconds.Should().BeGreaterThan(0);
        t.GetStatus(1, last, Now).WaitSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Passado_o_intervalo_libera()
    {
        var t = Build();
        var last = Now.AddSeconds(-301); // além do teto máximo do intervalo (300s)

        t.Check(1, last, Now).Allowed.Should().BeTrue();
        t.GetStatus(1, last, Now).WaitSeconds.Should().Be(0);
    }

    [Fact]
    public void StartOfTodayBrazil_e_meia_noite_de_Brasilia_em_UTC()
    {
        // 17/06 09:00 BRT → início do dia = 17/06 00:00 BRT = 17/06 03:00 UTC.
        JoinThrottle.StartOfTodayBrazil(Now).Should().Be(new DateTimeOffset(2026, 6, 17, 3, 0, 0, TimeSpan.Zero));

        // 17/06 23:00 BRT (= 18/06 02:00 UTC) ainda é o dia 17 em Brasília → mesma meia-noite.
        var lateNightUtc = new DateTimeOffset(2026, 6, 18, 2, 0, 0, TimeSpan.Zero);
        JoinThrottle.StartOfTodayBrazil(lateNightUtc).Should().Be(new DateTimeOffset(2026, 6, 17, 3, 0, 0, TimeSpan.Zero));
    }
}
