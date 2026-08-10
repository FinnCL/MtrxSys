using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Prova que uma tela desconhecida bloqueando o envio deixa RASTRO, e que o rastro é colhido
/// antes de ela ser dispensada.</summary>
/// <remarks>
/// 🔴 O CASO: o disparo roda sozinho, de madrugada. Quando um aviso que o driver não conhece aparece na
/// frente da conversa, o único jeito de descobrir qual botão fecha ele era alguém estar presente pra
/// rodar o <c>uiautomator dump</c> naquele instante. O envio não espera: o lote segue, o próximo contato
/// abre outra conversa e a tela some. O defeito voltava toda semana e não era reproduzível.
/// <para>Estes testes fixam as três garantias que tornam o diagnóstico independente de haver alguém
/// olhando: os rótulos chegam na mensagem de erro (que já é gravada no CSV e no log), a tela inteira é
/// gravada em disco, e a captura acontece ANTES do BACK que apaga a evidência.</para>
/// </remarks>
public sealed class WhatsAppUiDriverTelaDesconhecidaTests : IDisposable
{
    private const string Numero = "+5584998420730";
    private const string Texto = "oi";

    /// <summary>Um aviso por cima da conversa com botão de rótulo que o driver NÃO conhece.</summary>
    /// <remarks>Sem <c>com.whatsapp:id/send</c> nem <c>id/entry</c>: enquanto o aviso está lá, o campo
    /// de mensagem não existe na árvore, que é exatamente o que fazia o envio falhar como "a conversa
    /// não abriu".</remarks>
    private const string TelaDoAviso =
        """
        <node text="Mensagens temporárias" clickable="false" bounds="[0,400][1080,500]"/>
        <node text="Prosseguir mesmo assim" clickable="true" bounds="[100,900][500,1000]"/>
        <node text="" content-desc="Saiba mais" clickable="true" bounds="[600,900][900,1000]"/>
        """;

    private readonly string _pasta =
        Path.Combine(Path.GetTempPath(), "mtrx-telas-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    /// <summary>Adb falso: todo dump devolve a mesma tela, que é como um modal se comporta.</summary>
    private sealed class AdbDeUmaTela(string tela) : IAdbRunner
    {
        public List<string> Comandos { get; } = [];

        public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct)
        {
            Comandos.Add(command);
            // `rm -f`, dump e `cat` chegam numa linha só (ver DumpUiAsync).
            if (command.Contains("uiautomator dump", StringComparison.Ordinal))
            {
                return Task.FromResult((0, $"<hierarchy>{tela}</hierarchy>", ""));
            }
            return Task.FromResult((0, "ok", ""));
        }

        public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
            Task.FromResult((0, "", ""));

        public bool SupportsRoot => false;

        public string Target => "falso";
    }

    private PhoneOptions Opts => new()
    {
        HumanTyping = false,
        WhatsAppOpenWaitMs = 500,
        WhatsAppSendWaitMs = 500,
        WhatsAppSendTimeoutSeconds = 15,
        UiDumpDir = _pasta,
    };

    private async Task<(WhatsAppSendResult Resultado, AdbDeUmaTela Adb)> EnviarAsync()
    {
        var adb = new AdbDeUmaTela(TelaDoAviso);
        using var driver = new WhatsAppUiDriver(adb, Opts);
        return (await driver.SendAsync(Numero, Texto, CancellationToken.None), adb);
    }

    [Fact]
    public async Task Rotulos_do_aviso_desconhecido_vao_na_mensagem_de_erro()
    {
        // Na mensagem de erro, e não só num arquivo: o erro já cai no CSV do console e no log do
        // dispatcher. É o caminho que chega a quem lê o resultado do lote de manhã, sem configurar nada.
        var (r, _) = await EnviarAsync();

        r.Sent.Should().BeFalse();
        r.Error.Should().Contain("Prosseguir mesmo assim", "é o rótulo candidato a fechar o aviso");
        r.Error.Should().Contain("Saiba mais", "botão sem texto ainda tem content-desc");
        r.Error.Should().NotContain("Mensagens temporárias", "o título não é clicável, não é candidato");
    }

    [Fact]
    public async Task Tela_inteira_fica_gravada_em_disco()
    {
        var (r, _) = await EnviarAsync();

        var arquivos = Directory.GetFiles(_pasta, "tela-*.xml");
        arquivos.Should().HaveCount(1);
        (await File.ReadAllTextAsync(arquivos[0])).Should().Contain("Prosseguir mesmo assim");
        r.Error.Should().Contain(arquivos[0], "quem lê o erro precisa saber onde está a tela inteira");
    }

    [Fact]
    public async Task Tela_repetida_no_lote_grava_um_arquivo_so()
    {
        // 🔴 Um modal aparece no contato 1 e nos 29 seguintes. Sem deduplicar pelo conteúdo, a pasta
        // enche de cópias idênticas e a tela NOVA, que é a única que ensina algo, fica enterrada.
        await EnviarAsync();
        await EnviarAsync();
        await EnviarAsync();

        Directory.GetFiles(_pasta, "tela-*.xml").Should().HaveCount(1);
    }

