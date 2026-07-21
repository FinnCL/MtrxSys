using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class OptOutLinkSignerTests
{
    private const string Key = "signing-key-com-pelo-menos-32-caracteres-aqui";
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    private static OptOutLinkSigner Build() =>
        new(Options.Create(new JwtOptions { SigningKey = Key }));

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

    [Fact]
    public void Sign_produces_short_token()
    {
        var parts = Build().Sign(Guid.NewGuid(), Now.AddDays(90)).Split('.');

        parts.Should().HaveCount(3);
        parts[0].Length.Should().Be(22, "contactId em base64url de 16 bytes (era 32 hex)");
        parts[2].Length.Should().Be(22, "tag HMAC truncada a 128 bits (era 43)");
    }

    [Fact]
    public void Verify_accepts_legacy_long_format_token()
    {
        // Reproduz o formato ANTIGO (guid 32-hex + tag HMAC completa de 256 bits), como os links de
        // 90d já enviados antes da troca. O novo Verify TEM que continuar aceitando.
        var id = Guid.NewGuid();
        var payload = $"{id:N}.{Now.AddDays(90).ToUnixTimeSeconds()}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Key));
        var full = hmac.ComputeHash(Encoding.UTF8.GetBytes($"mtrxsys.optout.v1.{payload}"));
        var oldTag = Convert.ToBase64String(full).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var legacyToken = $"{payload}.{oldTag}";

        Build().TryVerify(legacyToken, Now, out var got).Should().BeTrue("link antigo de 90d ainda vale");
        got.Should().Be(id);
    }
}
