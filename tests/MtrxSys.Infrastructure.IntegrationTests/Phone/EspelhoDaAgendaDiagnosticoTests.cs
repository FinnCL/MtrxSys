using MtrxSys.Infrastructure.Phone;
using Xunit.Abstractions;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Pergunta ao APARELHO DE VERDADE se ele confirma um número, pelo mesmo código que o lote
/// usa. Existe para responder "o console está errado ou o espelho não chegou?" sem gastar um lote.
/// </summary>
/// <remarks>
/// 🔴 NÃO RODA NO CI, e é de propósito: precisa de um celular plugado e do WhatsApp registrado nele.
/// Sem as duas variáveis de ambiente ele passa sem fazer nada, então não quebra quem rodar a suíte.
///
/// <code>
/// set MTRX_DIAG_SERIAL=RQ8WB048RFW
/// set MTRX_DIAG_NUMEROS=5521977044796,5566999699026
/// dotnet test tests/MtrxSys.Infrastructure.IntegrationTests --filter EspelhoDaAgenda
/// </code>
///
/// <para>O que ele mede é exatamente o que o `segurar` consulta: <c>IsOnWhatsAppAsync</c>, que devolve
/// true ou null e NUNCA false. null não é veredito sobre o número: é "a agenda não sabe agora", e as
/// duas causas comuns são o contato não estar gravado e o sync do WhatsApp ainda não ter publicado o
/// espelho, que leva minutos. Este teste separa uma coisa da outra em segundos.</para>
/// </remarks>
public sealed class EspelhoDaAgendaDiagnosticoTests(ITestOutputHelper saida)
{
    [Fact]
    public async Task Conta_quantos_a_agenda_confirma_agora()
    {
        var serial = Environment.GetEnvironmentVariable("MTRX_DIAG_SERIAL");
        var numeros = Environment.GetEnvironmentVariable("MTRX_DIAG_NUMEROS");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(numeros))
        {
            saida.WriteLine("diagnóstico manual: defina MTRX_DIAG_SERIAL e MTRX_DIAG_NUMEROS.");
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("Phone__AdbPath")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe");

        var reader = new WhatsAppContactsReader(new DirectAdbRunner(serial, adbPath));
        var lista = numeros.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var confirmados = 0;
        foreach (var numero in lista)
        {
            // O console chama com os dígitos crus, sem o "+", e é assim que tem que ser testado: o
            // caminho que o lote percorre, e não uma variação mais gentil dele.
            var resposta = await reader.IsOnWhatsAppAsync(numero, CancellationToken.None);
            if (resposta is true)
            {
                confirmados++;
            }
            saida.WriteLine($"{numero} -> {(resposta is true ? "CONFIRMADO" : "nao sei (null)")}");
        }

        saida.WriteLine($"a agenda confirma {confirmados} de {lista.Length}");

        // Sem assert de valor: o resultado É o relatório. Zero confirmados não é falha do teste, é o
        // achado que interessa, e falhar aqui esconderia o linha a linha que explica o porquê.
        Assert.True(lista.Length > 0);
    }
}
