using System.Text.RegularExpressions;
using MtrxSys.Core.Domain.Common;
using MtrxSys.Core.Domain.Contacts;
using LibFormat = PhoneNumbers.PhoneNumberFormat;
using LibParseException = PhoneNumbers.NumberParseException;
using LibUtil = PhoneNumbers.PhoneNumberUtil;

namespace MtrxSys.Core.Validation;

/// <summary>
/// Valida e normaliza números brasileiros para E.164 usando a libphonenumber.
/// A lib mantém as regras de DDD válido (Anatel), fixo vs. celular e o 9º dígito,
/// então não duplicamos essas regras na mão. O regex aqui só faz faxina da entrada.
/// </summary>
public sealed partial class BrazilPhoneValidator
{
    private const string DefaultRegion = "BR";
    private static readonly LibUtil Util = LibUtil.GetInstance();

    /// <summary>
    /// Validação completa para entrada não confiável (cadastro manual, CSV):
    /// limpa, valida e normaliza o número, ou falha com o motivo.
    /// </summary>
    public Result<PhoneNumber> Validate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result<PhoneNumber>.Failure("Número vazio.");
        }

        var cleaned = CleanupRegex().Replace(raw, string.Empty);
        if (cleaned.Length == 0 || cleaned == "+")
        {
            return Result<PhoneNumber>.Failure("Número sem dígitos.");
        }

        PhoneNumbers.PhoneNumber parsed;
        try
        {
            parsed = Util.Parse(cleaned, DefaultRegion);
        }
        catch (LibParseException ex)
        {
            return Result<PhoneNumber>.Failure($"Número inválido: {ex.Message}");
        }

        if (!Util.IsValidNumber(parsed))
        {
            return Result<PhoneNumber>.Failure("Número não é válido para o Brasil.");
        }

        var e164 = Util.Format(parsed, LibFormat.E164);
        return Result<PhoneNumber>.Success(PhoneNumber.FromValidatedE164(e164));
    }

    /// <summary>
    /// Normalização não destrutiva para números vindos da WAHA (já são IDs de roteamento
    /// do WhatsApp). Se a lib considerar válido, devolve o E.164 normalizado; caso contrário
    /// mantém o número original — não descartamos um contato real só porque a lib não
    /// reconhece um número legado (ex.: 9º dígito ausente em DDDs antigos).
    /// </summary>
    public PhoneNumber NormalizeTrusted(string wahaE164)
    {
        var result = Validate(wahaE164);
        return result.IsSuccess && result.Value is not null
            ? result.Value
            : PhoneNumber.FromValidatedE164(wahaE164);
    }

    // Mantém apenas dígitos e um eventual '+' (a lib é tolerante a formatação,
    // mas tiramos parênteses, espaços e traços antes de entregar pra ela).
    [GeneratedRegex(@"[^\d+]")]
    private static partial Regex CleanupRegex();
}
