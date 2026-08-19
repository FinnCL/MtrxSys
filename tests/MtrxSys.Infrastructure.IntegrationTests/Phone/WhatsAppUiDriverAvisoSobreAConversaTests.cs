using FluentAssertions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>O aviso que o WhatsApp desenha POR CIMA de uma conversa que já abriu: fechar antes de
/// digitar, sem confundir o botão dele com o conteúdo do chat.</summary>
/// <remarks>
/// 🔴 O CASO, relatado operando em 2026-08-18: conversa com MENSAGENS TEMPORÁRIAS mostra um aviso com
/// "OK" na frente do chat, e enquanto ele está lá o toque não chega no campo de mensagem.
///
/// <para>Já havia recuperação pra aviso na frente da conversa, mas ela só roda quando o campo de
/// mensagem NÃO é encontrado. Este aviso não tira o campo da árvore: o `uiautomator dump` despeja a
/// tela inteira, então o `id/entry` continua lá atrás do aviso. O driver concluía que a conversa abriu,
/// digitava contra um toque bloqueado, e a falha chegava ao console como "campo ficou com nada
/// caracteres", que acusa o teclado quando o teclado está perfeito.</para>
///
/// <para>🔴 O QUE PROVA QUE EXISTE AVISO É A JANELA. O dump concatena as JANELAS da tela como filhas de
/// `&lt;hierarchy&gt;`, e um diálogo é outra janela. Por isso metade destes testes é sobre estrutura e
/// não sobre rótulo: um aviso na própria janela da conversa, um aviso em janela separada, e a mesma
/// tela nas DUAS ordens possíveis entre janelas. Ordem é a suposição que não dá pra medir sem o
/// aparelho na mão, então o jeito de não depender dela é testar as duas.</para>
///
/// <para>A outra metade é sobre não tocar no que só parece botão, que é o preço de fechar aviso com a
/// conversa aberta.</para>
/// </remarks>
public sealed class WhatsAppUiDriverAvisoSobreAConversaTests : IDisposable
{
    private const string Numero = "5584998420730";
    private const string Texto = "Ola, tudo bem por ai?";

    private readonly string _pasta =
        Path.Combine(Path.GetTempPath(), "mtrx-telas-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }

    private PhoneOptions Opts(bool digitacaoHumana) => new()
    {
        HumanTyping = digitacaoHumana,
        WhatsAppOpenWaitMs = 500,
        WhatsAppSendWaitMs = 500,
        WhatsAppSendTimeoutSeconds = 20,
        UiDumpDir = _pasta,
    };

    private static AndroidFalso ComAviso()
    {
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Numero);
        android.ComAvisoSobreposto.Add(Numero);
        return android;
    }

    // As duas formas do dump moram no DumpFalso, junto com a explicação de por que a forma importa.
    private static string Janela(string dentro) => DumpFalso.Janela(dentro);

    private static readonly string CampoDeMensagem = DumpFalso.CampoDeMensagem();

    [Fact]
    public async Task Com_digitacao_humana_o_aviso_sai_da_frente_e_a_mensagem_e_entregue()
    {
        // 🔴 O CAMINHO QUE RODA: digitação humana é o default. Aqui o aviso não impedia só o toque em
        // enviar, impedia a DIGITAÇÃO inteira, e o contato ia pro balde de falha de aparelho.
        var android = ComAviso();
        using var driver = new WhatsAppUiDriver(android, Opts(digitacaoHumana: true));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        android.AvisoSobrepostoNaTela.Should().BeFalse("o aviso tinha que ser fechado");
        android.Entregues.Should().ContainSingle().Which.Should().Be((Numero, Texto),
            "quem tem que receber é o contato ATUAL, não só o próximo do lote");
        envio.Sent.Should().BeTrue();
    }

