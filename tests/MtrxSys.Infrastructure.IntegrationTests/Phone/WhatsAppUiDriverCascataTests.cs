using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>A cascata de abertura da conversa: registro do contato, lista do "+", e por último o número.</summary>
/// <remarks>
/// 🔴 POR QUE ELA EXISTE: abrir por `whatsapp://send?phone=…` exige que o app RESOLVA o número no
/// servidor, e é aí que ele nega gente que TEM WhatsApp — quando a conta guarda o número na outra forma
/// do 9º dígito, e quando a conta está restringida. Os dois primeiros níveis usam um contato que o app
/// JÁ reconheceu, então não há o que resolver.
/// <para>Estes testes fixam a ORDEM e a QUEDA de um nível para o outro. O que eles não podem afirmar é
/// que os resource-ids da lista do "+" estão certos no aparelho real: isso só o primeiro lote responde,
/// e é por isso que a desistência guarda a tela.</para>
/// </remarks>
public sealed class WhatsAppUiDriverCascataTests
{
    private const string Numero = "+5584998420730";
    private const string Texto = "oi";
    private const string ChatUri = "content://com.android.contacts/data/4242";

    /// <summary>Tela sem o campo de mensagem: nenhuma conversa aberta.</summary>
    private const string TelaSemCampo =
        """<node resource-id="com.whatsapp:id/status" bounds="[10,10][20,20]"/>""";

    /// <summary>Conversa aberta: o campo de mensagem existe.</summary>
    private const string TelaComCampo =
        """<node resource-id="com.whatsapp:id/entry" bounds="[100,1800][900,1900]"/>""";

    /// <summary>Adb falso que só "abre a conversa" quando o comando esperado chega.</summary>
    /// <remarks>O gatilho é passado por quem monta o teste, então dá pra afirmar qual nível abriu.</remarks>
    private sealed class AdbQueAbreCom(string gatilho) : IAdbRunner
    {
        private bool _aberta;

        public List<string> Comandos { get; } = [];

        /// <summary>Tela devolvida enquanto a conversa não abriu. Deixa montar o seletor do "+".</summary>
        public string TelaPadrao { get; init; } = TelaSemCampo;

        public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct)
        {
            Comandos.Add(command);
            if (command.Contains(gatilho, StringComparison.Ordinal))
            {
                _aberta = true;
            }
            if (command.Contains("uiautomator dump", StringComparison.Ordinal))
            {
                var tela = _aberta ? TelaComCampo : TelaPadrao;
                return Task.FromResult((0, $"<hierarchy>{tela}</hierarchy>", ""));
            }
            return Task.FromResult((0, "", ""));
        }

        public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
            Task.FromResult((0, "", ""));

        public bool SupportsRoot => false;

