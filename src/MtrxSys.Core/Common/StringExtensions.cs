using System.Diagnostics.CodeAnalysis;

namespace MtrxSys.Core.Common;

public static class StringExtensions
{
    /// <summary>
    /// Trunca para no máximo <paramref name="max"/> caracteres, com reticência ("…") no fim
    /// quando corta. Não corta no meio de um par surrogate (emoji): manter só o high surrogate
    /// deixaria um caractere UTF-16 inválido, que estoura ao gravar em UTF-8 no Postgres
    /// (EncoderFallbackException "Unable to translate Unicode character").
    /// </summary>
    [return: NotNullIfNotNull(nameof(s))]
    public static string? TruncateWithEllipsis(this string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
        {
            return s;
        }
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1]))
        {
            cut--;
        }
        return s[..cut] + "…";
    }
}
