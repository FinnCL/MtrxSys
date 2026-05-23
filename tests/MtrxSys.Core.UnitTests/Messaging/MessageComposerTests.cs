using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;
using MtrxSys.Core.Messaging;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Messaging;

public sealed class MessageComposerTests
{
    private readonly IMessageTemplateRepository _templates = Substitute.For<IMessageTemplateRepository>();
    private readonly IRandomSource _rng = Substitute.For<IRandomSource>();

    private MessageComposer Build()
    {
        _rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(0);
        return new MessageComposer(new SpintaxExpander(_rng), _templates);
    }

    private static Contact BuildContact(string? name = "Maria", string? group = "Grupo VIP", string? theme = "Promo")
    {
        var phone = PhoneNumber.FromValidatedE164("+5511999998888");
        return Contact.Create(Guid.NewGuid(), phone, name, group, theme, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Compose_substitutes_name_placeholder()
    {
        var t = MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting, "Oi, {{name}}!");
        var c = BuildContact(name: "João");

        Build().Compose(t, c).Should().Be("Oi, João!");
    }

    [Fact]
    public void Compose_uses_fallback_when_name_is_null()
    {
        var t = MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting, "Oi, {{name|amigo}}!");
        var c = BuildContact(name: null);

        Build().Compose(t, c).Should().Be("Oi, amigo!");
    }

    [Fact]
    public void Compose_substitutes_portuguese_aliases()
    {
        var t = MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting,
            "Olá {{nome}}, do grupo {{grupo}}, tema {{tema}}, fone {{telefone}}");
        var c = BuildContact();

        Build().Compose(t, c).Should().Be("Olá Maria, do grupo Grupo VIP, tema Promo, fone +5511999998888");
    }

    [Fact]
    public void Compose_expands_spintax_then_substitutes()
    {
        _rng.NextInt(0, 2).Returns(0);
        var t = MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting,
            "{Oi|Olá}, {{name}}!");
        var c = BuildContact(name: "Ana");

        Build().Compose(t, c).Should().Be("Oi, Ana!");
    }

    [Fact]
    public void Compose_unknown_placeholder_uses_empty_string_without_fallback()
    {
        var t = MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting, "Hello {{xyz}} world");
        var c = BuildContact();

        Build().Compose(t, c).Should().Be("Hello  world");
    }
}
