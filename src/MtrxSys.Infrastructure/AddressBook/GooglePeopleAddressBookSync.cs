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
    // Quantos dígitos finais comparar pra casar um número já salvo. 8 = número local BR (SEM o 9º e
    // SEM DDI), que é o sufixo comum às variações (+55 / com e sem o 9º dígito). Colisão é rara e, no
    // pior caso, cria um contato duplicado (inofensivo) ou pula um save (o Defer re-tenta no ciclo).
    private const int MatchSuffixDigits = 8;

    private readonly PeopleServiceService _service;
    private readonly int _graceSeconds;
    private readonly ILogger<GooglePeopleAddressBookSync> _log;

    public GooglePeopleAddressBookSync(AddressBookSyncOptions options, ILogger<GooglePeopleAddressBookSync> log)
    {
        _log = log;
        _graceSeconds = Math.Max(0, options.GraceSeconds);
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
        try
        {
            if (await ExistsAsync(phoneE164, ct))
            {
                return AddressBookSaveResult.AlreadyPresent;
            }
            var person = new Person
            {
                Names = [new Name { GivenName = string.IsNullOrWhiteSpace(name) ? phoneE164 : name }],
                PhoneNumbers = [new PhoneNumber { Value = phoneE164 }],
            };
            await _service.People.CreateContact(person).ExecuteAsync(ct);
            _log.LogInformation("Google People: contato {Phone} criado na agenda do chip (anti-463).", phoneE164);
            return AddressBookSaveResult.Created;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // best-effort: qualquer erro do People API não pode derrubar o ciclo
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google People: falha ao garantir contato {Phone} na agenda.", phoneE164);
            return AddressBookSaveResult.Failed;
        }
#pragma warning restore CA1031
    }

    /// <summary>Já existe um contato com este número na conta? Varre as conexões (contatos) e casa pelo
    /// sufixo de dígitos (robusto a +55 / 9º dígito). O contato que criamos entra em "connections", então
    /// no ciclo seguinte ao Created isto devolve true e o envio libera.</summary>
    private async Task<bool> ExistsAsync(string phoneE164, CancellationToken ct)
    {
        var wanted = Suffix(phoneE164);
        if (wanted.Length == 0)
        {
            return false;
        }
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
                    if (Suffix(ph.CanonicalForm ?? ph.Value ?? string.Empty) == wanted)
                    {
                        return true;
                    }
                }
            }
            pageToken = resp.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken) && !ct.IsCancellationRequested);
        return false;
    }

    // Últimos N dígitos do número (ignora DDI/formatação). "" se tiver menos que N dígitos.
    private static string Suffix(string raw)
    {
        var digits = new string([.. raw.Where(char.IsDigit)]);
        return digits.Length >= MatchSuffixDigits ? digits[^MatchSuffixDigits..] : string.Empty;
    }

    public void Dispose() => _service.Dispose();
}
