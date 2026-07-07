namespace MtrxSys.Core.Application.Abstractions;

// Envia um alerta operacional (ex.: chip offline) por um canal externo (webhook). No-op quando não
// configurado. É SEMPRE best-effort: uma falha ao alertar nunca pode derrubar ou travar quem chamou.
public interface IAlertNotifier
{
    Task NotifyAsync(string message, CancellationToken ct);
}
