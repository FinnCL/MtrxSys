using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Messages;

public sealed class MessageTemplate : Entity<Guid>
{
    public MessageSlot Slot { get; private set; }
    public string ContentSpintax { get; private set; } = string.Empty;
    public bool Active { get; private set; }

    // Imagem opcional anexada à mensagem. Quando presente, o disparo envia a imagem
    // com o texto composto como legenda (em vez de mensagem de texto puro). Guardada
    // como bytes no banco e mandada ao WAHA como base64 — auto-contido, sem servir
    // arquivo por URL (o container do WAHA não precisa alcançar a aplicação).
    public byte[]? ImageData { get; private set; }
    public string? ImageMimeType { get; private set; }

    public bool HasImage => ImageData is { Length: > 0 };

    private MessageTemplate() { }

    public static MessageTemplate Create(
        Guid id,
        MessageSlot slot,
        string contentSpintax,
        bool active = true,
        byte[]? imageData = null,
        string? imageMimeType = null)
    {
        return new MessageTemplate
        {
            Id = id,
            Slot = slot,
            ContentSpintax = contentSpintax,
            Active = active,
            ImageData = imageData is { Length: > 0 } ? imageData : null,
            ImageMimeType = imageData is { Length: > 0 } ? imageMimeType : null,
        };
    }

    public void Deactivate() => Active = false;
}
