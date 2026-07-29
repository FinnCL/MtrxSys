using FluentAssertions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.UnitTests.SystemState;

/// <summary>
/// O sensor de entrega passou a ter DUAS fontes escrevendo no mesmo campo: o `message.ack` do WAHA
/// (assíncrono) e a leitura da TELA do aparelho quando o envio é pela UI. O modo emulador nunca teve a
/// primeira, então o `DeliveredAt` ficava sempre null e o guard de shadow-restriction precisou ser
/// desligado — cegando justamente o caminho que mais precisava dele.
///
/// O que estes testes travam é a semântica da tradução (ver `AckFromUiDelivery` no DispatchEngine):
/// "delivered" e "read" contam como entregue, "sent" NÃO conta. Inverter isso é fácil e silencioso, e
/// o efeito seria inflar a taxa de entrega — o guard passaria a aprovar exatamente o cenário que ele
/// existe pra pegar (mensagem que sai e não chega).
/// </summary>
public sealed class SendAuditDeliveryFromUiTests
{
    private static readonly DateTimeOffset Quando = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static SendAuditEntry NovoEnvio() =>
        SendAuditEntry.Create(
            id: Guid.NewGuid(), dispatchJobId: Guid.NewGuid(), phoneE164: "+5511921404487",
            renderedText: "oi", typingMs: 0, delayMs: 0, occurredAt: Quando, waMessageId: string.Empty);

    [Theory]
    [InlineData(2)] // "delivered" na tela
    [InlineData(3)] // "read" na tela
    public void Entregue_e_lida_contam_como_entrega(int ack)
    {
        var e = NovoEnvio();

        e.MarkAck(ack, Quando);

        e.DeliveredAt.Should().Be(Quando);
    }

    [Fact]
    public void Enviada_nao_conta_como_entrega()
    {
        // Um traço só. Pode ser destinatário offline OU bloqueio de saída — a leitura da tela não
        // distingue, e por isso não se afirma entrega. É o que mantém o sensor honesto.
        var e = NovoEnvio();

        e.MarkAck(1, Quando);

        e.DeliveredAt.Should().BeNull();
        e.Ack.Should().Be(1);
    }

    [Fact]
    public void Status_desconhecido_nao_marca_nada()
    {
        // `AckFromUiDelivery` devolve null quando a tela não deu status legível, e aí nem MarkAck roda.
        // O envio fica como "sem informação de entrega", que é diferente de "não entregue".
        var e = NovoEnvio();

        e.Ack.Should().Be(0);
        e.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public void Ack_do_waha_nao_regride_o_que_a_tela_ja_marcou()
    {
        // Cenário real do modo híbrido: a tela leu "delivered" na hora do envio e, depois, chega um
        // message.ack atrasado com valor MENOR. O estado não pode andar pra trás, senão a mesma
        // mensagem entraria e sairia da conta de entregues conforme a ordem de chegada dos eventos.
        var e = NovoEnvio();
        e.MarkAck(2, Quando);

        e.MarkAck(1, Quando.AddMinutes(5));

        e.Ack.Should().Be(2);
        e.DeliveredAt.Should().Be(Quando);
    }

    [Fact]
    public void Leitura_posterior_promove_para_lida_sem_mover_o_carimbo_de_entrega()
    {
        var e = NovoEnvio();
        e.MarkAck(2, Quando);

        e.MarkAck(3, Quando.AddMinutes(10));

        e.Ack.Should().Be(3);
        e.DeliveredAt.Should().Be(Quando); // entrega é quando ENTREGOU, não quando leu
    }
}
