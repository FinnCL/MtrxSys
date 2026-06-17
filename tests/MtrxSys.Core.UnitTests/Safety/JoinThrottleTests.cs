using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>
/// JoinThrottle.GetStatus — o snapshot que o painel usa pra deixar a trava anti-ban explícita
/// (entradas hoje / teto / restantes / espera). RNG fixo pra o intervalo ser determinístico.
/// </summary>
public sealed class JoinThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static JoinThrottle Build(CollectorOptions? opts = null, int interval = 120)
    {
        var rng = Substitute.For<IRandomSource>();
        rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(interval);
        return new JoinThrottle(rng, Options.Create(opts ?? new CollectorOptions()));
    }

    [Fact]
    public void Sem_entradas_mostra_teto_cheio_e_sem_espera()
    {
        var s = Build().GetStatus(Now);

        s.JoinsToday.Should().Be(0);
        s.MaxPerDay.Should().Be(15);
        s.Remaining.Should().Be(15);
        s.WaitSeconds.Should().Be(0);
    }

    [Fact]
    public void Apos_entrar_conta_uma_e_pede_o_resto_do_intervalo()
    {
        var t = Build(interval: 120);
        t.RegisterJoin(Now);

        var s = t.GetStatus(Now.AddSeconds(30)); // 30s depois de um intervalo de 120s

        s.JoinsToday.Should().Be(1);
        s.Remaining.Should().Be(14);
        s.WaitSeconds.Should().Be(90, "faltam 90s dos 120 do intervalo");
    }

    [Fact]
    public void Teto_diario_atingido_zera_o_restante()
    {
        var t = Build(new CollectorOptions { MaxJoinsPerDay = 2 });
        t.RegisterJoin(Now);
        t.RegisterJoin(Now.AddMinutes(10));

        var s = t.GetStatus(Now.AddMinutes(20));

        s.JoinsToday.Should().Be(2);
        s.Remaining.Should().Be(0);
    }

    [Fact]
    public void Virada_de_dia_zera_a_contagem()
    {
        var t = Build();
        t.RegisterJoin(Now);

        var s = t.GetStatus(Now.AddDays(1));

        s.JoinsToday.Should().Be(0, "a contagem é por dia (UTC)");
        s.Remaining.Should().Be(15);
    }
}
