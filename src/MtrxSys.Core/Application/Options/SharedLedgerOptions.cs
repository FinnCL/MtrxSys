namespace MtrxSys.Core.Application.Options;

// Registro compartilhado de telefones entre os 10 ambientes (dedup de disparo + opt-out global).
// DESLIGADO por padrão (Mode = Off): sem isto, nada muda no comportamento atual.
public sealed class SharedLedgerOptions
{
    public const string SectionName = "SharedLedger";

    // Off = recurso desligado (no-op total).
    // Observe = consulta e grava no registro, mas NÃO suprime nada — só loga o que faria (validação segura).
    // Enforce = suprime de fato (pula o envio / exclui do público).
    public SharedLedgerMode Mode { get; set; } = SharedLedgerMode.Off;

    // Rótulo deste ambiente/chip no registro (A, B, ...). Apenas informativo.
    public string Chip { get; set; } = "?";
}

public enum SharedLedgerMode
{
    Off,
    Observe,
    Enforce,
}
