namespace MtrxSys.Core.Domain.Campaigns;

public enum DispatchStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3,
    // Falhou uma vez (transitório) e voltou pro fim da fila pra um reenvio automático.
    // Mantém o motivo da falha visível (ErrorReason) enquanto aguarda a nova tentativa.
    Retrying = 4,
}
