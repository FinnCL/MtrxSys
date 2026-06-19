using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class OptOutLinkSignerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    private static OptOutLinkSigner Build() =>
        new(Options.Create(new JwtOptions { SigningKey = "signing-key-com-pelo-menos-32-caracteres-aqui" }));

    [Fact]
    public void Sign_then_verify_returns_the_same_contact()
    {
        var signer = Build();
        var id = Guid.NewGuid();

        var token = signer.Sign(id, Now.AddDays(90));

        signer.TryVerify(token, Now, out var got).Should().BeTrue();
        got.Should().Be(id);
    }

    [Fact]
    public void Verify_fails_when_expired()
    {
        var signer = Build();
        var token = signer.Sign(Guid.NewGuid(), Now.AddMinutes(-1)); // já expirado

        signer.TryVerify(token, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void Verify_fails_when_tampered()
    {
        var signer = Build();
        var token = signer.Sign(Guid.NewGuid(), Now.AddDays(90));
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA"); // muda a assinatura

        signer.TryVerify(tampered, Now, out _).Should().BeFalse();
    }

    [Fact]
    public void Verify_fails_with_different_key()
    {
        var token = Build().Sign(Guid.NewGuid(), Now.AddDays(90));
        var other = new OptOutLinkSigner(
            Options.Create(new JwtOptions { SigningKey = "uma-chave-totalmente-diferente-com-32-chars+" }));

        other.TryVerify(token, Now, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b")]
    [InlineData("not-a-guid.123.sig")]
    public void Verify_fails_on_malformed(string token)
    {
        Build().TryVerify(token, Now, out _).Should().BeFalse();
    }
}
