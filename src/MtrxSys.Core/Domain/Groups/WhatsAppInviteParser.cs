using System.Text.RegularExpressions;

namespace MtrxSys.Core.Domain.Groups;

/// <summary>
/// Extrai códigos de convite de grupo do WhatsApp (chat.whatsapp.com/&lt;code&gt;) de um texto/HTML
/// qualquer. Fonte única da regex — usado pelas fontes do Coletor (Telegram/SearXNG) e pela entrada
/// manual de links.
/// </summary>
public static class WhatsAppInviteParser
{
    // Código de convite: ~22 chars base62. {12,} evita casar o placeholder "______" que alguns
    // diretórios usam (underscore não está na classe de caracteres).
    private static readonly Regex InviteRx =
        new(@"chat\.whatsapp\.com/([0-9A-Za-z]{12,40})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Códigos únicos encontrados no texto, na ordem de aparição (sem o domínio).</summary>
    public static IReadOnlyList<string> ExtractCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var codes = new List<string>();
        foreach (Match m in InviteRx.Matches(text))
        {
            var code = m.Groups[1].Value;
            if (seen.Add(code))
            {
                codes.Add(code);
            }
        }
        return codes;
    }
}