        public string Target => "falso";
    }

    // Digitação humana LIGADA porque é o default e é o caminho que tem a cascata. Texto ASCII simples
    // pra o driver conseguir digitar pelo `input text` sem depender do IME de broadcast.
    private static PhoneOptions Opts => new()
    {
        HumanTyping = true,
        WhatsAppOpenWaitMs = 500,
        WhatsAppSendWaitMs = 500,
        WhatsAppSendTimeoutSeconds = 20,
    };

    [Fact]
    public async Task Com_registro_na_agenda_nao_usa_o_deep_link_por_numero()
    {
        // O ponto da mudança inteira: quando o contato já é conhecido pelo app, o número não entra na
        // conversa. Se este teste falhar, voltamos a depender da resolução que nega gente boa.
        var adb = new AdbQueAbreCom("content://com.android.contacts/data/");
        using var driver = new WhatsAppUiDriver(adb, Opts);

        await driver.SendAsync(Numero, Texto, CancellationToken.None, ChatUri);

        adb.Comandos.Should().Contain(c => c.Contains(ChatUri, StringComparison.Ordinal));
        adb.Comandos.Should().NotContain(c => c.Contains("whatsapp://send", StringComparison.Ordinal),
            "com o contato já resolvido, abrir por número seria pagar o risco à toa");
    }

    [Fact]
    public async Task Sem_registro_tenta_a_lista_do_mais_antes_de_cair_no_numero()
    {
        // Ordem importa: a lista do "+" vem do banco interno do app, que enxerga contato que a agenda
        // não publicou. Cair direto no número perderia justamente esses.
        var adb = new AdbQueAbreCom("whatsapp://send");
        using var driver = new WhatsAppUiDriver(adb, Opts);

        await driver.SendAsync(Numero, Texto, CancellationToken.None, chatUri: null);

        var lista = adb.Comandos.FindIndex(c => c.Contains("monkey -p", StringComparison.Ordinal));
        var numero = adb.Comandos.FindIndex(c => c.Contains("whatsapp://send", StringComparison.Ordinal));
        lista.Should().BeGreaterThan(-1, "o nível da lista precisa ser tentado");
        numero.Should().BeGreaterThan(lista, "o número é o ÚLTIMO recurso, não o primeiro");
    }

    [Fact]
    public async Task Nunca_toca_quando_a_tela_oferece_CONVIDAR()
    {
        // 🔴 Quando o número não é usuário, o seletor mostra a linha dele numa seção de "Convidar", e
        // tocar ali dispara um CONVITE POR SMS: irreversível, para um estranho, e cobrado. Desistir
        // deste nível custa nada, porque o deep link resolve o caso legítimo logo abaixo.
        const string TelaDeConvite =
            """
            <node resource-id="com.whatsapp:id/contactpicker_row_name" text="Convidar para o WhatsApp" bounds="[0,300][1080,400]"/>
            """;
        var adb = new AdbQueAbreCom("nunca-abre") { TelaPadrao = TelaDeConvite };
        using var driver = new WhatsAppUiDriver(adb, Opts);

        await driver.SendAsync(Numero, Texto, CancellationToken.None, chatUri: null);

        adb.Comandos.Should().NotContain(c => c.StartsWith("input tap", StringComparison.Ordinal),
            "com convite na tela, nenhum toque pode acontecer no seletor");
    }

    [Fact]
    public async Task Para_de_tentar_a_lista_depois_de_falhar_seguido()
    {
        // 🔴 O nível da lista custa ~4s e navega o app antes de desistir. Com os resource-ids errados
        // neste aparelho, ele falharia assim nos 87 contatos do lote, cobrando o pedágio 87 vezes e
        // sacudindo a tela antes de cada deep link. Três tentativas bastam pra concluir que a tela do
        // seletor não é a esperada, e isso não muda no meio do lote.
        var adb = new AdbQueAbreCom("whatsapp://send");
        using var driver = new WhatsAppUiDriver(adb, Opts);

        for (var i = 0; i < 4; i++)
        {
            await driver.SendAsync(Numero, Texto, CancellationToken.None, chatUri: null);
        }

        adb.Comandos.Count(c => c.Contains("monkey -p", StringComparison.Ordinal))
            .Should().Be(3, "depois de três desistências o nível para de ser tentado");
    }

    [Fact]
    public async Task Registro_que_nao_abre_cai_para_os_niveis_seguintes()
    {
        // A URI pode existir e o intent não abrir nada (versão de app diferente, registro velho). O
        // nível 1 não pode virar beco sem saída: é isso que torna seguro implementá-lo sem ter medido.
        var adb = new AdbQueAbreCom("whatsapp://send");
        using var driver = new WhatsAppUiDriver(adb, Opts);

        await driver.SendAsync(Numero, Texto, CancellationToken.None, ChatUri);

        adb.Comandos.Should().Contain(c => c.Contains(ChatUri, StringComparison.Ordinal));
        adb.Comandos.Should().Contain(c => c.Contains("whatsapp://send", StringComparison.Ordinal),
            "sem queda para o deep link, um registro ruim deixaria o contato inalcançável");
    }
}
