namespace MtrxSys.Core.Application.Abstractions;

public interface IRandomSource
{
    int NextInt(int minInclusive, int maxExclusive);
    double NextDouble();
}
