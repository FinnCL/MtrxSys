using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Messages;

public sealed class MessageTemplate : Entity<Guid>
{
    public MessageSlot Slot { get; private set; }
    public string ContentSpintax { get; private set; } = string.Empty;
    public bool Active { get; private set; }

    private MessageTemplate() { }

    public static MessageTemplate Create(Guid id, MessageSlot slot, string contentSpintax, bool active = true)
    {
        return new MessageTemplate
        {
            Id = id,
            Slot = slot,
            ContentSpintax = contentSpintax,
            Active = active,
        };
    }

    public void Deactivate() => Active = false;
}
