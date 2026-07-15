using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MtrxSys.Infrastructure;

namespace MtrxSys.Dispatcher.IntegrationTests;

/// <summary>
/// Prova que o container do Dispatcher RESOLVE o DispatchEngine — com todas as dependências, na
/// mesma fiação do Program.cs.
///
/// Existe porque a falha que ele pega é silenciosa no build e cara em produção: o DispatchEngine é
/// Scoped e é construído só quando o worker roda o 1º ciclo. Uma dependência não registrada compila,
/// sobe, e só estoura minutos depois — nos 10 stacks ao mesmo tempo, em crash-loop. O compilador não
/// vê isso; este teste vê, em milissegundos.
/// </summary>
public sealed class DispatchEngineWiringTests
{
    // Espelha o Program.cs. Se ele mudar, este setup tem que mudar junto — é o ponto.
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=x;Username=x;Password=x",
                // ValidateOnStart do JwtOptions: o dispatcher não serve HTTP, mas herda a validação
                // via AddInfrastructure. Sem chave de 32+ chars ele nem construiria o container.
                ["Jwt:SigningKey"] = "test-only-signing-key-with-32-chars-minimum",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(config);
        services.AddSingleton<DispatchSettleTracker>();
        services.AddSingleton<HumanPhaseTracker>();
        services.AddScoped<DispatchEngine>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Pega o erro de LIFETIME também: um Singleton que dependesse de algo Scoped (o caso do
            // HumanPhaseTracker, se alguém o trocasse por Scoped por engano) explode aqui.
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void Dispatch_engine_resolves_with_every_dependency()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // Não abre conexão com banco: só constrói o objeto e suas dependências.
        var engine = scope.ServiceProvider.GetRequiredService<DispatchEngine>();

        engine.Should().NotBeNull();
    }

    [Fact]
    public void Human_phase_tracker_is_shared_between_cycles()
    {
        // O latch só serve se SOBREVIVER ao ciclo — o engine é Scoped (novo a cada ciclo), o tracker
        // não pode ser. Se virasse Scoped, a fase seria recomputada pra sempre, em silêncio: sem
        // erro, só um group-by sobre chat_messages a cada job, eternamente.
        using var provider = BuildProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<HumanPhaseTracker>();
        var b = scope2.ServiceProvider.GetRequiredService<HumanPhaseTracker>();

        b.Should().BeSameAs(a);
    }

    [Fact]
    public void Human_phase_latch_is_keyed_by_anchor_so_a_new_chip_redoes_the_phase()
    {
        var tracker = new HumanPhaseTracker();
        var anchor = new DateOnly(2026, 7, 20);

        tracker.IsClosedFor(anchor).Should().BeFalse();
        tracker.MarkClosedFor(anchor);
        tracker.IsClosedFor(anchor).Should().BeTrue();

        // Chip novo re-ancora (RestartWarmup) → o latch cai sozinho e a fase é refeita.
        tracker.IsClosedFor(anchor.AddDays(5)).Should().BeFalse();
    }
}
