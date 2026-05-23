using System.Text.RegularExpressions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;

namespace MtrxSys.Core.Messaging;

public sealed partial class MessageComposer(
    SpintaxExpander spintax,
    IMessageTemplateRepository templates)
{
    [GeneratedRegex(@"\{\{\s*(?<key>[a-zA-Z][a-zA-Z0-9_]*)(\s*\|\s*(?<default>[^}]*))?\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    public async Task<string> ComposeFromTemplateIdAsync(Guid templateId, Contact contact, CancellationToken ct)
    {
        var template = await templates.GetByIdAsync(templateId, ct)
            ?? throw new InvalidOperationException($"Template {templateId} not found");
        return Compose(template, contact);
    }

    public string Compose(MessageTemplate template, Contact contact)
    {
        var expanded = spintax.Expand(template.ContentSpintax);
        return SubstitutePlaceholders(expanded, contact);
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