    [Fact]
    public async Task Sem_digitacao_humana_o_aviso_sai_antes_do_toque_em_enviar()
    {
        // Mesmo aviso, outro caminho: aqui o texto já vem no deep link, então o que o aviso bloqueia é
        // o toque em ENVIAR. A falha saía como "o texto continua no campo", que também não fala do
        // aviso. Os dois caminhos precisam do mesmo tratamento, senão o mesmo defeito real aparece no
        // relatório em dois baldes diferentes conforme um toggle de configuração.
        var android = ComAviso();
        using var driver = new WhatsAppUiDriver(android, Opts(digitacaoHumana: false));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        android.Entregues.Should().ContainSingle().Which.Should().Be((Numero, Texto));
        envio.Sent.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_ordem_das_janelas_no_dump_nao_muda_o_desfecho(bool avisoAntes)
    {
        // 🔴 O TESTE QUE A PRIMEIRA VERSÃO NÃO TINHA, E QUE TERIA CONDENADO ELA. Aquela leitura só
        // olhava o trecho POSTERIOR ao campo, apoiada em "quem desenha por cima aparece depois". Isso
        // vale DENTRO de uma janela, onde o dump é varredura em profundidade. Entre JANELAS quem manda
        // é o `uiautomator`, que serializa a ATIVA primeiro — e a ativa é o modal. Ou seja: na ordem
        // provável de verdade, a varredura nunca alcançava o aviso e o fechamento era um no-op.
        //
        // E não dava pra perceber, porque o falso emitia o aviso DEPOIS da conversa: falso e driver
        // compartilhavam a suposição, e o verde só provava que os dois concordavam entre si. Rodar a
        // mesma tela nas duas ordens é o que substitui um dump real que ninguém tem aqui.
        var android = ComAviso();
        android.AvisoAntesDaConversa = avisoAntes;
        using var driver = new WhatsAppUiDriver(android, Opts(digitacaoHumana: true));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        envio.Sent.Should().BeTrue("a leitura não pode depender da ordem em que as janelas saíram");
        android.Entregues.Should().ContainSingle().Which.Should().Be((Numero, Texto));
    }

    [Fact]
    public async Task Aviso_desenhado_dentro_da_propria_janela_da_conversa_tambem_e_fechado()
    {
        // A outra forma de aviso: não é outra janela, é um painel desenhado DENTRO da conversa (bottom
        // sheet de layout). Aqui a ordem dos nós vale, porque é ordem dentro de UMA janela, e é ela que
        // separa o painel dos ancestrais do campo: pai vem antes do filho, painel vem depois.
        const string PainelSobreOCampo =
            """
            <node text="Mensagens temporárias" package="com.whatsapp" bounds="[0,1400][1080,2400]">
              <node text="OK" class="android.widget.Button" package="com.whatsapp" clickable="true" bounds="[400,2000][700,2100]"/>
            </node>
            """;
        var adb = new AdbDeTelaFixa(Janela(CampoDeMensagem + PainelSobreOCampo));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(400, 2000, 700, 2100).Should().BeTrue("o painel cobre o campo e o botão é dele");
    }

    [Fact]
    public async Task Avisos_empilhados_fecham_pelo_botao_da_segunda_janela()
    {
        // 🔴 Os avisos do WhatsApp vêm EMPILHADOS: mensagens temporárias e, atrás dele, criptografia ou
        // conta comercial. A primeira versão parava na primeira janela candidata e devolvia "tem aviso,
        // não sei fechar" mesmo com o botão à mão na janela seguinte, e o lote parava com o sintoma de
        // sempre. Achar aviso e achar botão são perguntas diferentes: a primeira resposta não encerra
        // a segunda.
        const string AvisoSemBotao =
            "<node text=\"Suas mensagens são criptografadas\" package=\"com.whatsapp\" "
            + "bounds=\"[100,600][900,800]\"/>";
        const string AvisoComBotao =
            "<node text=\"Mensagens temporárias\" package=\"com.whatsapp\" bounds=\"[100,900][900,1100]\"/>"
            + "<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
            + "clickable=\"true\" bounds=\"[400,1200][600,1300]\"/>";
        var adb = new AdbDeTelaFixa(
            Janela(AvisoSemBotao) + Janela(AvisoComBotao) + Janela(CampoDeMensagem));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(400, 1200, 600, 1300).Should().BeTrue(
            "a primeira janela sem botão reconhecível não pode encerrar a procura");
    }

    // ── O outro lado: não tocar no que só parece botão ────────────────────────────────────────────

    [Fact]
    public async Task Bolha_de_mensagem_escrita_ok_nao_e_confundida_com_botao()
    {
        // 🔴 O PREÇO DE FECHAR AVISO COM A CONVERSA ABERTA. No WhatsApp a bolha de mensagem é CLICÁVEL e
        // o rótulo dela é o texto que a pessoa escreveu, então "ok" e "entendi" (respostas comuns) são
        // rótulos legítimos dentro da conversa. Tocar por texto tocaria na conversa da pessoa.
        //
        // Repare que aqui EXISTE um aviso na tela, ou seja, a condição que autoriza tocar está dada. O
        // que salva a bolha é ela estar em OUTRA janela que não a do aviso.
        const string BolhaComOk =
            "<node text=\"ok\" resource-id=\"com.whatsapp:id/message_text\" package=\"com.whatsapp\" "
            + "class=\"android.widget.TextView\" clickable=\"true\" bounds=\"[50,1000][800,1100]\"/>";
        const string AvisoSemBotaoConhecido =
            "<node text=\"Mensagens temporárias\" package=\"com.whatsapp\" bounds=\"[100,900][900,1100]\"/>";
        var adb = new AdbDeTelaFixa(
            Janela(AvisoSemBotaoConhecido) + Janela(BolhaComOk + CampoDeMensagem));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(50, 1000, 800, 1100).Should().BeFalse(
            "tocar na mensagem da pessoa achando que é botão é tocar às cegas na conversa dela");
        adb.TocouEm(50, 1800, 880, 1900).Should().BeTrue("o campo de mensagem continua sendo tocado");
    }

    [Fact]
    public async Task Botao_de_resposta_rapida_dentro_da_conversa_nunca_e_tocado()
    {
        // 🔴 O PIOR TOQUE POSSÍVEL, e o que quase entrou junto com este conserto. Mensagem de template
        // traz botões de RESPOSTA RÁPIDA, que são botões DE VERDADE (class de botão, clicáveis) e cujo
        // rótulo quem escolhe é quem mandou a mensagem: "OK" é o mais comum que existe. Tocar num deles
        // MANDA UMA MENSAGEM pro contato. Seria o disparo escrevendo sozinho na conversa de alguém.
        //
        // 🔴 E ELE ESTÁ DEPOIS DO CAMPO DE PROPÓSITO, com um aviso de verdade na tela. É a combinação
        // que a primeira versão tocava: ela procurava o botão em QUALQUER lugar depois do campo, então
        // "tem coisa cobrindo o campo" e "existe um OK na árvore" bastavam, sem que um tivesse relação
        // com o outro. Agora o botão só conta se estiver DENTRO do aviso, e este está na conversa.
        const string RespostaRapida =
            "<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
            + "clickable=\"true\" bounds=\"[50,1000][400,1100]\"/>";
        const string AvisoSemBotaoConhecido =
            "<node text=\"Mensagens temporárias\" package=\"com.whatsapp\" bounds=\"[100,900][900,1100]\"/>";
        var adb = new AdbDeTelaFixa(
            Janela(AvisoSemBotaoConhecido) + Janela(CampoDeMensagem + RespostaRapida));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(50, 1000, 400, 1100).Should().BeFalse(
            "responder por conta própria na conversa de um contato é irreversível, e o botão não é do "
            + "aviso: está na janela da conversa");
    }

    [Fact]
    public async Task Botao_neutro_fora_do_painel_que_cobre_o_campo_nao_e_tocado()
    {
        // A mesma exigência do teste acima, agora dentro de UMA janela só: tem painel cobrindo o campo,
        // e tem um botão neutro na tela, mas o botão está FORA do painel. Sem a exigência de o botão
        // caber dentro do aviso, os dois fatos somariam num toque, e eles não têm relação nenhuma.
        const string PainelSemBotao =
            "<node text=\"Mensagens temporárias\" package=\"com.whatsapp\" bounds=\"[0,1400][1080,2400]\"/>";
        const string BotaoNeutroForaDoPainel =
            "<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
            + "clickable=\"true\" bounds=\"[100,300][400,400]\"/>";
        var adb = new AdbDeTelaFixa(
            Janela(CampoDeMensagem + PainelSemBotao + BotaoNeutroForaDoPainel));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(100, 300, 400, 400).Should().BeFalse(
            "botão que não é do aviso não fecha o aviso, e tocar nele é tocar às cegas");
    }

    [Fact]
    public async Task Botao_neutro_que_nao_cobre_o_campo_tambem_nao_e_tocado()
    {
        // 🔴 A OUTRA METADE DA REGRA. Barra inferior, rodapé de conta comercial e faixa de "adicionar
        // aos contatos" ficam DEPOIS do campo na árvore e não cobrem nada: são parte da conversa, não um
        // aviso na frente dela. Sem exigir que algo esteja por cima, qualquer um desses com rótulo
        // neutro viraria alvo de toque.
        const string BarraEmbaixo =
            "<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
            + "clickable=\"true\" bounds=\"[50,1900][400,2000]\"/>";
        var adb = new AdbDeTelaFixa(Janela(CampoDeMensagem + BarraEmbaixo));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(50, 1900, 400, 2000).Should().BeFalse(
            "sem nada na frente da conversa não existe aviso pra fechar, e o toque seria às cegas");
    }

    // ── O que sobra quando o driver NÃO reconhece o botão ─────────────────────────────────────────

    [Fact]
    public async Task A_tela_do_aviso_fica_gravada_antes_de_ser_fechada()
    {
        // 🔴 Depois do toque o aviso não volta pro mesmo contato: ou se captura naquele instante, ou a
        // prova de que ele existiu some. E é esse dump que mantém a lista de rótulos MEDIDA em vez de
        // adivinhada, que é a fraqueza declarada dela.
        var android = ComAviso();
        using var driver = new WhatsAppUiDriver(android, Opts(digitacaoHumana: true));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        // Uma tela entre as guardadas, e não a única: a cascata de abertura também guarda a dela ao
        // desistir do nível da lista, e as duas respondem perguntas diferentes.
        var telas = await Task.WhenAll(
            Directory.GetFiles(_pasta, "tela-*.xml").Select(f => File.ReadAllTextAsync(f)));
        telas.Should().Contain(t => t.Contains("Mensagens temporárias", StringComparison.Ordinal));
        envio.Sent.Should().BeTrue(
            "a captura tem que acontecer no caminho que DEU CERTO; guardar a tela só quando o envio "
            + "falha é o que já existia, e é justamente o que não ensina nada sobre este aviso");
    }

    [Fact]
    public async Task Campo_que_nao_recebeu_o_texto_relata_os_botoes_que_estavam_na_tela()
    {
        // 🔴 "Campo ficou com nada caracteres" descreve o sintoma e esconde as duas causas, que têm
        // consertos opostos: teclado falhando, ou alguma coisa por cima da conversa comendo o toque.
        // Quando o aviso tem um botão que o driver AINDA não conhece, esta linha é o que permite
        // descobrir o rótulo novo sem alguém estar olhando o aparelho na hora exata.
        const string AvisoComBotaoDesconhecido =
            "<node text=\"Prosseguir mesmo assim\" class=\"android.widget.Button\" "
            + "package=\"com.whatsapp\" clickable=\"true\" bounds=\"[100,900][500,1000]\"/>";
        var adb = new AdbDeTelaFixa(
            Janela(AvisoComBotaoDesconhecido) + Janela(CampoDeMensagem));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        envio.Sent.Should().BeFalse();
        envio.Error.Should().Contain("Prosseguir mesmo assim", "é o candidato a rótulo novo");
        envio.Error.Should().Contain("OUTRA JANELA",
            "afirmar a causa provável é o que separa 'mexa no aviso' de 'mexa no teclado'");
        adb.TocouEm(100, 900, 500, 1000).Should().BeFalse(
            "rótulo desconhecido é justamente o que NÃO se toca: a tela pode ter Bloquear ou Denunciar");
        Directory.GetFiles(_pasta, "tela-*.xml").Should().ContainSingle(
            "a tela inteira precisa sobreviver ao lote");
    }

    [Fact]
    public async Task Dialogo_de_numero_sem_conta_nao_e_fechado_antes_do_diagnostico()
    {
        // 🔴 REGRESSÃO QUE ESTE CONSERTO PODIA CAUSAR. O diálogo de "não está no WhatsApp" também tem um
        // botão OK. Fechá-lo antes de ler apagaria a única prova de que a causa é o NÚMERO, e a falha
        // voltaria pro balde de APARELHO — que é o contador que para o lote. Por isso o fechamento só
        // roda com o campo de mensagem JÁ encontrado, ou seja, com a conversa aberta.
        const string DialogoSemConta =
            "<node text=\"O número não está no WhatsApp\" package=\"com.whatsapp\" "
            + "clickable=\"false\" bounds=\"[100,700][900,900]\"/>"
            + "<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
            + "clickable=\"true\" bounds=\"[400,1200][600,1300]\"/>";
        var adb = new AdbDeTelaFixa(Janela(DialogoSemConta));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        envio.NoWhatsAppAccount.Should().BeTrue(
            "sem esta marca o console culpa o aparelho e para o lote por causa de um número ruim");
        adb.TocouEm(400, 1200, 600, 1300).Should().BeFalse(
            "diagnóstico conclusivo não tem o que desbloquear, e o toque só apagaria a prova");
    }

    [Fact]
    public async Task Dialogo_de_numero_sem_conta_por_cima_de_uma_conversa_velha_tambem_nao_e_fechado()
    {
        // 🔴 O FURO QUE A REGRA "só fecha com o campo encontrado" NÃO tapava, e que só aparece quando
        // se junta duas coisas. O diálogo de "não está no WhatsApp" é OUTRA JANELA do app e tem um
        // botão OK: pela regra de janela ele é um aviso perfeitamente fechável. A proteção era supor
        // que, achando o campo, a conversa PEDIDA está aberta — e essa suposição cai quando o aparelho
        // ignora os BACK do SairDaConversaAnteriorAsync e a conversa ANTERIOR fica na tela por baixo
        // do diálogo. Aqui a tela nunca muda, que é exatamente um aparelho que não responde a BACK.
        //
        // Fechar apagaria a única prova de que a causa é o NÚMERO, e a falha voltaria pro balde de
        // APARELHO, que é o contador que para o lote por engano.
        const string DialogoSemConta =
            "<node text=\"O número não está no WhatsApp\" package=\"com.whatsapp\" "
            + "bounds=\"[100,700][900,900]\"/>"
            + "<node text=\"OK\" class=\"android.widget.Button\" package=\"com.whatsapp\" "
            + "clickable=\"true\" bounds=\"[400,1200][600,1300]\"/>";
        var adb = new AdbDeTelaFixa(Janela(DialogoSemConta) + Janela(CampoDeMensagem));
        using var driver = new WhatsAppUiDriver(adb, Opts(digitacaoHumana: true));

        var envio = await driver.SendAsync("+" + Numero, Texto, CancellationToken.None);

        adb.TocouEm(400, 1200, 600, 1300).Should().BeFalse(
            "a tela já disse a causa, e ela vale mais do que fechar um aviso");
        envio.Sent.Should().BeFalse();
    }
}
