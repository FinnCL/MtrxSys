namespace MtrxSys.Core.Domain.Contacts;

public sealed class ContactTag
{
    public string Name { get; private set; } = null!;
    public string? Color { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private ContactTag() { }

    public static ContactTag Create(string name, string? color, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ContactTag
        {
            Name = name.Trim().ToLowerInvariant(),
            Color = color,
            CreatedAt = createdAt,
        };
    }

    public void Recolor(string? color) => Color = color;
}
