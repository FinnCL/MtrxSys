namespace MtrxSys.Core.Application.Options;

/// <summary>Config do "salvar+sincronizar contato na agenda" antes de disparar pra FRIO (anti-463).
/// Ver docs/google-contacts-sync.md. DESLIGADO por padrão (<see cref="Enabled"/> = false) → o motor
/// nem consulta o provider e o comportamento atual fica intacto. Por-stack: cada stack usa o refresh
/// token do SEU chip.</summary>
public sealed class AddressBookSyncOptions
{
    public const string SectionName = "AddressBookSync";

    /// <summary>Liga o pipeline. Com false, o provider é NoOp (nada muda).</summary>
    public bool Enabled { get; set; }

    /// <summary>Provider da agenda: "None" (default) ou "Google" (People API na conta do chip).</summary>
    public string Provider { get; set; } = "None";

    /// <summary>Segundos que o job espera após CRIAR o contato, pro sync propagar até o WhatsApp do
    /// aparelho primário antes de enviar. Default 180s.</summary>
    public int GraceSeconds { get; set; } = 180;

    /// <summary>Credenciais OAuth da conta Google do chip (usadas quando Provider = "Google").</summary>
    public GoogleContactsOptions Google { get; set; } = new();
}

/// <summary>OAuth da conta Google do chip pro People API. Client id/secret são COMPARTILHADOS pelos 10;
/// o RefreshToken é POR CHIP (o do dono daquele stack). Ver docs/google-contacts-sync.md.</summary>
public sealed class GoogleContactsOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }
}
