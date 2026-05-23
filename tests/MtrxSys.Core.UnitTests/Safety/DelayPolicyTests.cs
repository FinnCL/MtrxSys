using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class DelayPolicyTests
{
    [Fact]
    public void NextDelay_returns_value_from_rng_within_configured_range()
    {
        var rng = Substitute.For<IRandomSource>();
        rng.NextInt(45, 76).Returns(60);
        var opts = Options.Create(new DispatchOptions { DelayMinSeconds = 45, DelayMaxSeconds = 75 });
        var svc = new DelayPolicy(rng, opts);

        svc.NextDelay().Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void NextDelay_passes_correct_bounds_to_rng()
    {
        var rng = Substitute.For<IRandomSource>();
        rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(10);
        var opts = Options.Create(new DispatchOptions { DelayMinSeconds = 10, DelayMaxSeconds = 20 });
        var svc = new DelayPolicy(rng, opts);

        svc.NextDelay();

        rng.Received(1).NextInt(10, 21);
    }
}