    [Fact]
    public async Task Aparelho_e_acordado_ANTES_de_abrir_a_conversa()
    {
        // 🔴 A tela apaga em 10 min e a pausa entre blocos é de ~30: o primeiro envio de cada bloco
        // encontra o aparelho dormindo. Acordar DEPOIS do `am start` não adianta, porque a Activity já
        // teria nascido atrás do cadeado e o botão de enviar nunca entraria na árvore.
        var (_, adb) = await EnviarAsync();

        var acorda = adb.Comandos.FindIndex(c => c.Contains("KEYCODE_WAKEUP", StringComparison.Ordinal));
        var abre = adb.Comandos.FindIndex(c => c.Contains("am start", StringComparison.Ordinal));
        acorda.Should().BeGreaterThan(-1, "sem isto o lote morre na primeira pausa longa");
        abre.Should().BeGreaterThan(acorda, "acordar depois de abrir não desfaz a conversa nascida travada");
    }

    [Fact]
    public async Task Nunca_usa_KEYCODE_POWER_que_apagaria_a_tela_acesa()
    {
        // POWER é uma CHAVE: numa tela já acesa ele APAGA. Seria transformar a proteção em defeito, e o
        // sintoma apareceria só no segundo envio, o mais difícil de associar à causa.
        var (_, adb) = await EnviarAsync();

        adb.Comandos.Should().NotContain(c => c.Contains("KEYCODE_POWER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tela_bloqueada_e_diagnosticada_como_APARELHO_e_nao_como_numero()
    {
        // O erro antigo era "a conversa não abriu", que manda conferir o número. Com PIN no aparelho a
        // causa é outra e o conserto é outro: essa confusão custa o lote inteiro toda madrugada.
        const string TelaDeCadeado =
            """<node resource-id="com.android.systemui:id/keyguard_indication_area" clickable="false" bounds="[0,0][1080,2400]"/>""";
        var adb = new AdbDeUmaTela(TelaDeCadeado);
        using var driver = new WhatsAppUiDriver(adb, Opts);

        var r = await driver.SendAsync(Numero, Texto, CancellationToken.None);

        r.Sent.Should().BeFalse();
        r.NoWhatsAppAccount.Should().BeFalse("o número não tem culpa de o celular estar trancado");
        r.Error.Should().Contain("TELA BLOQUEADA");
    }

    [Fact]
    public async Task Cadeado_do_proprio_WhatsApp_NAO_e_confundido_com_tela_de_bloqueio()
    {
        // 🔴 O WhatsApp tem cadeado de conversa e aviso de criptografia, então "lock" e "keyguard"
        // soltos no XML pegariam a tela de conversa. O falso positivo não erra uma ação (o envio já
        // falhou), erra a INSTRUÇÃO: mandaria tirar um bloqueio de tela que não existe, enquanto a
        // causa real seguiria intocada. Por isso o marcador exige o pacote do systemui.
        const string TelaComCadeadoDoApp =
            """<node resource-id="com.whatsapp:id/lock_icon_view" text="Conversa bloqueada" clickable="true" bounds="[0,0][100,100]"/>""";
        var adb = new AdbDeUmaTela(TelaComCadeadoDoApp);
        using var driver = new WhatsAppUiDriver(adb, Opts);

        var r = await driver.SendAsync(Numero, Texto, CancellationToken.None);

        r.Sent.Should().BeFalse();
        r.Error.Should().NotContain("TELA BLOQUEADA", "o cadeado é do app, não do Android");
    }

    [Fact]
    public async Task Veredito_de_numero_sem_conta_tambem_guarda_a_tela()
    {
        // 🔴 É o veredito mais FORTE do driver: encerra o contato, culpa o número e manda conferir a
        // lista. E nasce de uma busca por texto. Sem a tela guardada, um lote inteiro pode marcar gente
        // boa como inexistente e não sobra com que discordar. Motivado por operação em 2026-08-10.
        const string TelaSemConta =
            """<node text="O número não está no WhatsApp" clickable="false" bounds="[0,0][1080,600]"/>""";
        var adb = new AdbDeUmaTela(TelaSemConta);
        using var driver = new WhatsAppUiDriver(adb, Opts);

        var r = await driver.SendAsync(Numero, Texto, CancellationToken.None);

        r.NoWhatsAppAccount.Should().BeTrue();
        var arquivos = Directory.GetFiles(_pasta, "tela-*.xml");
        arquivos.Should().HaveCount(1, "o veredito precisa ser conferível depois");
        r.Error.Should().Contain(arquivos[0]);
    }

    [Fact]
    public async Task Captura_acontece_ANTES_do_BACK_que_apaga_a_evidencia()
    {
        // O BACK é o que fecha o aviso e devolve o aparelho ao estado neutro. Capturar depois dele
        // descreveria a tela que o próprio driver criou, não a que bloqueou o envio.
        var (_, adb) = await EnviarAsync();

        var back = adb.Comandos.FindIndex(c => c.Contains("KEYCODE_BACK", StringComparison.Ordinal));
        back.Should().BeGreaterThan(-1, "sem botão conhecido, o BACK é a saída");
        adb.Comandos.Take(back).Should().Contain(
            c => c.Contains("uiautomator dump", StringComparison.Ordinal),
            "a tela precisa ter sido lida enquanto o aviso ainda estava nela");
    }
}
