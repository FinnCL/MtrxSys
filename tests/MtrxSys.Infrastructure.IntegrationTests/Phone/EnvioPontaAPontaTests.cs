using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Ponta a ponta do disparo pelo aparelho: gravar na agenda, abrir a conversa e enviar, com o
/// <see cref="AndroidFalso"/> no lugar do celular.</summary>
/// <remarks>
/// <para>A asserção principal é sempre <c>Entregues</c>, ou seja, O QUE O APARELHO RECEBEU. O valor
/// devolvido é conferido depois, e a divergência entre os dois é o que estes testes existem pra pegar:
/// quase todo defeito desta área era exatamente um retorno afirmando uma coisa e o aparelho estando
/// noutra.</para>
/// <para>⚠️ COBERTURA. Isto cobre tudo abaixo do <c>IPhoneOrchestrator</c>. O laço do console
/// (<c>PhoneConsoleCommand.DispararAsync</c>) NÃO está coberto: ele mora no MtrxSys.Cli, que não tem
/// projeto de teste, e grava estado num diretório fixo do usuário. Onde um teste aqui reproduz uma
/// decisão do console (a segunda chance, por exemplo), isso está dito no próprio teste — a decisão
/// continua sem cobertura, só a mecânica que ela usa é que está provada.</para>
/// </remarks>
public sealed class EnvioPontaAPontaTests
{
    private const string Texto = "Ola, tudo bem?";

    // Mesma pessoa nas duas formas: com o 9o digito e sem.
    private const string Com9 = "5584998420730";
    private const string Sem9 = "558498420730";

    private static PhoneOptions Opts => new()
    {
        HumanTyping = false,
        WhatsAppOpenWaitMs = 500,
        WhatsAppSendWaitMs = 500,
        WhatsAppSendTimeoutSeconds = 15,
    };

    private static (WhatsAppUiDriver Driver, WhatsAppContactsReader Agenda) Montar(AndroidFalso android) =>
        (new WhatsAppUiDriver(android, Opts), new WhatsAppContactsReader(android));

    // ── O caminho que deve funcionar ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Grava_na_agenda_e_entrega_a_mensagem()
    {
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        var (driver, agenda) = Montar(android);
        using var _ = driver;

        var gravado = await agenda.SaveContactAsync("+" + Com9, "Fulano de Tal", CancellationToken.None);
        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        gravado.Should().Be("ok");
        android.Agenda.Values.Should().ContainSingle()
            .Which.Telefone.Should().Be("+" + Com9,
                "só a forma E.164 COM o + é resolvível pelo WhatsApp; gravar os dígitos crus é o que "
                + "envenena o contato de forma persistente");
        android.Agenda.Values.Single().Nome.Should().Be("Fulano de Tal",
            "o espaço tem que sobreviver ao shell do aparelho");

        android.Entregues.Should().ContainSingle().Which.Should().Be((Com9, Texto));
        envio.Sent.Should().BeTrue();
        envio.DeliveryStatus.Should().Be("delivered", "a entrega é lida da própria tela, não inventada");
    }

    [Fact]
    public async Task Numero_sem_conta_falha_com_diagnostico_e_sem_entregar_nada()
    {
        var android = new AndroidFalso(); // ninguém tem conta
        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        android.Entregues.Should().BeEmpty();
        envio.Sent.Should().BeFalse();
        envio.Uncertain.Should().BeFalse("o app respondeu; é conclusivo, e tentar a outra forma é seguro");
        envio.Error.Should().Contain("não tem conta");
        envio.NoWhatsAppAccount.Should().BeTrue(
            "o console usa esta marca para separar os disjuntores: número sem conta não diz nada sobre "
            + "o próximo contato, e não pode derrubar o lote como derruba uma falha de aparelho");
    }

