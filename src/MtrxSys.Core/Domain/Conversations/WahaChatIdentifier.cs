namespace MtrxSys.Core.Domain.Conversations;

public static class WahaChatIdentifier
{
    public const string IndividualSuffix = "@c.us";
    public const string GroupSuffix = "@g.us";
    public const string LinkedIdSuffix = "@lid";
    public const string LegacyIndividualSuffix = "@s.whatsapp.net";

    public enum Kind
    {
        Unknown = 0,
        Individual = 1,
        Group = 2,
        LinkedId = 3,
    }

    public static Kind Classify(string? chatId)
    {
        if (string.IsNullOrEmpty(chatId))
        {
            return Kind.Unknown;
        }
        if (chatId.EndsWith(GroupSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return Kind.Group;
        }
        if (chatId.EndsWith(LinkedIdSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return Kind.LinkedId;
        }
        if (chatId.EndsWith(IndividualSuffix, StringComparison.OrdinalIgnoreCase) ||
            chatId.EndsWith(LegacyIndividualSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return Kind.Individual;
        }
        return Kind.Unknown;
    }

    public static bool IsGroup(string? chatId) => Classify(chatId) == Kind.Group;

    public static bool HasRealPhone(string? chatId) => Classify(chatId) == Kind.Individual;

    public static string? TryExtractPhoneE164(string? chatId)
    {
        if (!HasRealPhone(chatId))
        {
            return null;
        }
        var digits = ExtractDigits(chatId!);
        // Rejeita pseudo-números sem dígito real (ex.: "0@c.us" → "+0"), que viram contato-lixo.
        if (string.IsNullOrEmpty(digits) || digits.All(c => c == '0'))
        {
            return null;
        }
        return "+" + digits;
    }

    // Extrai o "core" do id de mensagem do WhatsApp — o token logo após o segmento do chatId
    // (o que contém '@'). Formato serializado do WAHA:
    //   inbound:  "false_5571..@lid_3EB0AB12"            → core "3EB0AB12"
    //   outbound: "true_5571..@lid_3EB0AB12_out"         → core "3EB0AB12" (ignora o sufixo "_out")
    //   já-core:  "3EB0AB12"                              → "3EB0AB12"
    // Esse core é o mesmo no envio (@c.us) e no eco (@lid), servindo de chave estável de
    // de-dupe entre o disparo e o webhook. Pegar o último token erraria por causa do "_out".
    public static string ExtractMessageCore(string? waMessageId)
    {
        if (string.IsNullOrEmpty(waMessageId))
        {
            return string.Empty;
        }
        var parts = waMessageId.Split('_');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Contains('@', StringComparison.Ordinal))
            {
                return parts[i + 1];
            }
        }
        return waMessageId; // sem segmento serializado: já é o core (id retornado pelo envio).
    }

    public static string ExtractDigits(string chatIdOrParticipant)
    {
        if (string.IsNullOrEmpty(chatIdOrParticipant))
        {
            return string.Empty;
        }
        var at = chatIdOrParticipant.IndexOf('@', StringComparison.Ordinal);
        var raw = at > 0 ? chatIdOrParticipant[..at] : chatIdOrParticipant;
        return new string([.. raw.Where(char.IsDigit)]);
    }
}
