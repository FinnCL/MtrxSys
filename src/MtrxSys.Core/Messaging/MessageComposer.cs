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

    // Cópia FIXA do bloco de opt-out anexado quando o link de 1 clique está ligado (prod). Mora no código
    // de propósito, como fonte única: é texto sensível a ban — o wording certo faz a pessoa responder
    // "SAIR" em vez de denunciar/bloquear. O DispatchOptions.OptOutFooter (configurável) é só o texto de
    // fallback pra quando o link está DESLIGADO (sem OptOut:PublicBaseUrl, ex.: localhost).
    private const string MergedOptOut = "Para não receber mais mensagens, responda *SAIR* ou toque aqui:";
    private const string LinkOnlyOptOut = "Para sair, toque aqui:";

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

    // Anexa o bloco de opt-out só na 1ª mensagem a cada contato (LastSentAt ainda null) e só
    // se o texto ainda não menciona "sair" (não duplica quando o operador já escreveu no template).
    // São DOIS caminhos pro mesmo destino: responder "SAIR" (detectado no webhook) e o link de 1 clique.
    // Quando os dois estão ativos, saem numa FRASE SÓ ligada por "ou" (em vez de dois blocos repetitivos).
    private string AppendOptOutIfFirstContact(string text, Contact contact)
    {
        // Rodapé/link só na 1ª mensagem a cada contato (LastSentAt ainda null).
        if (contact.LastSentAt is not null)
        {
            return text;
        }
        // "Oferecer o texto de SAIR?": só se o footer (fallback) está configurado E o template ainda não
        // menciona "sair" (não duplica quando o operador já escreveu SAIR — que é o caso recomendado na UI).
        var footer = dispatchOptions.Value.OptOutFooter;
        var textOptOut = !string.IsNullOrWhiteSpace(footer) && !OptOutKeywordRegex().IsMatch(text);
        // Link de 1 clique: só quando há URL pública (servidor). Em localhost (vazio) fica de fora.
        var url = BuildOptOutUrl(contact);

        var block = (url, textOptOut) switch
        {
            // Os dois caminhos ativos → uma frase só (cópia fixa), "responda SAIR ou toque aqui: <link>".
            (not null, true) => $"{MergedOptOut}\n{url}",
            // Só o link (footer vazio OU o template já menciona "sair") — não repete o "responda SAIR".
            (not null, false) => $"{LinkOnlyOptOut}\n{url}",
            // Sem link (localhost): só o rodapé de texto CONFIGURÁVEL (é onde o OptOutFooter tem efeito).
            (null, true) => footer,
            _ => null,
        };
        return block is null ? text : $"{text}\n\n{block}";
    }

    // "{baseUrl}/sair?t={token}" com token assinado por contato (validade 90d). A URL sai em LINHA
    // PRÓPRIA no bloco de opt-out (mais limpa e menos "suspeita" que colada no texto).
    // null quando OptOut:PublicBaseUrl está vazio (link desligado — estado localhost).
    private string? BuildOptOutUrl(Contact contact)
    {
        var baseUrl = optOutOptions.Value.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }
        var token = optOutSigner.Sign(contact.Id, DateTimeOffset.UtcNow.AddDays(90));
        return $"{baseUrl.TrimEnd('/')}/sair?t={token}";
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
