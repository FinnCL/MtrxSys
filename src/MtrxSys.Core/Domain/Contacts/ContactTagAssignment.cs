namespace MtrxSys.Core.Domain.Contacts;

public sealed class ContactTagAssignment
{
    public Guid ContactId { get; private set; }
    public string TagName { get; private set; } = null!;
    public DateTimeOffset AssignedAt { get; private set; }

    private ContactTagAssignment() { }

    public static ContactTagAssignment Create(Guid contactId, string tagName, DateTimeOffset assignedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        return new ContactTagAssignment
        {
            ContactId = contactId,
            TagName = tagName.Trim().ToLowerInvariant(),
            AssignedAt = assignedAt,
        };
    }
}
