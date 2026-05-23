namespace MtrxSys.Core.Messaging;

public sealed record MessageMatrix(
    IReadOnlyList<string> Greetings,
    IReadOnlyList<string> Intros,
    IReadOnlyList<string> Hooks,
    IReadOnlyList<string> OptOuts);
