using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.Messaging;

public sealed partial class MessageComposer(
    SpintaxExpander spintax,
    IMessageTemplateRepository templates,
    IOptions<DispatchOptions> dispatchOptions,
    IOptions<OptOutOptions> optOutOptions,
    OptOutLinkSigner optOutSigner)
{
    [GeneratedRegex(@"\{\{\s*(?<key>[a-zA-Z][a-zA-Z0-9_]*)(\s*\|\s*(?<default>[^}]*))?\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    // "O texto já oferece opt-out?" por palavra inteira — evita falso positivo em "sairemos" etc.
    [GeneratedRegex(@"\bsair\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OptOutKeywordRegex();

    public async Task<string> ComposeFromTemplateIdAsync(Guid templateId, Contact contact, CancellationToken ct)
    {
        var template = await templates.GetByIdAsync(templateId, ct)
            ?? throw new InvalidOperationException($"Template {templateId} not found");
        return Compose(template, contact);
    }

    public string Compose(MessageTemplate template, Contact contact)
    {
        var expanded = spintax.Expand(template.ContentSpintax);
        var text = SubstitutePlaceholders(expanded, contact);
        return AppendOptOutIfFirstContact(text, contact);
    }

    // Anexa o rodapé de opt-out só na 1ª mensagem a cada contato (LastSentAt ainda null) e só
    // se o texto ainda não menciona "sair" (não duplica quando o operador já escreveu no template).
    private string AppendOptOutIfFirstContact(string text, Contact contact)
    {
        // Rodapé/link só na 1ª mensagem a cada contato (LastSentAt ainda null).
        if (contact.LastSentAt is not null)
        {
            return text;
        }
        // Rodapé de texto ("responda SAIR"): só se configurado e o template ainda não menciona "sair".
        var footer = dispatchOptions.Value.OptOutFooter;
        var appendFooter = !string.IsNullOrWhiteSpace(footer) && !OptOutKeywordRegex().IsMatch(text);
        // Link de 1 clique: só quando há URL pública (servidor). Em localhost (vazio) fica de fora.
        var link = BuildOptOutLink(contact);

        if (!appendFooter && link is null)
        {
            return text;
        }
        var sb = new StringBuilder(text);
        if (appendFooter)
        {
            sb.Append("\n\n").Append(footer);
        }
        if (link is not null)
        {
            sb.Append("\n\n").Append(link);
        }
        return sb.ToString();
    }

    // "Para sair, toque aqui: {baseUrl}/sair?t={token}" com token assinado por contato (validade 90d).
    // null quando OptOut:PublicBaseUrl está vazio (link desligado — estado localhost).
    private string? BuildOptOutLink(Contact contact)
    {
        var baseUrl = optOutOptions.Value.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }
        var token = optOutSigner.Sign(contact.Id, DateTimeOffset.UtcNow.AddDays(90));
        return $"Para sair, toque aqui: {baseUrl.TrimEnd('/')}/sair?t={token}";
    }

    private static string SubstitutePlaceholders(string text, Contact contact)
    {
        return PlaceholderRegex().Replace(text, m =>
        {
            var key = m.Groups["key"].Value.ToLowerInvariant();
            var fallback = m.Groups["default"].Success ? m.Groups["default"].Value.Trim() : string.Empty;
            return ResolvePlaceholder(key, contact) ?? fallback;
        });
    }

    private static string? ResolvePlaceholder(string key, Contact contact) => key switch
    {
        "name" or "nome" => string.IsNullOrWhiteSpace(contact.Name) ? null : contact.Name,
        "phone" or "telefone" => contact.Phone.E164,
        "group" or "grupo" => contact.GroupTag,
        "theme" or "tema" => contact.Theme,
        _ => null,
    };
}
