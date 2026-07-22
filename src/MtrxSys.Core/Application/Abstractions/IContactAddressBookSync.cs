namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Resultado de garantir um contato na agenda (Google) do chip.</summary>
public enum AddressBookSaveResult
{
    /// <summary>Provider inerte (desligado) — não faz nada.</summary>
    NotSupported,
    /// <summary>Já estava na agenda (sincronizado antes) — pode enviar.</summary>
    AlreadyPresent,
    /// <summary>Acabou de ser criado — precisa de tempo (grace) pra sincronizar antes de enviar.</summary>
    Created,
    /// <summary>Falhou ao salvar (credencial/rede) — NÃO arriscar o envio (evita 463).</summary>
    Failed,
}

/// <summary>Garante que um contato FRIO está salvo na agenda (ex.: Google Contacts) da conta do chip,
/// ANTES de disparar pra ele. Por quê: no NOWEB grátis, enviar pra quem nunca respondeu dá
/// <c>463 (account restricted / missing tctoken)</c> e derruba a sessão. A cadeia que destrava é
/// Google Contacts → aparelho primário sincroniza → WhatsApp do primário → o companion WAHA herda o
/// tctoken. Este contrato é o "salvar+sincronizar" desacoplado do provider (Google hoje; outro amanhã).
/// No-op por padrão (<see cref="IsEnabled"/> = false) — nada muda até configurar
/// <c>AddressBookSync:Enabled=true</c> com credenciais.</summary>
public interface IContactAddressBookSync
{
    /// <summary>Provider ativo? Quando false, o motor nem chama <see cref="EnsureSavedAsync"/>.</summary>
    bool IsEnabled { get; }

    /// <summary>Segundos que o job espera (Defer) depois de CRIAR o contato, pro sync propagar até o
    /// WhatsApp do primário antes do envio. Curto demais → ainda 463; longo demais → fila lenta.</summary>
    int GraceSeconds { get; }

    /// <summary>Garante o contato na agenda: procura; cria se faltar. Best-effort — nunca lança
    /// (mapeia exceção pra <see cref="AddressBookSaveResult.Failed"/>), só cancelamento propaga.</summary>
    Task<AddressBookSaveResult> EnsureSavedAsync(string phoneE164, string? name, CancellationToken ct);
}
