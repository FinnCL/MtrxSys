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

    /// <summary>Envia uma tecla de navegação do Android (back/home/recents) via adb keyevent — pros
    /// botões ◁ ○ □ da aba (o emulador em modo gestos não mostra a barra). Default: não suportado.</summary>
    Task<string> SendKeyAsync(string key, CancellationToken ct) =>
        Task.FromResult("navegação não suportada neste engine.");

    /// <summary>Digita um texto no campo focado do Android (adb input text) — pra colar de fora do
    /// emulador (ex.: código de pareamento do WAHA). Default: não suportado.</summary>
    Task<string> SendTextAsync(string text, CancellationToken ct) =>
        Task.FromResult("digitação não suportada neste engine.");

    /// <summary>Lê o número do WhatsApp registrado no emulador (registration_jid) — pra auto-preencher
    /// o Passo 2 e evitar digitar o número errado. Vazio se não achar. Default: não suportado.</summary>
    Task<string> GetWhatsAppNumberAsync(CancellationToken ct) => Task.FromResult("");

    /// <summary>Abre uma URL no WhatsApp do emulador via intent VIEW (adb am start) — o deep link de
    /// vínculo por QR (a URL do QR do WAHA), que abre "Deseja conectar um dispositivo?" SEM câmera nem
    /// rate limit; o usuário toca "Continuar". Default: não suportado.</summary>
    Task<string> OpenUrlAsync(string url, CancellationToken ct) =>
        Task.FromResult("abertura de URL não suportada neste engine.");

    /// <summary>"Trocar chip": zera o WhatsApp do emulador (pm clear) pra registrar OUTRO número —
    /// volta pra tela de boas-vindas. A conta velha sai do app (não do servidor). Default: não
    /// suportado.</summary>
    Task<string> ClearWhatsAppAsync(CancellationToken ct) =>
        Task.FromResult("troca de chip não suportada neste engine.");

    /// <summary>Grava um número na AGENDA do Android do emulador (contacts provider) — pra o disparo
    /// sair pra um "contato salvo" (perfil menos-robô, ajuda anti-ban). Chamado pelo DispatchEngine
    /// antes de cada envio: IDEMPOTENTE (não duplica) e best-effort. Default: não suportado.</summary>
    Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct) =>
        Task.FromResult("gravação de contato não suportada neste engine.");
}
