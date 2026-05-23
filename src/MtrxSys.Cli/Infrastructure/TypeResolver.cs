using Spectre.Console.Cli;

namespace MtrxSys.Cli.Infrastructure;

internal sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);

    public void Dispose()
    {
        if (provider is IDisposable d)
        {
            d.Dispose();
        }
    }
}
