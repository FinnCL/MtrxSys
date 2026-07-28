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

    /// <summary>Todos os contatos da agenda externa. <c>null</c> = NÃO deu pra ler.</summary>
    /// <remarks>
    /// Existe pra COMPARAR a agenda com o banco e mostrar a diferença ao operador. NUNCA pra montar
    /// fila de disparo: o fluxo é de mão única (banco → agenda), porque é o próprio disparo que escreve
    /// aqui antes de cada envio frio. Ler de volta pra decidir quem recebe seria o sistema lendo o
    /// próprio rastro, e perderia o que só o banco sabe — quem já recebeu, quem pediu pra sair, e de
    /// qual chip o contato é (o gate anti-463).
    ///
    /// <para>🔴 O <c>null</c> NÃO é preciosismo de tipo. Devolver lista vazia numa falha de leitura faz
    /// a tela dizer "a conta e o sistema estão iguais" — a mesma frase de quando está tudo certo. O
    /// operador conclui que não há o que importar, e não há como ele saber que a pergunta nem chegou ao
    /// Google. É o modo de falha que custou um dia inteiro em 2026-07-27, quando "zero linhas" e "não
    /// consegui ler" estavam colapsados num só valor na leitura do banco do WhatsApp.</para>
    /// </remarks>
    Task<IReadOnlyList<AddressBookEntry>?> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AddressBookEntry>?>([]);
}

/// <summary>Um contato como ele existe na agenda externa (hoje, a conta Google do chip).</summary>
public sealed record AddressBookEntry(string PhoneE164, string? Name);
