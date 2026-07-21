using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Contacts;

/// <summary>
/// Cadastro manual de contatos: o usuário digita ou cola uma lista de números (um por linha,
/// vírgula ou ponto-e-vírgula) e eles entram no disparo, igual aos importados de grupo.
/// Diferente do import de grupo, a entrada é NÃO confiável — usamos <see cref="BrazilPhoneValidator.Validate"/>
/// (estrito) e tentamos uma única auto-correção previsível: inserir o 9º dígito em celular antigo.
/// Tudo que sobra fora do padrão volta como "Inválido" com o motivo, sem derrubar o resto do lote.
/// </summary>
public sealed class AddManualContactsUseCase(
    IContactRepository contacts,
    IUnitOfWork uow,
    IClock clock,
    BrazilPhoneValidator phones,
    WarmupSeedEnroller seed)
{
    /// <summary>Grupo virtual onde caem os números avulsos (sem grupo do WhatsApp).</summary>
    public const string DefaultGroupTag = "Avulsos";

    public async Task<ManualImportResult> ExecuteAsync(
        IReadOnlyList<string> rawNumbers, string? groupTag, CancellationToken ct)
    {
        var tag = string.IsNullOrWhiteSpace(groupTag) ? DefaultGroupTag : groupTag.Trim();

        // Pré-aloca o resultado em ORDEM de entrada: o usuário precisa casar cada linha colada
        // com seu status. Os inválidos já ficam resolvidos aqui; os válidos esperam o dedup em lote.
        var results = new ManualLineResult?[rawNumbers.Count];
        var pending = new List<(int Index, string Raw, PhoneNumber Phone, string? Correction)>();

        for (var i = 0; i < rawNumbers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = rawNumbers[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                results[i] = ManualLineResult.Invalid(raw, "Linha vazia.");
                continue;
            }

            var (phone, correction, error) = Resolve(raw);
            if (phone is null)
            {
                results[i] = ManualLineResult.Invalid(raw, error ?? "Número inválido.");
                continue;
            }
            pending.Add((i, raw, phone, correction));
        }

        // Um único SELECT pros já-existentes (evita N+1 ao colar centenas). Procura tanto a forma
        // canônica (com o 9) quanto a legada (sem o 9): contatos importados de grupo podem estar
        // salvos sem o 9 (NormalizeTrusted mantém o número cru da WAHA quando a lib rejeita). Sem
        // isso, colar a forma moderna criaria um 2º contato da MESMA pessoa → disparo duplicado.
        var lookupKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pending)
        {
            lookupKeys.Add(p.Phone.E164);
            var legacy = LegacyNoNinthVariant(p.Phone.E164);
            if (legacy is not null)
            {
                lookupKeys.Add(legacy);
            }
        }
        var existing = await contacts.GetByPhonesAsync(lookupKeys.ToList(), ct);
        var known = new Dictionary<string, Contact>(existing, StringComparer.Ordinal);

        var added = 0;
        var duplicated = 0;
        var corrected = 0;
        // Telefones dos contatos NOVOS deste lote — pra auto-inscrever no Círculo de Aquecimento se o
        // chip está nos primeiros dias do aquecimento (ver WarmupSeedEnroller). Manuais entram sem nome.
        var newlyCreated = new List<(string PhoneE164, string? Name)>();

        foreach (var (index, raw, phone, correction) in pending)
        {
            ct.ThrowIfCancellationRequested();
            var dup = FindExisting(known, phone.E164);
            if (dup is not null)
            {
                // Já existe (na forma canônica OU legada sem o 9): traz de volta se estava descartado
                // e preenche o grupo se estava vazio (mesma semântica do re-import de grupo). NÃO
                // troca o telefone salvo — o legado sem o 9 é como o WhatsApp roteia aquele número.
                if (dup.ReimportInto(tag))
                {
                    await contacts.UpdateAsync(dup, ct);
                }
                duplicated++;
                results[index] = ManualLineResult.Duplicate(raw, dup.Phone.E164);
                continue;
            }

            var contact = Contact.Create(
                id: Guid.NewGuid(),
                phone: phone,
                name: null,
                groupTag: tag,
                theme: null,
                optInAt: clock.UtcNow);
            await contacts.AddAsync(contact, ct);
            known[phone.E164] = contact; // número repetido no mesmo lote reaproveita o contato
            newlyCreated.Add((phone.E164, null));
            added++;
            if (correction is not null)
            {
                corrected++;
                results[index] = ManualLineResult.Corrected(raw, phone.E164, correction);
            }
            else
            {
                results[index] = ManualLineResult.Ok(raw, phone.E164);
            }
        }

        // Semente do Círculo de Aquecimento: contatos introduzidos nos primeiros dias do aquecimento
        // entram no círculo (persistido junto no commit abaixo). No-op fora da janela ou sem novos.
        await seed.EnrollIfSeedPhaseAsync(newlyCreated, ct);
        await uow.SaveChangesAsync(ct);
        return new ManualImportResult(
            Total: rawNumbers.Count,
            Added: added,
            Duplicated: duplicated,
            Corrected: corrected,
            Lines: results.Select(r => r!).ToList());
    }

    /// <summary>
    /// Resolve um número cru pro E.164: tenta validar como está; se falhar e parecer um celular
    /// antigo (DDD + 8 dígitos começando em 6-9), tenta inserir o 9º dígito e revalida. Devolve o
    /// rótulo da correção quando precisou inserir o 9 (a única auto-correção que é inferência).
    /// </summary>
    private (PhoneNumber? Phone, string? Correction, string? Error) Resolve(string raw)
    {
        var direct = phones.Validate(raw);
        if (direct.IsSuccess && direct.Value is not null)
        {
            // Ferramenta é só Brasil (anti-ban BR). A lib aceita estrangeiro com DDI explícito
            // (ex.: +1...), então barramos aqui — e ainda pega um número BR digitado errado que
            // por acaso valide como de outro país. O caminho de auto-9 abaixo já é sempre +55.
            return IsBrazil(direct.Value)
                ? (direct.Value, null, null)
                : (null, null, "Só números do Brasil (DDI +55).");
        }

        var withNinth = TryBuildWithNinthDigit(raw);
        if (withNinth is not null)
        {
            var fixedUp = phones.Validate(withNinth);
            if (fixedUp.IsSuccess && fixedUp.Value is not null)
            {
                return (fixedUp.Value, "9º dígito inserido", null);
            }
        }

        return (null, null, direct.Error);
    }

    private static bool IsBrazil(PhoneNumber phone) =>
        phone.E164.StartsWith("+55", StringComparison.Ordinal);

    /// <summary>
    /// Procura um contato existente pela forma canônica e, se não achar, pela forma legada sem o 9
    /// (celular antigo importado de grupo). Evita criar duplicado da mesma pessoa.
    /// </summary>
    private static Contact? FindExisting(Dictionary<string, Contact> known, string e164)
    {
        if (known.TryGetValue(e164, out var direct))
        {
            return direct;
        }
        var legacy = LegacyNoNinthVariant(e164);
        if (legacy is not null && known.TryGetValue(legacy, out var byLegacy))
        {
            return byLegacy;
        }
        return null;
    }

    /// <summary>
    /// Forma legada (sem o 9) de um celular canônico, ou null se não for um celular com 9. Ex.:
    /// "+5511987654321" → "+551187654321". Um nacional de 11 dígitos com '9' após o DDD é sempre
    /// celular (fixo tem 10), então não há colisão com fixos.
    /// </summary>
    private static string? LegacyNoNinthVariant(string e164)
    {
        if (!e164.StartsWith("+55", StringComparison.Ordinal))
        {
            return null;
        }
        var national = e164[3..]; // DDD(2) + assinante
        if (national.Length != 11 || national[2] != '9')
        {
            return null;
        }
        return $"+55{national[..2]}{national[3..]}"; // remove o 9 logo após o DDD
    }

    /// <summary>
    /// Se o número cru for um celular no formato antigo (DDD + 8 dígitos, assinante começando em
    /// 6-9), monta o candidato com o 9 inserido após o DDD. Fixos (começam em 2-5) e qualquer outro
    /// tamanho retornam null — não há inferência segura, então deixamos a validação rejeitar.
    /// </summary>
    private static string? TryBuildWithNinthDigit(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());

        // Tira o DDI 55 quando presente, pra raciocinar só sobre DDD + assinante.
        if (digits.Length == 12 && digits.StartsWith("55", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        if (digits.Length != 10)
        {
            return null; // só tratamos DDD(2) + assinante(8)
        }

        var ddd = digits[..2];
        var subscriber = digits[2..];
        if (subscriber[0] < '6')
        {
            return null; // fixo (2-5): não recebe o 9
        }

        return $"+55{ddd}9{subscriber}";
    }
}

