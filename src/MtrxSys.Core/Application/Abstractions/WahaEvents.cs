namespace MtrxSys.Core.Application.Abstractions;

public static class WahaEvents
{
    public const string Message = "message";
    public const string MessageAny = "message.any";
    // Estado de entrega das mensagens (sensor anti-shadow-restriction). Assinado no config da sessão.
    public const string MessageAck = "message.ack";

    public static readonly IReadOnlySet<string> InboundMessageEvents =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Message, MessageAny };

    public static bool IsAck(string? evt) =>
        string.Equals(evt, MessageAck, StringComparison.OrdinalIgnoreCase);
}