    /// <summary>Tela ilegível é falha do APARELHO, e não pode ser vendida como número sem conta.</summary>
    /// <remarks>
    /// 🔴 O CONSOLE DECIDE COM ISTO. Sequência de "sem conta" acusa a LISTA, e o lote continua até um
    /// limite mais frouxo; falha de aparelho acusa o CELULAR, e o lote para em três. Classificar tela
    /// ilegível como "sem conta" queimaria a lista inteira contra um aparelho travado, uma conversa
    /// por vez, que é exatamente o defeito que o disjuntor existe para impedir.
    /// </remarks>
    [Fact]
    public async Task Falha_de_leitura_de_tela_nao_e_classificada_como_numero_sem_conta()
    {
        var android = new AndroidFalso { DumpsFalhando = 99 };
        android.ContasExistentes.Add(Com9);   // a conta EXISTE: o problema é só não conseguir ler a tela
        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        android.Entregues.Should().BeEmpty();
        envio.Sent.Should().BeFalse();
        envio.NoWhatsAppAccount.Should().BeFalse(
            "não dá pra culpar o número quando nem a tela foi lida");
    }

    /// <summary>Conversa com MENSAGENS TEMPORÁRIAS abre um aviso por cima; a mensagem tem que sair
    /// mesmo assim, NESTE contato.</summary>
    /// <remarks>
    /// 🔴 Enquanto o aviso está na tela, o campo de mensagem e o botão de enviar não existem na árvore,
    /// e o envio falhava com "a conversa não abriu" — diagnóstico que acusa o APARELHO quando o
    /// aparelho está perfeito, e que alimenta o alerta de celular travado. A recuperação já existia,
    /// mas rodava DEPOIS do resultado: limpava a tela para o próximo contato e perdia o atual.
    /// </remarks>
    [Fact]
    public async Task Aviso_de_mensagem_temporaria_e_dispensado_e_a_mensagem_sai()
    {
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.ComAvisoTemporaria.Add(Com9);
        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        android.AvisoNaTela.Should().BeFalse("o aviso tinha que sair da frente");
        android.Entregues.Should().ContainSingle().Which.Should().Be((Com9, Texto),
            "o contato ATUAL tem que receber, não só o próximo do lote");
        envio.Sent.Should().BeTrue();
    }

    /// <summary>Dispensar aviso não pode apagar a prova de que o número não tem conta.</summary>
    /// <remarks>
    /// 🔴 O diálogo de "não está no WhatsApp" TAMBÉM tem um botão OK. Dispensar antes de diagnosticar
    /// apagaria a única evidência da causa, e a falha voltaria a ser classificada como problema de
    /// aparelho — reintroduzindo, por outro caminho, o defeito que a marca NoWhatsAppAccount corrigiu.
    /// </remarks>
    [Fact]
    public async Task Numero_sem_conta_continua_sendo_diagnosticado_com_a_limpeza_de_tela_ligada()
    {
        var android = new AndroidFalso(); // ninguém tem conta: o diálogo aparece
        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        envio.NoWhatsAppAccount.Should().BeTrue();
        envio.Error.Should().Contain("não tem conta");
    }

    // ── O caso que motivou a segunda chance ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_conta_existe_so_na_outra_forma_do_numero()
    {
        // Medido em 2026-08-05: no mesmo DDD 84, um número de 12 dígitos entregou e outro falhou. O
        // WhatsApp guarda a conta ora com o 9º dígito, ora sem, conforme a época do registro.
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9); // a lista tem Sem9, mas a conta viva é a Com9
        var (driver, agenda) = Montar(android);
        using var _ = driver;

        await agenda.SaveContactAsync("+" + Sem9, "Fulano", CancellationToken.None);
        var primeira = await driver.SendAsync("+" + Sem9, Texto, CancellationToken.None);

        primeira.Sent.Should().BeFalse();
        primeira.Uncertain.Should().BeFalse();
        android.Entregues.Should().BeEmpty();

        // ⚠️ A DECISÃO de tentar a outra forma é do console e não está coberta aqui; o que este teste
        // prova é que a mecânica em que ela se apoia funciona de ponta a ponta.
        var alternativo = BrazilPhoneValidator.AlternateBrazilianForm(Sem9);
        alternativo.Should().Be(Com9);

        var segunda = await driver.SendAsync("+" + alternativo!, Texto, CancellationToken.None);
        await agenda.SaveContactAsync("+" + alternativo, "Fulano", CancellationToken.None);

