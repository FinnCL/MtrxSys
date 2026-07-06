using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Warmup;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Warmup;

public sealed class TemplateWarmupContentProviderTests
{
    private static TemplateWarmupContentProvider Build(IRandomSource rng) => new(rng);

    [Fact]
    public void BuildConversation_is_two_way_starting_with_opener()
    {
        var rng = Substitute.For<IRandomSource>();
        rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(0);
        rng.NextDouble().Returns(0.9); // >= 0.60 → sem 3º turno

        var convo = Build(rng).BuildConversation();

        convo.Should().HaveCount(2);
        convo[0].SenderSlot.Should().Be(0); // abre
        convo[1].SenderSlot.Should().Be(1); // responde
        convo.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Text));
    }

    [Fact]
    public void BuildConversation_can_extend_to_four_alternating_turns()
    {
        var rng = Substitute.For<IRandomSource>();
        rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(0);
        rng.NextDouble().Returns(0.1); // < 0.60 e < 0.40 → 4 turnos

        var convo = Build(rng).BuildConversation();

        convo.Should().HaveCount(4);
        convo.Select(t => t.SenderSlot).Should().Equal(0, 1, 0, 1); // mão dupla alternada
    }
}
