using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;

namespace MtrxSys.Core.Messaging;

public sealed partial class MessageComposer(
    SpintaxExpander spintax,
    IMessageTemplateRepository templates,
    IOptions<DispatchOptions> dispatchOptions)
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
        var footer = dispatchOptions.Value.OptOutFooter;
        if (contact.LastSentAt is not null || string.IsNullOrWhiteSpace(footer))
        {
            return text;
        }
        if (OptOutKeywordRegex().IsMatch(text))
        {
            return text;
        }
        return text + "\n\n" + footer;
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
