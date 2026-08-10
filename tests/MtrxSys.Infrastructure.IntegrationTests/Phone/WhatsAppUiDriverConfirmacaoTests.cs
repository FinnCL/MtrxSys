using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Prova o TERCEIRO estado do envio: "não consegui confirmar", que não é nem sucesso nem falha.</summary>
/// <remarks>
/// <para>Estes casos existem porque a distinção decide um efeito irreversível. Quem chama reage a uma
/// falha reabrindo a conversa na outra forma do número; fazer isso num envio que talvez tenha saído
/// entrega a campanha duas vezes para a mesma pessoa. E o oposto também custa caro: tratar tela
/// ilegível como confirmação tira o contato da lista e grava "enviado" para quem não recebeu nada.</para>
/// <para>Nenhum dos dois dá pra provocar no aparelho sob demanda: dependem do `uiautomator dump` falhar
/// na hora exata. Um adb falso coloca a falha no lugar e deixa afirmar o que o driver conclui.</para>
/// </remarks>
public sealed class WhatsAppUiDriverConfirmacaoTests
{
    private const string Numero = "+5584998420730";
    private const string Texto = "oi";

    /// <summary>Tela com o botão de enviar presente (campo cheio) e um nó de status.</summary>
    private const string TelaComBotaoEnviar =
        """<node resource-id="com.whatsapp:id/send" bounds="[900,1800][1000,1900]"/>""";

    private const string TelaSemBotaoEnviar =
        """<node resource-id="com.whatsapp:id/status" content-desc="Entregue" bounds="[10,10][20,20]"/>""";

    /// <summary>Adb falso: responde ao dump com a sequência de telas passada, uma por leitura.</summary>
    /// <remarks>
    /// `null` na sequência representa DUMP QUE FALHOU, que é o caso central destes testes. A última
    /// entrada se repete quando as leituras acabam, pra o poll poder insistir sem estourar a lista.
    /// </remarks>
    private sealed class AdbDeTela(string?[] telas) : IAdbRunner
    {
        private int _leitura;

        public List<string> Comandos { get; } = [];

        private string? TelaAtual => telas[Math.Min(_leitura, telas.Length - 1)];

        public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct)
        {
            Comandos.Add(command);

            if (command.Contains("uiautomator dump", StringComparison.Ordinal))
            {
                // `rm -f`, dump e `cat` chegam NUMA LINHA SÓ (ver DumpUiAsync), então UMA chamada
                // consome uma tela da sequência. Dump falho não deixa arquivo para o `cat` da mesma
                // linha, e a saída vazia é como o driver descobre que não deu pra ler.
                var tela = TelaAtual;
                _leitura++;
                return Task.FromResult(tela is null
                    ? (1, "", "ERROR: could not get idle state.")
                    : (0, $"<hierarchy>{tela}</hierarchy>", ""));
            }
            return Task.FromResult((0, "", ""));
        }

        public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
            Task.FromResult((0, "", ""));

        public bool SupportsRoot => false;

        public string Target => "falso";
    }

    // Deep link (sem digitação humana) é o caminho curto: abre, acha enviar, toca, confirma.
    private static PhoneOptions Opts => new()
    {
        HumanTyping = false,
        WhatsAppOpenWaitMs = 500,
        WhatsAppSendWaitMs = 500,
        WhatsAppSendTimeoutSeconds = 15,
    };

    private static async Task<WhatsAppSendResult> EnviarAsync(params string?[] telas)
    {
        using var driver = new WhatsAppUiDriver(new AdbDeTela(telas), Opts);
        return await driver.SendAsync(Numero, Texto, CancellationToken.None);
    }

    [Fact]
    public async Task Campo_esvaziou_e_envio_confirmado()
    {
        // 1ª leitura: pré-condição de tela limpa. 2ª: acha o botão de enviar. 3ª: campo esvaziou.
        var r = await EnviarAsync(TelaSemBotaoEnviar, TelaComBotaoEnviar, TelaSemBotaoEnviar);

        r.Sent.Should().BeTrue();
        r.Uncertain.Should().BeFalse();
    }

    [Fact]
    public async Task Tela_ilegivel_depois_do_toque_NAO_e_confirmacao()
    {
        // 🔴 O caso que motivou o terceiro estado. O primeiro poll de confirmação roda logo depois do
        // toque, com a tela animando, que é quando o dump mais falha. Antes, dump falho devolvia "sem
        // botão de enviar" e isso significava campo vazio, ou seja, ENVIO CONFIRMADO.
        var r = await EnviarAsync(TelaSemBotaoEnviar, TelaComBotaoEnviar, null);

        r.Sent.Should().BeFalse("não dá pra afirmar entrega sem ter conseguido olhar a tela");
        r.Uncertain.Should().BeTrue("pode ter saído, então quem chama NÃO pode reenviar por conta própria");
    }

    [Fact]
    public async Task Texto_continua_no_campo_e_falha_CONCLUSIVA()
    {
        // Tela LIDA e o texto continua lá: aqui dá pra afirmar que não saiu, e reenviar é seguro.
        var r = await EnviarAsync(TelaSemBotaoEnviar, TelaComBotaoEnviar, TelaComBotaoEnviar);

        r.Sent.Should().BeFalse();
        r.Uncertain.Should().BeFalse("o texto visível no campo é prova de que a mensagem não saiu");
    }

    [Fact]
    public async Task Chat_que_nao_abre_e_falha_CONCLUSIVA()
    {
        // Nunca apareceu botão de enviar: o toque não aconteceu, então nada saiu. É o caso para o qual a
        // segunda chance com a outra forma do número existe.
        var r = await EnviarAsync(TelaSemBotaoEnviar);

        r.Sent.Should().BeFalse();
        r.Uncertain.Should().BeFalse();
    }

    [Fact]
    public async Task Dump_apaga_o_arquivo_antes_de_escrever()
    {
        // Sem isto, um `uiautomator dump` que falha sem sobrescrever deixa o `cat` devolver a leitura
        // ANTERIOR, e a tela de antes passa por tela de agora — justamente sobre o que se decide onde
        // tocar e se a mensagem saiu.
        var adb = new AdbDeTela([TelaSemBotaoEnviar, TelaComBotaoEnviar, TelaSemBotaoEnviar]);
        using var driver = new WhatsAppUiDriver(adb, Opts);
        await driver.SendAsync(Numero, Texto, CancellationToken.None);

        adb.Comandos.Should().Contain(c => c.Contains("uiautomator dump", StringComparison.Ordinal));
        adb.Comandos.Where(c => c.Contains("uiautomator dump", StringComparison.Ordinal))
            .Should().OnlyContain(c => c.Contains("rm -f", StringComparison.Ordinal));
    }
}
