using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MtrxSys.Core.Application.Warmup;
using MtrxSys.Core.Safety;

namespace MtrxSys.Infrastructure.IntegrationTests.Warmup;

/// <summary>
/// Prova que o container resolve o <see cref="HumanPhaseAutoSender"/> — ele tem 15 dependências e é
/// construído só quando o worker bate o primeiro tick, um minuto depois de subir. Uma que faltasse
/// compilaria, subiria, e só apareceria como worker morto em produção. O compilador não vê; isto vê.
/// </summary>
public sealed class HumanPhaseAutoSenderWiringTests
{
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=x;Username=x;Password=x",
                ["Jwt:SigningKey"] = "test-only-signing-key-with-32-chars-minimum",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void Auto_sender_resolves_with_every_dependency()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<HumanPhaseAutoSender>().Should().NotBeNull();
    }

    [Fact]
    public void Human_phase_gate_resolves_for_the_api_too()
    {
        // A Api serve o card com o MESMO gate que o Dispatcher usa pra travar — regra única, sem
        // cópia. Se ele não resolvesse aqui, o card daria 500 enquanto o disparo seguia travado.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<HumanPhaseGate>().Should().NotBeNull();
    }
}
