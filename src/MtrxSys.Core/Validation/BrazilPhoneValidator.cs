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

        // 🔴 SEM "+", NÃO DÁ PRA PERGUNTAR À LIBPHONENUMBER. Ela parseia com a região padrão (BR), então
        // um número sem código de país volta com CountryCode 55 POR CONSTRUÇÃO, nunca por evidência —
        // e o IsPossibleNumber só olha comprimento, que 11 dígitos satisfaz.
        //
        // MEDIDO em produção 2026-07-27: `37368544314` (Moldávia, prefixo 373) passou nos dois checks,
        // virou contato ativo, ganhou entrada na agenda Google e foi enfileirado. Só não recebeu porque
        // alguém olhou a lista à mão. O MESMO número com "+" era corretamente recusado.
        //
        // O emulador entrega os participantes de grupo SEM "+" (é o `user` do jid), então este é o
        // caminho de entrada real, não um caso de laboratório.
        //
        // Por isso a forma nacional é conferida por REGRA EXPLÍCITA do plano de numeração brasileiro,
        // em vez de delegada. Não dá pra usar IsValidNumber no lugar: ela recusa o celular BR legado
        // (8 dígitos após o DDD), que aqui é o caso NORMAL e cuja rejeição já custou um grupo inteiro
        // importando zero contatos.
        var digits = new string([.. cleaned.Where(char.IsDigit)]);
        var national = cleaned.StartsWith('+') || digits.Length is 12 or 13
            ? (digits.StartsWith("55", StringComparison.Ordinal) ? digits[2..] : null)
            : digits;
        if (national is null || !IsBrazilianNationalNumber(national))
        {
            return false;
        }

        try
        {
            var parsed = Util.Parse("+55" + national, DefaultRegion);
            return parsed.CountryCode == BrazilCountryCode && Util.IsPossibleNumber(parsed);
        }
        catch (LibParseException)
        {
            // Nem parsear dá: não dá pra afirmar nada a favor.
            return false;
        }
    }

    /// <summary>Forma NACIONAL brasileira: DDD real + assinante com o comprimento certo.</summary>
    /// <remarks>
    /// Dez dígitos = legado (DDD + 8), que é o caso comum na base fria e que a validação estrita
    /// rejeitaria. Onze = moderno (DDD + 9), e aí o assinante PRECISA começar com 9: é o que separa um
    /// celular BR de um número estrangeiro que só por acaso tem onze dígitos.
    /// </remarks>
    private static bool IsBrazilianNationalNumber(string national) =>
        national.Length is 10 or 11
        && national.All(char.IsAsciiDigit)
        && ValidAreaCodes.Contains(national[..2])
        && (national.Length == 10 || national[2] == '9');

    // DDDs que existem no Brasil. Lista fechada de propósito: "dois dígitos quaisquer" aceitaria 37 pra
    // um número moldavo (foi o caso real) e qualquer outro par que a numeração não usa.
    private static readonly System.Collections.Frozen.FrozenSet<string> ValidAreaCodes =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "11", "12", "13", "14", "15", "16", "17", "18", "19",
            "21", "22", "24", "27", "28",
            "31", "32", "33", "34", "35", "37", "38",
            "41", "42", "43", "44", "45", "46", "47", "48", "49",
            "51", "53", "54", "55",
            "61", "62", "63", "64", "65", "66", "67", "68", "69",
            "71", "73", "74", "75", "77", "79",
            "81", "82", "83", "84", "85", "86", "87", "88", "89",
            "91", "92", "93", "94", "95", "96", "97", "98", "99",
        ], StringComparer.Ordinal);

    private const int BrazilCountryCode = 55;

    /// <summary>Que FORMA este número tem, pelo plano de numeração brasileiro.</summary>
    /// <remarks>
    /// Serve pra CONFERIR uma lista antes de disparar, e mora aqui de propósito: quem responde tem que
    /// ser o mesmo código que o envio usa. Uma classificação feita à parte (planilha, script) diverge
    /// da real sem avisar, e aí o relatório tranquiliza sobre um comportamento que não é o que vai
    /// acontecer.
    /// </remarks>
    public enum BrazilNumberShape
    {
        /// <summary>Nem chega a ser um brasileiro plausível: código do país, DDD ou comprimento fora.</summary>
        NaoBrasileiro,

        /// <summary>11 dígitos nacionais começando com 9: o celular de hoje.</summary>
        CelularModerno,

        /// <summary>10 dígitos nacionais com assinante em 6-9: celular no formato ANTERIOR à migração
        /// do 9º dígito. Continua existindo, e muitas contas do WhatsApp seguem registradas assim.</summary>
        CelularLegado,

        /// <summary>10 dígitos nacionais com assinante em 2-5. Ou é telefone FIXO, ou é um celular a que
        /// falta o 9º dígito. Os dois casos são indistinguíveis olhando só o número, e nos dois o envio
        /// tende a falhar — por isso vale conferir na origem antes de disparar.</summary>
        FixoOuSemONono,
    }

    /// <summary>A forma deste número, pelas MESMAS regras que <see cref="IsPlausibleBrazilian"/> e
    /// <see cref="AlternateBrazilianForm"/> aplicam.</summary>
    public static BrazilNumberShape ShapeOf(string? raw)
    {
        var digits = new string([.. (raw ?? string.Empty).Where(char.IsAsciiDigit)]);
        if (!digits.StartsWith("55", StringComparison.Ordinal))
        {
            return BrazilNumberShape.NaoBrasileiro;
        }
        var nacional = digits[2..];
        if (nacional.Length is not (10 or 11) || !ValidAreaCodes.Contains(nacional[..2]))
        {
            return BrazilNumberShape.NaoBrasileiro;
        }
        if (nacional.Length == 11)
        {
            return nacional[2] == '9' ? BrazilNumberShape.CelularModerno : BrazilNumberShape.NaoBrasileiro;
        }
        return nacional[2] is >= '6' and <= '9'
            ? BrazilNumberShape.CelularLegado
            : BrazilNumberShape.FixoOuSemONono;
    }

    /// <summary>A OUTRA forma do mesmo celular brasileiro: com o 9º dígito se veio sem, sem ele se veio
    /// com. <c>null</c> quando não existe forma alternativa plausível.</summary>
    /// <remarks>
    /// 🔴 Existe porque o WhatsApp guarda a conta ora numa forma, ora na outra, conforme a época do
    /// registro, e abrir a conversa pela forma errada faz o app responder "não tem WhatsApp" — um
    /// contato BOM parece morto. Medido em 2026-08-05: num mesmo DDD 84, um número de 12 dígitos
    /// entregou e outro falhou.
    ///
    /// <para>NÃO serve pra normalizar na entrada. Converter tudo pra 13 dígitos quebraria justamente
    /// os que funcionam em 12 — não há como saber qual forma a conta usa sem tentar. O uso correto é
    /// como SEGUNDA tentativa, depois de a primeira falhar.</para>
    ///
    /// <para>O 9 só é inserido quando o assinante começa em 6-9, a faixa histórica de celular. Fixo
    /// começa em 2-5, e enfiar um 9 nele inventa um número que nunca existiu.</para>
    /// </remarks>
    public static string? AlternateBrazilianForm(string? phone)
    {
        var digits = new string([.. (phone ?? string.Empty).Where(char.IsAsciiDigit)]);
        if (!digits.StartsWith("55", StringComparison.Ordinal))
        {
            return null;
        }

        var nacional = digits[2..];
        if (nacional.Length < 10 || !ValidAreaCodes.Contains(nacional[..2]))
        {
            return null;
        }

        var outra = nacional.Length switch
        {
            // 🔴 CONFERE O QUE SOBRA AO TIRAR O 9, e não só que ele está lá. Tirar o 9 de
            // "11 9 2140-4487" deixa "11 2140-4487", cujo assinante começa com 2 — faixa de FIXO.
            // Números assim foram alocados DEPOIS da migração do 9º dígito e nunca tiveram forma de 8
            // dígitos: a "outra forma" deles não existe, e mandar pra ela é mandar pro telefone de
            // outra pessoa.
            //
            // A direção oposta já se protegia disso desde sempre (ver o caso de 10 logo abaixo). Esta
            // não, e a assimetria sobreviveu a várias leituras. Só ficou óbvia ao classificar uma lista
            // REAL em 2026-08-06: 8 de 15 celulares modernos eram "9 2xxx-xxxx" e produziam alternativa
            // na faixa de fixo.
            11 when nacional[2] == '9' && nacional[3] is >= '6' and <= '9'
                => "55" + nacional[..2] + nacional[3..],
            10 when nacional[2] is >= '6' and <= '9'
                => "55" + nacional[..2] + "9" + nacional[2..],
            _ => null,
        };

        // A alternativa passa pelo MESMO corte da entrada: gerar uma forma que o próprio sistema
        // recusaria seria só trocar um número morto por outro.
        return outra is not null && new BrazilPhoneValidator().IsPlausibleBrazilian("+" + outra)
            ? outra
            : null;
    }

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
