using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Contacts;

public sealed class Contact : Entity<Guid>
{
    public PhoneNumber Phone { get; private set; } = null!;
    public string? Name { get; private set; }
    public string? GroupTag { get; private set; }
    public string? Theme { get; private set; }
    public DateTimeOffset? OptInAt { get; private set; }
    public DateTimeOffset? OptOutAt { get; private set; }
    public DateTimeOffset? LastSentAt { get; private set; }
    public ContactStage Stage { get; private set; } = ContactStage.Lead;
    public DateTimeOffset? StageChangedAt { get; private set; }

    private Contact() { }

    public static Contact Create(Guid id, PhoneNumber phone, string? name, string? groupTag, string? theme, DateTimeOffset? optInAt)
    {
        return new Contact
        {
            Id = id,
            Phone = phone,
            Name = name,
            GroupTag = groupTag,
            Theme = theme,
            OptInAt = optInAt,
            Stage = ContactStage.Lead,
        };
    }

    public void RegisterSend(DateTimeOffset at) => LastSentAt = at;

    public void OptOut(DateTimeOffset at) => OptOutAt = at;

    public ContactStage? ChangeStage(ContactStage newStage, DateTimeOffset at)
    {
        if (Stage == newStage)
        {
            return null;
        }
        var previous = Stage;
        Stage = newStage;
        StageChangedAt = at;
        return previous;
    }
}
