using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Core.Domain.Funnel;

/// <summary>Monta o link click-to-chat oficial do WhatsApp (wa.me). Quando a pessoa clica, ELA abre a
/// conversa e te manda a 1ª mensagem — ou seja, o inbound que estabelece o consentimento/tctoken e
/// destrava o envio livre (sem 463). É o coração do funil: distribuir esses links (anúncio/site/e-mail)
/// pra o contato iniciar.</summary>
public static class WaMeLink
{
    /// <summary>`https://wa.me/{digits}?text={texto}`. `phoneE164` pode vir com "+"/sufixo — só os
    /// dígitos entram (o wa.me quer o número puro com DDI). Retorna null se não houver dígitos.
    /// `prefillText` opcional é URL-encodado.</summary>
    public static string? Build(string? phoneE164, string? prefillText = null)
    {
        var digits = WahaChatIdentifier.ExtractDigits(phoneE164 ?? string.Empty);
        if (string.IsNullOrEmpty(digits))
        {
            return null;
        }
        var url = "https://wa.me/" + digits;
        if (!string.IsNullOrWhiteSpace(prefillText))
        {
            url += "?text=" + Uri.EscapeDataString(prefillText);
        }
        return url;
    }
}
