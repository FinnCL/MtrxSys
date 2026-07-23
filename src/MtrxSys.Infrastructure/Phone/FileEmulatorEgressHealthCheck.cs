using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Lê o flag de saúde do egresso escrito pelo watchdog do host (montado no container). Sem
/// caminho configurado, o gate fica DESLIGADO. Com caminho, FAIL-CLOSED: só "ok" libera; "leak",
/// vazio, ou erro de leitura viram Unhealthy — parar o disparo é melhor que arriscar vazar.</summary>
internal sealed class FileEmulatorEgressHealthCheck(IOptions<DispatchOptions> opts) : IEmulatorEgressHealthCheck
{
    public EmulatorEgressStatus Check()
    {
        var path = opts.Value.EmulatorEgressHealthPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return EmulatorEgressStatus.Disabled;
        }
        try
        {
            return string.Equals(File.ReadAllText(path).Trim(), "ok", StringComparison.Ordinal)
                ? EmulatorEgressStatus.Healthy
                : EmulatorEgressStatus.Unhealthy;
        }
#pragma warning disable CA1031 // fail-closed DE PROPÓSITO: QUALQUER erro de leitura = não envia
        catch (Exception)
        {
            // Flag ausente/ilegível (watchdog não subiu, mount errado, caminho malformado na config):
            // FAIL-CLOSED. Um flag que não dá pra ler é indistinguível de um proxy fora — na dúvida,
            // NÃO envia. O catch é AMPLO de propósito: um ArgumentException/NotSupportedException de um
            // caminho ruim não pode DERRUBAR o ciclo do dispatcher — tem que virar "não protegido".
            return EmulatorEgressStatus.Unhealthy;
        }
#pragma warning restore CA1031
    }
}
