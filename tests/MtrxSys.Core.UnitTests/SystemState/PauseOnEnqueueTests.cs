using FluentAssertions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.UnitTests.SystemState;

/// <summary>
/// A regra que decide se ENFILEIRAR deve pausar. É uma trava de segurança ("nada sai sem o operador
/// mandar"), então tem que ser explícita e testada — e ela já esteve errada em produção.
///
/// O bug real: o POST /api/dispatch pausava SEMPRE. Mas o mesmo endpoint atende dois botões — o
/// "Adicionar para disparar" (fila vazia) e o "+ Adicionar N novo(s) à fila", que a tela oferece
/// DURANTE o envio. Clicar no segundo derrubava o envio em curso, e o banner seguia dizendo
/// "Enviando" porque a tela não olhava a resposta. O motor abortava no meio do typing.
///
/// A garantia NÃO é enfraquecida: ela é "nada sai sem o operador mandar", e numa fila que já está
/// rodando ele já mandou.
/// </summary>
public sealed class PauseOnEnqueueTests
{
    private static SystemStateAggregate Paused()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.Pause(SystemStateAggregate.ManualPauseReason);
        return s;
    }

    private static SystemStateAggregate Running()
    {
        var s = SystemStateAggregate.CreateInitial();
        s.Resume();
        return s;
    }

    [Fact]
    public void Fila_rodando_esta_enviando_agora_entao_somar_nao_pode_pausar() =>
        Running().IsSendingNow(queueHasPendingJobs: true).Should().BeTrue(
            "é o caso do '+ Adicionar à fila' durante o envio — pausar aqui derrubava o envio em curso");

    [Fact]
    public void Fila_vazia_e_nao_pausada_NAO_esta_enviando_entao_preparar_pausa() =>
        Running().IsSendingNow(queueHasPendingJobs: false).Should().BeFalse(
            "sistema recém-subido tem o MESMO 'não pausado' de um que está enviando — quem distingue "
            + "é a fila ter job. Sem isso, preparar uma fila nova não pausaria e a mensagem sairia "
            + "sem o operador clicar 'Iniciar envios'");

    [Fact]
    public void Pausado_com_fila_NAO_esta_enviando() =>
        Paused().IsSendingNow(queueHasPendingJobs: true).Should().BeFalse(
            "fila preparada e parada: continua parada, e somar mais contatos não a inicia");

    [Fact]
    public void Pausado_e_vazio_NAO_esta_enviando() =>
        Paused().IsSendingNow(queueHasPendingJobs: false).Should().BeFalse();
}

