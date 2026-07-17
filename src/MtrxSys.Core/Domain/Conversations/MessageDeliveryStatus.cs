namespace MtrxSys.Core.Domain.Conversations;

/// <summary>Status de ENTREGA de uma mensagem NOSSA (outbound), derivado do ack do WhatsApp que o WAHA
/// manda por <c>message.ack</c>. Existe pra tornar VISÍVEL no Chat o que antes só a auditoria do disparo
/// via — em especial a <see cref="Failed"/> (ack=-1), o sintoma de que o WhatsApp REJEITOU o envio
/// (companion restrito / reachout): a mensagem "sai" (201) mas não chega a ninguém, e sem isto parece
/// enviada na tela.
///
/// Os valores são ORDENADOS pelo progresso (maior = mais adiante), então <c>MarkAck</c> só avança e
/// nunca regride. <see cref="Failed"/> fica BAIXO de propósito: um ack de progresso posterior o supera,
/// e uma mensagem já entregue nunca "volta" a falha por um evento espúrio.</summary>
public enum MessageDeliveryStatus
{
    /// <summary>Sem ack ainda (ou mensagem recebida — inbound não tem status de entrega nosso).</summary>
    None = 0,

    /// <summary>ack -1 (ERROR): o WhatsApp rejeitou o envio — não entregou a ninguém.</summary>
    Failed = 1,

    /// <summary>ack 0 (PENDING): na fila, ainda não saiu.</summary>
    Pending = 2,

    /// <summary>ack 1 (SERVER): chegou ao servidor do WhatsApp.</summary>
    Sent = 3,

    /// <summary>ack 2 (DEVICE): entregue no aparelho do destinatário.</summary>
    Delivered = 4,

    /// <summary>ack 3+ (READ/PLAYED): lida.</summary>
    Read = 5,
}
