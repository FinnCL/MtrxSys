namespace MtrxSys.Core.Domain.SystemState;

/// <summary>Modo de operação da aba "Celular" — persistido no system_state (fonte da verdade do que a
/// página renderiza). É o toggle único da UI:
///  • <see cref="WahaOnly"/>  = WAHA + aparelho REAL físico (sem emulador; nada de emulador é renderizado).
///  • <see cref="Emulator"/>  = Emulador (Android em container) + WAHA (disparo + salvar contato na agenda).
/// Antes o "modo" era DERIVADO do container do emulador estar ligado (frágil, some no 502). Agora é um
/// estado explícito e persistido.</summary>
public enum PhoneDispatchMode
{
    WahaOnly = 0,
    Emulator = 1,
}
