using System.Text;
using FluentAssertions;
using MtrxSys.Core.Common;
using Xunit;

namespace MtrxSys.Core.UnitTests.Common;

public sealed class StringExtensionsTests
{
    [Fact]
    public void Returns_null_for_null() =>
        ((string?)null).TruncateWithEllipsis(10).Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("oi")]
    [InlineData("exato")] // length == max: sem corte
    public void Keeps_strings_within_limit(string s) =>
        s.TruncateWithEllipsis(5).Should().Be(s);

    [Fact]
    public void Truncates_long_string_with_ellipsis() =>
        "abcdefgh".TruncateWithEllipsis(5).Should().Be("abcd…");

    [Fact]
    public void Does_not_split_a_surrogate_pair_at_the_boundary()
    {
        // 🏆 ocupa os índices 3 (high) e 4 (low); o corte antigo (s[..4]) deixaria o high
        // surrogate solto — UTF-16 inválido que estoura ao gravar em UTF-8.
        var result = "aaa🏆bbbb".TruncateWithEllipsis(5);

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var act = () => strictUtf8.GetBytes(result!);

        act.Should().NotThrow();
        result.Should().Be("aaa…"); // recuou 1 char: descartou o emoji incompleto
    }
}
