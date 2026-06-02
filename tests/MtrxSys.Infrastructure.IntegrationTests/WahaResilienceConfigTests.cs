using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MtrxSys.Infrastructure;

namespace MtrxSys.Infrastructure.IntegrationTests;

// Garante que a app SOBE: o AddStandardResilienceHandler do cliente WAHA é registrado com
// ValidateOnStart, então valores inválidos (TotalRequestTimeout <= AttemptTimeout, ou
// SamplingDuration < 2×AttemptTimeout) derrubariam API/Dispatcher no startup — algo que o build
// limpo NÃO pega. IStartupValidator.Validate() roda exatamente esse gate (WahaOptions, JwtOptions
// e as opções do resilience handler). Não toca em banco nem rede.
public sealed class WahaResilienceConfigTests
{
    [Fact]
    public void Startup_validation_passes_with_configured_waha_timeout()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=x;Username=x;Password=x",
                ["Waha:BaseUrl"] = "http://localhost:3000",
                ["Waha:TimeoutSeconds"] = "60",
                ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters-long-xyz",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(config);
        using var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IStartupValidator>();
        var act = validator.Validate;

        act.Should().NotThrow("API/Dispatcher precisam subir — a config do resilience handler do WAHA tem de ser válida");
    }
}
