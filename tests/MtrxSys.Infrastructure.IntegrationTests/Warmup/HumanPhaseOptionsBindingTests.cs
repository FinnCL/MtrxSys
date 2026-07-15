using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.IntegrationTests.Warmup;

/// <summary>
/// Binding do HumanPhaseOptions pelo caminho REAL (AddInfrastructure + configuração), porque um
/// erro aqui não é um teste vermelho: é crash-loop no startup dos 10 stacks ao mesmo tempo.
///
/// O caso que importa é a STRING VAZIA. O compose declara as variáveis como `${VAR:-}` (default
/// vazio), então um stack que não configurar a data manda "" — e "" precisa virar null (recurso
/// desligado), não exceção. É o que separa "nada muda" de "a produção inteira cai".
/// </summary>
public sealed class HumanPhaseOptionsBindingTests
{
    private static HumanPhaseOptions Bind(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddOptions<HumanPhaseOptions>().Bind(config.GetSection(HumanPhaseOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<IOptions<HumanPhaseOptions>>().Value;
    }

    [Fact]
    public void Empty_effective_from_binds_to_null_and_does_not_throw()
    {
        // `HumanPhase__EffectiveFrom=` (vazio) = o default do compose num stack não configurado.
        var opts = Bind(("HumanPhase:EffectiveFrom", ""));

        opts.EffectiveFrom.Should().BeNull();
    }

    [Fact]
    public void Absent_section_leaves_the_feature_off_with_safe_defaults()
    {
        var opts = Bind();

        opts.EffectiveFrom.Should().BeNull();  // desligado = produção intacta
        opts.MinDays.Should().Be(3);
        opts.MinPeople.Should().Be(5);
        opts.MinInbound.Should().Be(3);
        opts.MinOutbound.Should().Be(3);
    }

    [Fact]
    public void Date_binds_from_configuration()
    {
        var opts = Bind(("HumanPhase:EffectiveFrom", "2026-07-20"), ("HumanPhase:MinPeople", "4"));

        opts.EffectiveFrom.Should().Be(new DateOnly(2026, 7, 20));
        opts.MinPeople.Should().Be(4);
    }
}
