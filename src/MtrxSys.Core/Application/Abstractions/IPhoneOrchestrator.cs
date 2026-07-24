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

    /// <summary>Proxy in-guest do emulador de pé? (gost escutando na :12345 + regra REDIRECT no nat OUTPUT,
    /// DENTRO do Android — a MESMA pós-condição do watchdog). É o que garante que o egresso sai pelo
    /// residencial e não pelo IP do datacenter: o SINAL de que é seguro registrar o chip. false quando não
    /// dá pra confirmar (container ausente, adb mudo, sem root, proxy fora) — fail-safe: a UI segura o
    /// registro. Default: false (engines sem esse conceito nunca liberam).</summary>
    Task<bool> IsEgressProxyUpAsync(CancellationToken ct) => Task.FromResult(false);

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
    /// <summary>O número está no WhatsApp, SEGUNDO O PRÓPRIO APARELHO? Equivalente LOCAL do
    /// check-exists do WAHA, sem API nenhuma: quando um contato da agenda é usuário da plataforma, o
    /// WhatsApp cria por conta própria um raw contact na conta <c>com.whatsapp</c> (com as linhas
    /// <c>vnd.com.whatsapp.profile</c>/<c>.voip.call</c>), agregado ao contato original. A ausência
    /// desse espelho, num contato JÁ sincronizado, é o sinal de que o número não tem WhatsApp.
    /// Medido em produção (2026-07-23): 115 contatos salvos → 113 espelhados → 2 não-usuários.
    /// PRÉ-REQUISITO: o contato precisa estar salvo E ter dado tempo de sincronizar (o espelho leva
    /// de ~2,5 a ~7 min pra aparecer). Por isso <c>null</c> ("não sei") quando ele nem está na agenda
    /// — quem chama deve salvar, esperar o grace e perguntar de novo, nunca tratar null como "não".
    /// </summary>
    /// <returns>true = é usuário; false = está na agenda há tempo e NÃO é usuário; null = não deu pra saber.</returns>
    Task<bool?> IsOnWhatsAppAsync(string phoneE164, CancellationToken ct) =>
        Task.FromResult<bool?>(null);

    Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct) =>
        Task.FromResult("gravação de contato não suportada neste engine.");

    /// <summary>Envia uma mensagem de WhatsApp DIRETO pela UI do emulador (o PRIMÁRIO), NÃO pelo WAHA.
    /// É o "Caminho A" anti-463: o companion NOWEB dá 463 em frio; o primário (dono da conta) manda
    /// normal. Abre o chat com a mensagem já preenchida via intent click-to-chat
    /// (whatsapp://send?phone=X&amp;text=Y — funciona pra número salvo OU não), acha o botão "enviar" por
    /// resource-id (uiautomator, robusto) e toca. Retorna resultado ESTRUTURADO (enviou? entrega? erro?).
    /// Default: não suportado.</summary>
    Task<WhatsAppSendResult> SendWhatsAppMessageAsync(string phoneE164, string text, CancellationToken ct) =>
        Task.FromResult(WhatsAppSendResult.Fail("envio pela UI não suportado neste engine."));
}

/// <summary>Resultado do envio pela UI do WhatsApp (Caminho A). Contrato claro em vez de uma string
/// que misturava status/ok/erro (que invertia o "ok" no sucesso).</summary>
/// <param name="Sent">A mensagem SAIU (botão tocado + campo esvaziou). false = não enviou.</param>
/// <param name="DeliveryStatus">Entrega NORMALIZADA (locale-independente): "sent" | "delivered" |
/// "read" | null (não lido ainda). Mata o "ack cego" do WAHA.</param>
/// <param name="Error">Motivo da falha quando <paramref name="Sent"/> = false; null no sucesso.</param>
public sealed record WhatsAppSendResult(bool Sent, string? DeliveryStatus, string? Error)
{
    public static WhatsAppSendResult Ok(string? deliveryStatus) => new(true, deliveryStatus, null);
    public static WhatsAppSendResult Fail(string error) => new(false, null, error);
}
