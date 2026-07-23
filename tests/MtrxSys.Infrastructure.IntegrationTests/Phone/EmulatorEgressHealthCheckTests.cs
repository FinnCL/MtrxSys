using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Prova as semânticas do gate de egresso — em especial o FAIL-CLOSED: qualquer coisa que não
/// seja o flag dizendo exatamente "ok" (inclusive flag AUSENTE) tem que virar Unhealthy, pra o disparo
/// parar em vez de arriscar vazar o IP do datacenter. Só arquivo temporário, sem Docker.</summary>
public sealed class EmulatorEgressHealthCheckTests
{
    private static FileEmulatorEgressHealthCheck Build(string path) =>
        new(Options.Create(new DispatchOptions { EmulatorEgressHealthPath = path }));

    [Fact]
    public void Caminho_vazio_desliga_o_gate()
    {
        Build("").Check().Should().Be(EmulatorEgressStatus.Disabled,
            "sem caminho configurado o gate não bloqueia nada (default dos 9 stacks sem emulador)");
    }

    [Fact]
    public void Flag_ok_libera()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "ok\n");
            Build(f).Check().Should().Be(EmulatorEgressStatus.Healthy);
        }
        finally { File.Delete(f); }
    }

    [Theory]
    [InlineData("leak")]
    [InlineData("")]
    [InlineData("OK")] // maiúsculo NÃO conta — só "ok" exato libera
    public void Qualquer_coisa_diferente_de_ok_bloqueia(string content)
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, content);
            Build(f).Check().Should().Be(EmulatorEgressStatus.Unhealthy,
                "só o flag dizendo exatamente 'ok' libera; o resto é fail-closed");
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Flag_ausente_e_FAIL_CLOSED()
    {
        // Caminho configurado mas o arquivo não existe (watchdog não subiu / mount errado): um flag que
        // não dá pra ler é indistinguível de proxy fora — na dúvida, NÃO envia.
        var missing = Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.health");
        Build(missing).Check().Should().Be(EmulatorEgressStatus.Unhealthy,
            "flag ausente vira Unhealthy (fail-closed), não Disabled nem Healthy");
    }
}
