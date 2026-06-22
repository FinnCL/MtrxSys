namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Estado do aparelho virtual visto pela aba "Celular".
/// State: "unavailable" (sem docker/host não suporta) · "not_created" (container não existe) ·
/// "exited"/"created"/"running" (estado do container). ViewUrl = noVNC a embutir quando rodando.</summary>
public sealed record PhoneStatus(string State, bool Running, string? ViewUrl);

/// <summary>Orquestra o "aparelho virtual" (Android em container, docker-android) a partir do app,
/// pra TUDO ficar dentro da aba "Celular" — provisionar, ligar, instalar o WhatsApp, aplicar proxy,
/// ver a tela e os logs — sem janela/prompt/script externo. Implementação: docker CLI sobre o socket
/// montado (deploy Linux com /dev/kvm). Fail-safe: erros viram PhoneStatus("unavailable", ...), então
/// em ambientes sem docker a aba degrada sem quebrar.</summary>
public interface IPhoneOrchestrator
{
    /// <summary>Estado atual do aparelho virtual.</summary>
    Task<PhoneStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Android terminou de bootar? (adb getprop sys.boot_completed == 1). O container ficar
    /// "running" não basta — o Android ainda leva ~1-2 min pra subir. O botão "Provisionar número"
    /// espera isto antes de instalar o WhatsApp.</summary>
    Task<bool> IsBootedAsync(CancellationToken ct);

    /// <summary>Provisiona o aparelho: cria o container (se ainda não existe) e o liga. Idempotente.</summary>
    Task<PhoneStatus> ProvisionAsync(CancellationToken ct);

    /// <summary>Liga o aparelho já provisionado.</summary>
    Task<PhoneStatus> StartAsync(CancellationToken ct);

    /// <summary>Desliga o aparelho.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Logs do container (boot do Android etc.), exibidos na aba.</summary>
    Task<string> GetLogsAsync(int tail, CancellationToken ct);

    /// <summary>Instala o WhatsApp no Android (sideload do APK via adb). Retorna a saída do comando.</summary>
    Task<string> InstallWhatsAppAsync(CancellationToken ct);

    /// <summary>Aplica (ou limpa, com hostPort vazio) o http_proxy global do Android — o mesmo IP do
    /// chip/WAHA. Retorna a saída do comando.</summary>
    Task<string> SetProxyAsync(string? hostPort, CancellationToken ct);
}
