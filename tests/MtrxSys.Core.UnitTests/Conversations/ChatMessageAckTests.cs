using FluentAssertions;
using MtrxSys.Core.Domain.Conversations;
using Xunit;

namespace MtrxSys.Core.UnitTests.Conversations;

public sealed class ChatMessageAckTests
{
    private static ChatMessage Outbound() => ChatMessage.Create(
        Guid.NewGuid(), Guid.NewGuid(), "wamid.X", MessageDirection.Outbound, null, "oi", DateTimeOffset.UtcNow);

    [Fact]
    public void Nasce_sem_status()
    {
        Outbound().DeliveryStatus.Should().Be(MessageDeliveryStatus.None);
    }

    [Fact]
    public void Ack_negativo_marca_falha()
    {
        var m = Outbound();
        m.MarkAck(-1);
        m.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed,
            "ack=-1 (ERROR) é o WhatsApp rejeitando o envio — o sintoma que ficava invisível");
    }

    [Fact]
    public void Progride_no_caminho_de_entrega_e_nao_regride()
    {
        var m = Outbound();
        m.MarkAck(1);
        m.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        m.MarkAck(2);
        m.DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
        m.MarkAck(3);
        m.DeliveryStatus.Should().Be(MessageDeliveryStatus.Read);
        m.MarkAck(1); // ack atrasado/fora de ordem
        m.DeliveryStatus.Should().Be(MessageDeliveryStatus.Read, "não regride");
    }

    [Fact]
    public void Ja_entregue_nao_vira_falha_por_ack_espurio()
    {
        var m = Outbound();
        m.MarkAck(2);  // Delivered
        m.MarkAck(-1); // ack espúrio depois
        m.DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered,
            "uma mensagem já entregue não regride pra falha");
    }
}
