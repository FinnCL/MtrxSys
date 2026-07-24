namespace MtrxSys.Core.Application.Options;

/// <summary>Config do "salvar+sincronizar contato na agenda" antes de disparar pra FRIO (anti-463).
/// Ver docs/google-contacts-sync.md. DESLIGADO por padrão (<see cref="Enabled"/> = false) → o motor
/// nem consulta o provider e o comportamento atual fica intacto. Por-stack: cada stack usa o refresh
/// token do SEU chip.</summary>
public sealed class AddressBookSyncOptions
{
    public const string SectionName = "AddressBookSync";

    /// <summary>IGNORADO para ativação (era o "gate" do pipeline, mas virou armadilha: nascia false e
    /// deixava a defesa anti-463 apagada mesmo com token válido — um dos furos que restringiram o chip do
    /// A). Hoje a ativação é AUTOMÁTICA: Provider=Google + RefreshToken presente LIGA sozinho (ver
    /// DependencyInjection). Desligar de propósito = Provider=None ou remover o token. Mantido só pra não
    /// quebrar bindings de env antigos; não altera mais o comportamento.</summary>
    public bool Enabled { get; set; } = true;

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
