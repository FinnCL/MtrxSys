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

    /// <summary>Conta de sistema/notificações do WhatsApp (0@c.us, dígitos todos zero) e canal de
    /// status (status@broadcast) — avisos como "WhatsApp Business", nunca conversa de contato.</summary>
    public static bool IsSystem(string? chatId)
    {
        if (string.IsNullOrEmpty(chatId))
        {
            return false;
        }
        if (chatId.Equals("status@broadcast", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var digits = ExtractDigits(chatId);
        return digits.Length > 0 && digits.All(c => c == '0');
    }

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

    /// <summary>Chave de de-dupe estável de uma mensagem NOSSA (outbound): o "core" do id
    /// (<see cref="ExtractMessageCore"/>, o MESMO que o webhook grava pro eco fromMe), ou um id gerado
    /// com o prefixo dado quando o WAHA não devolve id. Centraliza a convenção usada pelo disparo, pelo
    /// aquecimento e pela conversa manual — os três precisam do mesmo core pra o eco não duplicar.</summary>
    public static string ResolveOutboundCoreId(string? waMessageId, string fallbackPrefix)
    {
        var core = ExtractMessageCore(waMessageId);
        return string.IsNullOrEmpty(core) ? $"{fallbackPrefix}_{Guid.NewGuid():N}" : core;
    }

    /// <summary>Telefone E.164 do autor de uma mensagem, conforme o tipo de chat: em grupo, do
    /// participante; em individual recebido, do próprio chat (do nosso lado, no envio, não há autor
    /// externo → null). Regra única usada igual pelo webhook (tempo real) e pelo sync (histórico).</summary>
    public static string? ResolveAuthorPhone(Kind kind, string chatId, MessageDirection direction, string? participant)
    {
        if (kind == Kind.Group)
        {
            return string.IsNullOrEmpty(participant)
                ? null
                : TryExtractPhoneE164(participant) ?? "+" + ExtractDigits(participant);
        }
        if (direction == MessageDirection.Inbound && kind == Kind.Individual)
        {
            return TryExtractPhoneE164(chatId);
        }
        return null;
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
