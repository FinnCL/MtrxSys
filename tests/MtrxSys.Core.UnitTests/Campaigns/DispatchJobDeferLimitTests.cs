using FluentAssertions;
using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Core.UnitTests.Campaigns;

/// <summary>
/// Adiar é diferente de tentar: adiar NÃO pode queimar o `AttemptCount` (escasso, teto 2), senão um
/// hiccup na checagem de número consumiria as tentativas de ENVIO do contato. Mas adiamento sem fim
/// entope a fila, e isso é fácil de não enxergar porque cada volta parece inofensiva.
///
/// O caso concreto: no aparelho FÍSICO não há root, então não existe o `wa.db` que afirma "este número
/// NÃO é usuário" — a checagem só sabe dizer "sim" ou "não sei". Um número que genuinamente não tem
/// WhatsApp responderia "não sei" para sempre, e cada volta consome o MESMO intervalo de envio que uma
/// mensagem real consumiria.
///
/// Estes testes travam as duas metades: adiar não gasta tentativa, e adiar tem fim.
/// </summary>
public sealed class DispatchJobDeferLimitTests
{
    private static readonly DateTimeOffset Agora = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static DispatchJob NovoJob() =>
        DispatchJob.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Agora);

    [Fact]
    public void Adiar_nao_consome_tentativa_de_envio()
    {
        var job = NovoJob();

        for (var i = 0; i < 50; i++)
        {
            job.Defer(Agora.AddMinutes(8), "aguardando o WhatsApp reconhecer o número");
        }

        job.AttemptCount.Should().Be(0, "adiar não é tentar enviar");
        job.DeferCount.Should().Be(50);
    }

    [Fact]
    public void Adiar_conta_e_estoura_o_limite()
    {
        var job = NovoJob();
        job.ExceededDeferLimit(3).Should().BeFalse();

        job.Defer(Agora, "1");
        job.Defer(Agora, "2");
        job.ExceededDeferLimit(3).Should().BeFalse("ainda tem uma volta");

        job.Defer(Agora, "3");

        job.ExceededDeferLimit(3).Should().BeTrue();
    }

    [Fact]
    public void Limite_zero_significa_sem_teto()
    {
        // Escape hatch pra quem quiser o comportamento antigo (adiar pra sempre) sem mexer em código.
        var job = NovoJob();
        for (var i = 0; i < 1000; i++)
        {
            job.Defer(Agora, "sem fim");
        }

        job.ExceededDeferLimit(0).Should().BeFalse();
    }

    [Fact]
    public void Job_novo_nunca_estourou()
    {
        NovoJob().ExceededDeferLimit(1).Should().BeFalse(
            "um job que nunca foi adiado não pode nascer pulado");
    }

    [Fact]
    public void Retry_e_defer_contam_em_campos_separados()
    {
        // Se compartilhassem contador, um hiccup de checagem gastaria a tentativa de envio do contato
        // (ou o contrário), e o motivo do descarte no log apontaria pra causa errada.
        var job = NovoJob();

        job.Defer(Agora, "checagem indisponível");
        job.ScheduleRetry(Agora, "falha de envio");

        job.DeferCount.Should().Be(1);
        job.AttemptCount.Should().Be(1);
    }
}
