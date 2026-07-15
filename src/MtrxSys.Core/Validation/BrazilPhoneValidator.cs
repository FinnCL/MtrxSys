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
    /// É um número BRASILEIRO PLAUSÍVEL? Código do país 55 + comprimento possível pro BR. NÃO exige
    /// que seja válido pelas regras de HOJE — que é uma terceira pergunta, mais estrita.
    ///
    /// As três perguntas são diferentes, e confundi-las custou caro dos dois lados:
    ///
    /// • Usar <see cref="Validate"/> (validade completa) pra decidir "é brasileiro?" descarta o
    ///   celular BR no formato ANTIGO — 8 dígitos depois do DDD, sem o 9º. O WhatsApp guarda MUITOS
    ///   números assim, inclusive chips nossos. Um grupo inteiro de conhecidos importava ZERO
    ///   contatos, todos rotulados "não brasileiro": mentira, e apontando pra causa errada.
    ///
    /// • Só o código do país é permissivo DEMAIS: "+551100" tem 55 e viraria contato. Contato-lixo
    ///   vira disparo pra número inexistente = 463 = gatilho de ban. É o que os testes do Coletor
    ///   guardam, e foi eles que pegaram esse erro.
    ///
    /// IsPossibleNumber olha só o COMPRIMENTO contra o que existe no BR: aceita o legado de 10
    /// dígitos, recusa o lixo de 4. É exatamente o corte que se quer aqui.
    ///
    /// Número que o WhatsApp está roteando é real por definição; quem confirma que ele existe é o
    /// check-exists antes do envio, não esta função.
    /// </summary>
    public bool IsPlausibleBrazilian(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        var cleaned = CleanupRegex().Replace(raw, string.Empty);
        try
        {
            var parsed = Util.Parse(cleaned, DefaultRegion);
            return parsed.CountryCode == BrazilCountryCode && Util.IsPossibleNumber(parsed);
        }
        catch (LibParseException)
        {
            // Nem parsear dá: não dá pra afirmar nada a favor.
            return false;
        }
    }

    private const int BrazilCountryCode = 55;

    /// <summary>
    /// Normaliza entrada DIGITADA pelo operador, de gente que ele conhece (o círculo de aquecimento,
    /// os participantes de um grupo que ele vai criar). Devolve null se não der — o chamador vira 400.
    ///
    /// Deliberadamente NÃO usa <see cref="Validate"/>: aqui o operador digita gente conhecida, e a
    /// validação estrita rejeitaria um contato estrangeiro legítimo (e o celular BR legado, ver
    /// <see cref="IsPlausibleBrazilian"/>). Não confundir com número vindo do WAHA — pra aquilo é o
    /// <see cref="NormalizeTrusted"/>, que preserva a forma que o WhatsApp já usa.
    ///
    /// PARSEIA com a região BR em vez de só colar um "+" na frente dos dígitos. Isso não é
    /// preciosismo: colar o "+" cru transformava número em formato NACIONAL no país errado, em
    /// silêncio. "71 99107-2835" (11 dígitos, o jeito que sai de qualquer planilha) virava
    /// "+71991072835" — e +7 é RÚSSIA, com 11 dígitos batendo o comprimento de lá. Esse número ia
    /// direto pro `POST /groups` da WAHA como participante inexistente, que é o vetor do 463.
    /// Parseando com região BR, "71991072835" vira "+5571991072835" e "+351912345678" segue Portugal.
    ///
    /// IsPossibleNumber (comprimento plausível) no lugar do "8 a 15 dígitos" na mão: recusa "+000..."
    /// e afins sem precisar de regra própria.
    /// </summary>
    public static string? NormalizeTypedE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var cleaned = CleanupRegex().Replace(raw, string.Empty);
        try
        {
            var parsed = Util.Parse(cleaned, DefaultRegion);
            return Util.IsPossibleNumber(parsed) ? Util.Format(parsed, LibFormat.E164) : null;
        }
        catch (LibParseException)
        {
            return null;
        }
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
