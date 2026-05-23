using System.Security.Cryptography;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Randomness;

internal sealed class CryptoRandomSource : IRandomSource
{
    public int NextInt(int minInclusive, int maxExclusive) =>
        RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);

    public double NextDouble()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        var u = BitConverter.ToUInt64(buffer) >> 11;
        return u / (double)(1ul << 53);
    }
}
