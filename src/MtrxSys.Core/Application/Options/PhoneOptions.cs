namespace MtrxSys.Core.Application.Options;

/// <summary>Config do "aparelho virtual" da aba "Celular": um Android em container (docker-android)
/// que a API provisiona/liga/instala/registra TUDO pela aba — sem prompt, sem script. O Android vira
/// o dispositivo PRINCIPAL do número (registro por SMS) e o WAHA fica como companion pro disparo.
/// Exige um host com /dev/kvm (servidor Linux). Ver docs/phone.md.</summary>
public sealed class PhoneOptions
{
    public const string SectionName = "Phone";

    /// <summary>Nome do container do Android deste ambiente (ex.: mtrx-android / mtrx2-android).</summary>
    public string ContainerName { get; set; } = "mtrx-android";

    /// <summary>Imagem do Android em container usada ao provisionar pela aba.</summary>
    public string Image { get; set; } = "budtmo/docker-android:emulator_14.0";

    /// <summary>Perfil de aparelho do emulador (EMULATOR_DEVICE). Precisa ser um nome suportado pela
    /// imagem docker-android (ex.: "Samsung Galaxy S10", "Pixel 8", "Nexus 5") — "Pixel 6" NÃO é
    /// suportado pela emulator_14.0.</summary>
    public string Device { get; set; } = "Samsung Galaxy S10";

    /// <summary>Porta do host pro noVNC (tela do Android) ao provisionar. Embutida na aba.</summary>
    public int NoVncPort { get; set; } = 6080;

    /// <summary>Volume Docker que guarda o estado do Android (app + sessão do WhatsApp).</summary>
    public string VolumeName { get; set; } = "android-data";

    /// <summary>URL do noVNC a embutir na aba quando o Android está no ar (ex.: http://localhost:6080).
    /// Vazio = a aba só mostra status/controle.</summary>
    public string ViewUrl { get; set; } = "";

    /// <summary>URL do APK do WhatsApp pro botão "Instalar WhatsApp" (sideload). Você fornece (não há
    /// URL oficial estável). Vazio = a aba explica como instalar manualmente.</summary>
    public string WhatsAppApkUrl { get; set; } = "";

    // ── Tetos de recurso do container on-demand (docker run) ──────────────────────────────────

    /// <summary>Teto de RAM do emulador (docker run --memory). O docker-android (S10, 1440x3040) usa
    /// ~6 GiB em regime, então 8g dá folga sem OOM. Como só 1-2 emuladores ligam por vez (lifecycle de
    /// keep-alive), o teto por-aparelho pode ser generoso. Vazio = sem teto.</summary>
    public string MemoryLimit { get; set; } = "8g";

    /// <summary>Teto de CPU do emulador (docker run --cpus). 4 acelera o boot (o maior gasto de CPU);
    /// em idle o emulador fica perto de 0. Vazio = sem teto.</summary>
    public string Cpus { get; set; } = "4";

    /// <summary>Política de restart (docker run --restart). Default "no": o primário fica DESLIGADO no
    /// regime normal (acordamos só no keep-alive), então não deve voltar sozinho após reboot do host.
    /// "unless-stopped" reproduz o comportamento antigo (24/7).</summary>
    public string RestartPolicy { get; set; } = "no";

    // ── Keep-alive do primário (o WhatsApp desloga o companion se o principal sumir ~14 dias) ──

    /// <summary>Liga o loop que acorda o primário periodicamente e o desliga após parear.</summary>
    public bool KeepAliveEnabled { get; set; } = true;

    /// <summary>Intervalo entre keep-alives, em horas. Default 240h (10 dias) — margem de 4 dias sob o
    /// limite de ~14 do WhatsApp.</summary>
    public int KeepAliveIntervalHours { get; set; } = 240;

    /// <summary>Período do tick do serviço (segundos). Curto pra o stop-after-pair reagir rápido.</summary>
    public int KeepAliveTickSeconds { get; set; } = 60;

    /// <summary>Após o WAHA vincular (WORKING), desliga o emulador pra poupar recurso.</summary>
    public bool StopAfterPairEnabled { get; set; } = true;

    /// <summary>Carência (min) entre ver o WAHA WORKING e desligar o primário — deixa o sync inicial de
    /// histórico terminar.</summary>
    public int StopAfterPairGraceMinutes { get; set; } = 5;

    /// <summary>Minutos que o primário fica online durante o keep-alive antes de desligar de novo.</summary>
    public int KeepAliveHoldMinutes { get; set; } = 3;

    /// <summary>Teto (s) pra esperar o Android bootar no keep-alive.</summary>
    public int KeepAliveBootTimeoutSeconds { get; set; } = 240;

    /// <summary>Teto (s) pra esperar o WAHA voltar a WORKING após o wake.</summary>
    public int KeepAliveReconnectTimeoutSeconds { get; set; } = 180;

    /// <summary>Slot de escalonamento 0-9 (janela do dia em que este stack acorda). -1 = derivar do
    /// ContainerName por hash estável, pra os 10 não acordarem juntos.</summary>
    public int KeepAliveStaggerSlot { get; set; } = -1;
}
