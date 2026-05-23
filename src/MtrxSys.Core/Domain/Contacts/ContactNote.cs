using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Contacts;

public sealed class ContactNote : Entity<Guid>
{
    public Guid ContactId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    private ContactNote() { }

    public static ContactNote Create(Guid id, Guid contactId, string body, DateTimeOffset createdAt, Guid createdByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        return new ContactNote
        {
            Id = id,
            ContactId = contactId,
            Body = body,
            CreatedAt = createdAt,
            CreatedByUserId = createdByUserId,
        };
    }

    public void Edit(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        Body = body;
    }
}
