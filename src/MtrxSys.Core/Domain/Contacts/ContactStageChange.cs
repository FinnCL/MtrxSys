using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Contacts;

public sealed class ContactStageChange : Entity<Guid>
{
    public Guid ContactId { get; private set; }
    public ContactStage? FromStage { get; private set; }
    public ContactStage ToStage { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }
    public Guid ChangedByUserId { get; private set; }

    private ContactStageChange() { }

    public static ContactStageChange Create(
        Guid id,
        Guid contactId,
        ContactStage? fromStage,
        ContactStage toStage,
        DateTimeOffset changedAt,
        Guid changedByUserId)
    {
        return new ContactStageChange
        {
            Id = id,
            ContactId = contactId,
            FromStage = fromStage,
            ToStage = toStage,
            ChangedAt = changedAt,
            ChangedByUserId = changedByUserId,
        };
    }
}
