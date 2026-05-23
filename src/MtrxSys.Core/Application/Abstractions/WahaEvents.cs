namespace MtrxSys.Core.Application.Abstractions;

public static class WahaEvents
{
    public const string Message = "message";
    public const string MessageAny = "message.any";

    public static readonly IReadOnlySet<string> InboundMessageEvents =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Message, MessageAny };
}
