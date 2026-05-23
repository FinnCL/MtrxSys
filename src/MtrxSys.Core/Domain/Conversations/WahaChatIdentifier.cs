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
        return string.IsNullOrEmpty(digits) ? null : "+" + digits;
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