/// <summary>Status de uma linha colada. Identifica o que aconteceu com cada número.</summary>
public enum ManualLineStatus
{
    /// <summary>Já estava no padrão; só normalizado pra E.164.</summary>
    Ok,
    /// <summary>Estava fora do padrão e foi auto-corrigido (ver <see cref="ManualLineResult.Correction"/>).</summary>
    Corrected,
    /// <summary>Já existia no banco; não criou duplicado.</summary>
    Duplicate,
    /// <summary>Não foi possível normalizar com segurança (ver <see cref="ManualLineResult.Reason"/>).</summary>
    Invalid,
}

/// <summary>Resultado por linha, na mesma ordem em que o usuário colou.</summary>
public sealed record ManualLineResult(
    string Input,
    ManualLineStatus Status,
    string? Phone,
    string? Correction,
    string? Reason)
{
    public static ManualLineResult Ok(string input, string phone) =>
        new(input, ManualLineStatus.Ok, phone, null, null);

    public static ManualLineResult Corrected(string input, string phone, string correction) =>
        new(input, ManualLineStatus.Corrected, phone, correction, null);

    public static ManualLineResult Duplicate(string input, string phone) =>
        new(input, ManualLineStatus.Duplicate, phone, null, null);

    public static ManualLineResult Invalid(string input, string reason) =>
        new(input, ManualLineStatus.Invalid, null, null, reason);
}

public sealed record ManualImportResult(
    int Total,
    int Added,
    int Duplicated,
    int Corrected,
    IReadOnlyList<ManualLineResult> Lines)
{
    public int Invalid => Lines.Count(l => l.Status == ManualLineStatus.Invalid);
}
