namespace MtrxSys.Cli.Infrastructure;

internal sealed class CancellationTokenProvider
{
    public CancellationTokenProvider(CancellationToken token) => Token = token;

    public CancellationToken Token { get; }
}
