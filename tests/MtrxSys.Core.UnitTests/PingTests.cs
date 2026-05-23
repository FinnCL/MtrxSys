using FluentAssertions;
using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.UnitTests;

public sealed class PingTests
{
    [Fact]
    public void Result_Success_carries_value()
    {
        var r = Result<int>.Success(42);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
    }

    [Fact]
    public void Result_Failure_carries_error()
    {
        var r = Result<int>.Failure("boom");

        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be("boom");
    }
}
