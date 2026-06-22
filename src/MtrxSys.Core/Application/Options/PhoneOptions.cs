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
}