        segunda.Sent.Should().BeTrue();
        android.Entregues.Should().ContainSingle().Which.Should().Be((Com9, Texto),
            "a pessoa recebe UMA vez, e pela forma que realmente tem conta");
        android.Agenda.Values.Select(c => c.Telefone).Should().Contain("+" + Com9,
            "a forma que funcionou precisa entrar na agenda, senão o aparelho segue sem enxergar a pessoa");
    }

    // ── A conversa errada ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deep_link_que_nao_navega_NAO_manda_na_conversa_anterior()
    {
        // 🔴 O cenário caro. `am start` devolve 0 sem o app trocar de tela (ocupado, cold start), e a
        // conversa ANTERIOR ficou com texto no campo por causa de uma tentativa abortada. Sem a
        // pré-condição de tela limpa, o poll achava o botão de enviar daquela conversa e o toque
        // entregava o rascunho ALHEIO — enquanto o resultado dizia "enviado" para o contato da vez.
        const string outroContato = "5511999998888";
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.ContasExistentes.Add(outroContato);
        android.DeixarRascunhoAberto(outroContato, "rascunho de uma tentativa anterior");
        android.NavegacoesIgnoradas = 1;

        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        android.Entregues.Should().BeEmpty(
            "nada pode sair: nem o texto da vez, nem o rascunho que estava na tela");
        envio.Sent.Should().BeFalse("não houve envio, e afirmar que houve tirava o contato da lista");
    }

    /// <summary>Digitação humana: a conversa que ficou aberta do envio anterior não pode receber a
    /// mensagem do contato da vez.</summary>
    /// <remarks>
    /// 🔴 O IRMÃO ESCONDIDO do teste acima, e mais perigoso, porque a defesa que cobre o deep link NÃO
    /// cobre este caminho. Lá o texto vai na URL: sem navegação o campo fica vazio, não há botão de
    /// enviar e o envio morre limpo. Aqui é o DRIVER que digita, então ele escreve no campo que estiver
    /// na tela, e no sucesso o driver DEIXA a conversa anterior aberta de propósito.
    ///
    /// <para>Resultado sem a proteção: o contato anterior recebe uma SEGUNDA mensagem, o da vez não
    /// recebe nada, e o console grava sucesso pro da vez. É a falha mais cara possível neste projeto,
    /// porque dobra mensagem em quem já recebeu e ninguém fica sabendo.</para>
    /// <para>E repare que a conversa anterior está SEM rascunho, que é o estado que o próprio sucesso
    /// deixa: a pré-condição de "tela sem texto pendente" passa direto por ele.</para>
    /// </remarks>
    [Fact]
    public async Task Com_digitacao_humana_abertura_que_nao_navega_NAO_digita_na_conversa_anterior()
    {
        const string outroContato = "5511999998888";
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.ContasExistentes.Add(outroContato);

        var agenda = new WhatsAppContactsReader(android);
        await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);
        var uri = await agenda.WhatsAppChatUriAsync("+" + Com9, CancellationToken.None);
        uri.Should().NotBeNull("o teste precisa exercitar o NÍVEL 1 da cascata, que é o que roda antes");

        // Conversa anterior aberta e com o campo vazio: exatamente como um envio bem-sucedido deixa.
        android.DeixarRascunhoAberto(outroContato, "");
        // Nenhuma abertura navega: o `am start` responde 0 e a tela continua na conversa de antes.
        android.NavegacoesIgnoradas = 9;

        var opts = Opts;
        opts.HumanTyping = true; // o default do produto, e o caminho sem a proteção do texto na URL
        using var driver = new WhatsAppUiDriver(android, opts);

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None, uri);

        android.Entregues.Should().BeEmpty(
            "a mensagem do contato da vez não pode sair na conversa de outra pessoa");
        envio.Sent.Should().BeFalse("e afirmar que saiu tiraria o contato da lista sem ele ter recebido");
    }

    [Fact]
    public async Task Rascunho_de_outra_conversa_nao_e_confundido_com_o_texto_da_vez()
    {
        // Mesma sujeira na tela, mas agora o deep link NAVEGA. O envio tem que sair certo, na conversa
        // certa, e o rascunho do outro tem que continuar guardado sem ter sido enviado.
        const string outroContato = "5511999998888";
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.ContasExistentes.Add(outroContato);
        android.DeixarRascunhoAberto(outroContato, "rascunho alheio");

        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        envio.Sent.Should().BeTrue();
        android.Entregues.Should().ContainSingle().Which.Should().Be((Com9, Texto));
        android.RascunhoDe(outroContato).Should().Be("rascunho alheio",
            "sair da conversa guarda o rascunho; ele não pode nem sumir nem ser enviado");
    }

    // ── Quando a tela não pode ser lida ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Tela_ilegivel_depois_do_toque_admite_que_nao_sabe()
    {
        // A mensagem SAI, e logo depois a tela fica ilegível. O driver não pode dizer nem "enviei" nem
        // "não enviei": tem que dizer que não sabe, porque quem chama reage a "não enviei" reabrindo a
        // conversa na outra forma do número — e aí a pessoa recebe duas vezes.
        var android = new AndroidFalso
        {
            DumpsFalhando = 30,
            FalharDumpApenasAposEnvio = true,
        };
        android.ContasExistentes.Add(Com9);
        var (driver, _) = Montar(android);
        using var __ = driver;

        var envio = await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);

        android.Entregues.Should().ContainSingle().Which.Should().Be((Com9, Texto),
            "o aparelho recebeu de verdade; o que faltou foi conseguir confirmar");
        envio.Sent.Should().BeFalse();
        envio.Uncertain.Should().BeTrue(
            "esta é a diferença que impede a segunda mensagem para quem já recebeu a primeira");
    }

    [Fact]
    public async Task Dump_nao_reaproveita_leitura_antiga()
    {
        // O arquivo do dump é fixo. Se o `uiautomator` falhar sem sobrescrever, o `cat` devolveria a
        // tela de ANTES passando por tela de agora — e é sobre ela que se decide onde tocar.
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        var (driver, _) = Montar(android);
        using var __ = driver;

        await driver.SendAsync("+" + Com9, Texto, CancellationToken.None);
        android.DumpsFalhando = 99;

        var tela = await driver.DumpUiAsync(CancellationToken.None);

        tela.Should().BeNull("sem leitura nova, a resposta é \"não sei\", nunca a leitura anterior");
    }

    // ── A agenda sob concorrência ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_escrevendo_junto_nao_faz_gravar_no_contato_de_outra_pessoa()
    {
        // 🔴 Gravar um contato é o que ACORDA o escritor concorrente: o WhatsApp publica o espelho dele
        // em raw contacts da conta com.whatsapp, e a conta Google sincroniza. Se ele inserir na janela
        // entre o nosso insert e a leitura, o maior _id passa a ser de outra pessoa.
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        AndroidFalso.RawContact? doSync = null;
        android.AoCriarRawContact = () =>
        {
            android.AoCriarRawContact = null; // uma vez só
            // Entra DEPOIS do nosso, então passa a ser o maior _id — o que a regra antiga escolheria.
            doSync = android.CriarContatoDeTerceiro("Maria da Silva", "+5511988887777");
        };

        var (_, agenda) = Montar(android);
        var r = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);

        // 🔴 O ESTADO DO APARELHO PRIMEIRO. É aqui que mora o estrago, e a mensagem devolvida é só a
        // explicação dele: com a regra antiga isto devolvia "ok" com o contato da Maria renomeado pra
        // "Fulano" e o telefone do alvo colado nela, tudo sincronizando pra conta Google.
        doSync.Should().NotBeNull();
        doSync!.Nome.Should().Be("Maria da Silva", "o contato de outra pessoa não pode ser renomeado");
        doSync.Telefone.Should().Be("+5511988887777", "nem receber o telefone do nosso alvo");
        android.Agenda.Values.Should().NotContain(c => c.Telefone == "+" + Com9,
            "nada foi gravado: repetir é barato, escrever no contato errado não tem desfazer");

        r.Should().NotStartWith("ok");
        r.Should().Contain("mudou embaixo de mim");
    }

    [Fact]
    public async Task Gravar_e_idempotente_e_repetir_depois_da_corrida_funciona()
    {
        // A recusa acima é transitória por construção. O que o operador (e a segunda passada do
        // `gravar`) faz é repetir, então repetir precisa dar certo.
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.AoCriarRawContact = () =>
        {
            android.AoCriarRawContact = null;
            android.CriarContatoDeTerceiro("Contato do sync", "+5511977776666");
        };
        var (_, agenda) = Montar(android);

        var primeira = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);
        var segunda = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);
        var terceira = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);

        primeira.Should().Contain("mudou embaixo de mim");
        segunda.Should().Be("ok");
        terceira.Should().Be("já existe", "gravar de novo não pode duplicar");
        android.Agenda.Values.Count(c => c.Telefone == "+" + Com9).Should().Be(1);
    }

    [Fact]
    public async Task Contato_gravado_so_com_digitos_crus_e_curado()
    {
        // 🔴 Registro envenenado de antes da correção do E.164: existe pro Android e é invisível pro
        // WhatsApp, que passa a responder "sem conta" pra uma pessoa que existe.
        //
        // Este é o teste que o phone_lookup nunca poderia satisfazer. Ele compara sequências de
        // dígitos, entao "achei o contato" vem igual para as duas formas de gravação, e a decisão de
        // curar tem que sair do VALOR ARMAZENADO. Com a lógica antiga isto respondia "já existe" e a
        // pessoa seguia inalcançável pelo aparelho.
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.CriarContatoDeTerceiro("Fulano", Com9); // gravado SEM o "+"
        var (_, agenda) = Montar(android);

        var r = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);

        android.Agenda.Values.Select(c => c.Telefone).Should().Contain("+" + Com9,
            "a forma que o WhatsApp resolve tem que passar a existir, ao lado da envenenada");
        r.Should().StartWith("ok", "curar é sucesso; o console conta o resultado por StartsWith(\"ok\")");
        r.Should().Contain("sem o +");
    }

    [Fact]
    public async Task Contato_ja_gravado_em_E164_nao_duplica()
    {
        // O outro lado do teste acima: com a forma boa ja presente, nao ha o que curar nem o que criar.
        var android = new AndroidFalso();
        android.ContasExistentes.Add(Com9);
        android.CriarContatoDeTerceiro("Fulano", "+" + Com9);
        var (_, agenda) = Montar(android);

        var r = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);

        r.Should().Be("já existe");
        android.Agenda.Should().ContainSingle("gravar de novo nao pode criar um segundo registro");
    }

    [Fact]
    public async Task Forma_do_registro_ilegivel_nao_vira_palpite()
    {
        // O contato existe e nao deu pra ver COMO esta gravado. Criar as cegas duplica; dizer "ja
        // existe" pode estar escondendo um registro envenenado. Sem o dado, nenhuma das duas se
        // sustenta, entao o certo e nao fazer nada e dizer por que.
        var android = new AndroidFalso { FalharLeituraDeDados = true };
        android.ContasExistentes.Add(Com9);
        android.CriarContatoDeTerceiro("Fulano", "+" + Com9);
        var (_, agenda) = Montar(android);

        var r = await agenda.SaveContactAsync("+" + Com9, "Fulano", CancellationToken.None);

        android.Agenda.Should().ContainSingle("nada pode ser criado sem saber o que ja esta la");
        r.Should().NotStartWith("ok");
        r.Should().Contain("não consegui ler em que forma");
    }

    // ── Fluxo do lote, com o aparelho no meio ────────────────────────────────────────────────────

    [Fact]
    public async Task Lote_de_tres_entrega_um_por_contato_e_a_falha_nao_contamina_os_seguintes()
    {
        // O modo de falha relatado operando em 2026-08-05: uma falha isolada deixava o diálogo na tela
        // e o contato SEGUINTE abria atrás dele, falhando igual. Uma falha virava uma sequência.
        const string bom1 = "5584998420730";
        const string morto = "5584998420731";
        const string bom2 = "5511999998888";

        var android = new AndroidFalso();
        android.ContasExistentes.Add(bom1);
        android.ContasExistentes.Add(bom2);
        var (driver, _) = Montar(android);
        using var __ = driver;

        var resultados = new List<WhatsAppSendResult>();
        foreach (var numero in new[] { bom1, morto, bom2 })
        {
            resultados.Add(await driver.SendAsync("+" + numero, Texto, CancellationToken.None));
        }

        resultados[0].Sent.Should().BeTrue();
        resultados[1].Sent.Should().BeFalse("o número não tem conta");
        resultados[2].Sent.Should().BeTrue("a falha do anterior não pode derrubar este");

        android.Entregues.Should().HaveCount(2);
        android.Entregues.Should().BeEquivalentTo([(bom1, Texto), (bom2, Texto)]);
    }
}
