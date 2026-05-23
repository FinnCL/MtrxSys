using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Messaging;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Messaging;

public sealed class SpintaxExpanderTests
{
    private readonly IRandomSource _rng = Substitute.For<IRandomSource>();

    private SpintaxExpander Build() => new(_rng);

    [Fact]
    public void Empty_input_returns_empty()
    {
        Build().Expand(string.Empty).Should().BeEmpty();
        Build().Expand(null!).Should().BeEmpty();
    }

    [Fact]
    public void Plain_text_passes_through()
    {
        Build().Expand("hello world").Should().Be("hello world");
    }

    [Fact]
    public void Picks_first_option_when_rng_returns_zero()
    {
        _rng.NextInt(0, 3).Returns(0);
        Build().Expand("{a|b|c}").Should().Be("a");
    }

    [Fact]
    public void Picks_last_option_when_rng_returns_last_index()
    {
        _rng.NextInt(0, 3).Returns(2);
        Build().Expand("{a|b|c}").Should().Be("c");
    }

    [Fact]
    public void Handles_nested_spintax()
    {
        _rng.NextInt(0, 2).Returns(0, 1);
        Build().Expand("{Hi {there|friend}|Hello}").Should().Be("Hi friend");
    }

    [Fact]
    public void Concatenates_multiple_groups()
    {
        _rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(0);
        Build().Expand("{a|b}{c|d} {e|f}").Should().Be("ac e");
    }

    [Fact]
    public void Escape_brace_treated_as_literal()
    {
        Build().Expand(@"literal \{not spintax\}").Should().Be("literal {not spintax}");
    }

    [Fact]
    public void Escape_pipe_inside_group_not_split()
    {
        _rng.NextInt(0, 1).Returns(0);
        Build().Expand(@"{a\|b}").Should().Be("a|b");
    }

    [Fact]
    public void Unterminated_brace_treated_as_literal()
    {
        Build().Expand("hello {oops").Should().Be("hello {oops");
    }

    [Fact]
    public void Empty_group_picks_empty_string()
    {
        _rng.NextInt(0, 2).Returns(0);
        Build().Expand("a{|b}c").Should().Be("ac");
    }

    [Fact]
    public void Output_capped_at_max_size()
    {
        var huge = new string('x', 5000);
        var result = Build().Expand(huge);
        result.Length.Should().BeLessThanOrEqualTo(4096);
    }
}
