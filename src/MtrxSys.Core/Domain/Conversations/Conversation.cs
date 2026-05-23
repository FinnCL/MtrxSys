using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Conversations;

public sealed class Conversation : Entity<Guid>
{
    public string WaChatId { get; private set; } = null!;
    public Guid? ContactId { get; private set; }
    public string? Title { get; private set; }
    public bool IsGroup { get; private set; }
    public DateTimeOffset? LastMessageAt { get; private set; }
    public string? LastMessagePreview { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Conversation() { }

    public static Conversation Create(
        Guid id,
        string waChatId,
        Guid? contactId,
        string? title,
        bool isGroup,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waChatId);
        return new Conversation
        {
            Id = id,
            WaChatId = waChatId,
            ContactId = contactId,
            Title = title,
            IsGroup = isGroup,
            CreatedAt = createdAt,
        };
    }

    public void TouchLastMessage(DateTimeOffset at, string? preview)
    {
        if (LastMessageAt is null || at > LastMessageAt)
        {
            LastMessageAt = at;
            LastMessagePreview = Truncate(preview, 280);
        }
    }

    public void Rename(string? title) => Title = title;

    public void LinkContact(Guid contactId) => ContactId = contactId;

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..(max - 1)] + "…";
}
