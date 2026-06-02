using FluentAssertions;
using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Core.UnitTests.Campaigns;

public sealed class DispatchJobRetryTests
{
    private static DispatchJob NewJob() =>
        DispatchJob.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void New_job_starts_pending_with_zero_attempts()
    {
        var job = NewJob();
        job.Status.Should().Be(DispatchStatus.Pending);
        job.AttemptCount.Should().Be(0);
    }

    [Fact]
    public void ScheduleRetry_moves_to_Retrying_increments_and_keeps_reason()
    {
        var job = NewJob();
        var at = DateTimeOffset.UnixEpoch.AddMinutes(5);

        job.ScheduleRetry(at, "timeout");

        job.Status.Should().Be(DispatchStatus.Retrying);
        job.AttemptCount.Should().Be(1);
        job.ScheduledAt.Should().Be(at); // foi pro fim da fila (ScheduledAt = agora)
        job.ErrorReason.Should().Be("timeout"); // motivo continua visível
        job.SentAt.Should().BeNull();          // ainda não saiu de fato
    }

    [Theory]
    // maxAttempts=2 (original + 1 reenvio): reenvia só na 1ª falha (AttemptCount==0).
    [InlineData(0, 2, true)]
    [InlineData(1, 2, false)]
    // maxAttempts=3: reenvia enquanto houver tentativa contando a que acabou de falhar.
    [InlineData(0, 3, true)]
    [InlineData(1, 3, true)]
    [InlineData(2, 3, false)]
    public void CanRetry_respects_total_attempt_cap(int alreadyScheduledRetries, int max, bool expected)
    {
        var job = NewJob();
        for (var i = 0; i < alreadyScheduledRetries; i++)
        {
            job.ScheduleRetry(DateTimeOffset.UnixEpoch, "x");
        }

        job.CanRetry(max).Should().Be(expected);
    }

    [Fact]
    public void Flow_with_max_two_attempts_retries_once_then_fails_terminally()
    {
        const int max = 2;
        var job = NewJob();

        // 1ª falha (transitória): pode reenviar → vira Retrying, volta pra fila.
        job.CanRetry(max).Should().BeTrue();
        job.ScheduleRetry(DateTimeOffset.UnixEpoch.AddMinutes(1), "timeout #1");
        job.Status.Should().Be(DispatchStatus.Retrying);

        // 2ª falha: esgotou → não pode mais reenviar → falha definitiva.
        job.CanRetry(max).Should().BeFalse();
        job.MarkFailed("timeout #2", DateTimeOffset.UnixEpoch.AddMinutes(2));
        job.Status.Should().Be(DispatchStatus.Failed);
        job.ErrorReason.Should().Be("timeout #2");
    }
}
