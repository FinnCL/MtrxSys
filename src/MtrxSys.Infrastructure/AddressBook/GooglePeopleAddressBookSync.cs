using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.AddressBook;

/// <summary>Salva o contato na conta Google do chip via People API, pra o aparelho primário sincronizar
/// e o companion WAHA herdar o tctoken (anti-463). Autentica por refresh token (OAuth offline) — sem
/// interação. Ver docs/google-contacts-sync.md. Best-effort: qualquer falha vira
/// <see cref="AddressBookSaveResult.Failed"/> (o motor adia o job, NÃO arrisca o 463).</summary>
public sealed class GooglePeopleAddressBookSync : IContactAddressBookSync, IDisposable
{
    // Escopo de escrita de contatos. Precisa bater com o consentido ao gerar o refresh token.
    private const string ContactsScope = "https://www.googleapis.com/auth/contacts";
    // Dígitos finais comparados pra casar um número já salvo. 11 = número nacional BR COM o 9º (DDD+9+8).
    // Inclui o DDD de propósito: com só 8 (número local), dois DDDs diferentes colidiam e um FALSO
    // "já existe" mandaria pra um frio de verdade (o 463 que a feature evita). 11 torna a colisão
    // desprezível; o custo é, no MÁXIMO, um contato duplicado (inofensivo) quando o usuário já tinha o
    // número salvo em outro formato — sempre erramos pro lado seguro (duplicar, nunca pular um frio).
    private const int MatchNationalDigits = 11;
    // Piso do grace: 0 faria o job re-sair NA HORA (sem esperar o sync propagar) → 463. 30s garante
    // uma espera mínima real. O default é 180.
    private const int MinGraceSeconds = 30;

    private readonly PeopleServiceService _service;
    private readonly int _graceSeconds;
    private readonly ILogger<GooglePeopleAddressBookSync> _log;
    // Espelho em memória dos números já na agenda (sufixo nacional). Carregado UMA vez (a agenda
    // inteira) no 1º uso e mantido daí em diante — sem isto, cada envio frio revarreria connections.list
    // (chamada cara + estoura a cota do People API). Depois do load, existência é O(1) em memória e todo
    // Create atualiza o set. Perde no restart (recarrega 1x). Lock: o loop de disparo é sequencial, mas
    // o provider é singleton — o lock é barato e correto.
    private readonly HashSet<string> _known = [];
    private readonly object _knownLock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _loaded;
    // Token revogado/expirado (invalid_grant): PARA de chamar o Google (senão martelaria a cada frio, a
    // cada grace, pra sempre — o token de modo "Testing" morre em 7 dias). Fica Failed até trocar o
    // token e reiniciar o dispatcher; os frios ficam adiados (seguro, sem 463). Race é benigno.
    private volatile bool _authDead;

    public GooglePeopleAddressBookSync(AddressBookSyncOptions options, ILogger<GooglePeopleAddressBookSync> log)
    {
        _log = log;
        _graceSeconds = Math.Max(MinGraceSeconds, options.GraceSeconds);
        var g = options.Google;
        // Fluxo OAuth "offline": reconstrói a credencial a partir do refresh token guardado; a lib
        // troca por access token e renova sozinha. Sem client id/secret/refresh o DI nem registra este
        // provider (cai no NoOp), então aqui eles estão presentes.
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = g.ClientId, ClientSecret = g.ClientSecret },
            Scopes = [ContactsScope],
        });
        var credential = new UserCredential(flow, "chip", new TokenResponse { RefreshToken = g.RefreshToken });
        _service = new PeopleServiceService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "MtrxSys",
        });
    }

    public bool IsEnabled => true;
    public int GraceSeconds => _graceSeconds;

    public async Task<AddressBookSaveResult> EnsureSavedAsync(string phoneE164, string? name, CancellationToken ct)
    {
        // Token morto: não martela o Google (H2). Frios ficam adiados até trocar o token + reiniciar.
        if (_authDead)
        {
            return AddressBookSaveResult.Failed;
        }
        var suffix = Suffix(phoneE164);
        try
        {
            await EnsureLoadedAsync(ct);
            if (suffix.Length > 0)
            {
                lock (_knownLock)
                {
                    if (_known.Contains(suffix))
                    {
                        return AddressBookSaveResult.AlreadyPresent;
                    }
                }
            }
            var person = new Person
            {
                Names = [new Name { GivenName = string.IsNullOrWhiteSpace(name) ? phoneE164 : name }],
                PhoneNumbers = [new PhoneNumber { Value = phoneE164 }],
            };
            await _service.People.CreateContact(person).ExecuteAsync(ct);
            Remember(suffix);
            _log.LogInformation("Google People: contato {Phone} criado na agenda do chip (anti-463).", phoneE164);
            return AddressBookSaveResult.Created;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TokenResponseException ex) when (IsDeadToken(ex))
        {
            _authDead = true;
            _log.LogError(
                ex,
                "Google People: refresh token INVÁLIDO/REVOGADO (invalid_grant). Sync de agenda desligado "
                + "até trocar o token (docs/google-contacts-sync.md) e reiniciar o dispatcher. Os contatos "
                + "frios ficam ADIADOS (sem 463), não são enviados.");
            return AddressBookSaveResult.Failed;
        }
#pragma warning disable CA1031 // best-effort: qualquer outro erro do People API (cota/rede) não derruba o ciclo
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google People: falha (transitória?) ao garantir contato {Phone}.", phoneE164);
            return AddressBookSaveResult.Failed;
        }
#pragma warning restore CA1031
    }

    /// <summary>Carrega a agenda inteira UMA vez pro espelho em memória (paginado). Depois disso a
    /// existência é resolvida em memória — nenhum connections.list por envio.</summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded)
            {
                return;
            }
            var loaded = new HashSet<string>();
            string? pageToken = null;
            do
            {
                var req = _service.People.Connections.List("people/me");
                req.PersonFields = "phoneNumbers";
                req.PageSize = 1000;
                req.PageToken = pageToken;
                var resp = await req.ExecuteAsync(ct);
                foreach (var p in resp.Connections ?? [])
                {
                    foreach (var ph in p.PhoneNumbers ?? [])
                    {
                        var s = Suffix(ph.CanonicalForm ?? ph.Value ?? string.Empty);
                        if (s.Length > 0)
                        {
                            loaded.Add(s);
                        }
                    }
                }
                pageToken = resp.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken) && !ct.IsCancellationRequested);

            lock (_knownLock)
            {
                _known.UnionWith(loaded);
            }
            _loaded = true;
            _log.LogInformation("Google People: agenda carregada ({Count} números) — checagem agora é em memória.", loaded.Count);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void Remember(string suffix)
    {
        if (suffix.Length == 0)
        {
            return;
        }
        lock (_knownLock)
        {
            _known.Add(suffix);
        }
    }

    private static bool IsDeadToken(TokenResponseException ex)
        => string.Equals(ex.Error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);

    // Últimos N dígitos do número (ignora DDI/formatação). "" se tiver menos que N dígitos.
    private static string Suffix(string raw)
    {
        var digits = new string([.. raw.Where(char.IsDigit)]);
        return digits.Length >= MatchNationalDigits ? digits[^MatchNationalDigits..] : string.Empty;
    }

    public void Dispose()
    {
        _service.Dispose();
        _loadGate.Dispose();
    }
}
