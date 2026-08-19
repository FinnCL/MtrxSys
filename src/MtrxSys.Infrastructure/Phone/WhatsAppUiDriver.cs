using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Envia mensagem pela UI do WhatsApp de um aparelho Android, virtual ou físico.</summary>
/// <remarks>
/// <para>Espelha o caminho que roda em produção no <see cref="DockerCliPhoneOrchestrator"/>, com uma
/// diferença: o transporte entra por <see cref="IAdbRunner"/> em vez de `docker exec` fixo. Os
/// resource-ids, a ordem dos passos e os critérios de confirmação são os mesmos — foram medidos e cada
/// um custou um bug.</para>
/// <para>✅ VALIDADO no físico em 2026-07-29 (Galaxy A14, Android 15, WhatsApp 2.26.27.85, SEM root):
/// deep link abriu `com.whatsapp/.Conversation`, `uiautomator dump` funcionou, `id/entry` e `id/send`
/// existem com os mesmos nomes, o toque enviou e a entrega foi lida como "Entregue".</para>
/// <para>⚠️ DUPLICAÇÃO TEMPORÁRIA e consciente: o orquestrador do emulador segue com a cópia dele. A
/// desduplicação é passo separado — ver docs/engine-physical.md, Fase 1.</para>
/// </remarks>
internal sealed class WhatsAppUiDriver(IAdbRunner adb, PhoneOptions opts) : IDisposable
{
    private readonly IAdbRunner _adb = adb;
    private readonly PhoneOptions _opts = opts;

    // Um envio por vez por aparelho: `uiautomator dump` não roda concorrente e grava num arquivo fixo.
    private readonly SemaphoreSlim _uiLock = new(1, 1);

    // Caminho do dump. Fixo de propósito (o lock acima serializa), mas próprio do MtrxSys pra não
    // colidir com um dump que alguém tenha deixado em /sdcard/window_dump.xml.
    private const string DumpPath = "/sdcard/mtrx_ui.xml";

    public void Dispose() => _uiLock.Dispose();

    // ── Envio ────────────────────────────────────────────────────────────────────────────────────

    public async Task<WhatsAppSendResult> SendAsync(
        string phoneE164, string text, CancellationToken ct, string? chatUri = null)
    {
        var digits = new string([.. (phoneE164 ?? string.Empty).Where(char.IsDigit)]);
        if (digits.Length < 8)
        {
            return WhatsAppSendResult.Fail(FalhaCausa.EntradaInvalida, "phone inválido");
        }
        // Normaliza a quebra de linha ANTES de qualquer medida: o \r do Windows não é digitável e
        // entraria na contagem de caracteres que confere o campo antes de enviar.
        text = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
                                     .Replace('\r', '\n');
        if (text.Length == 0)
        {
            return WhatsAppSendResult.Fail(FalhaCausa.EntradaInvalida, "texto vazio");
        }

        // Sem digitação humana: o deep link entrega a mensagem pronta no campo e só resta tocar enviar.
        var resultado = !_opts.HumanTyping
            ? await SendByDeepLinkAsync(digits, text, ct)
            : await SendByTypingAsync(digits, text, chatUri, ct);

        // 🔴 FALHA DEIXA A TELA COMO ESTAVA, e "como estava" costuma ser um DIÁLOGO do WhatsApp — o
        // "o número não está no WhatsApp" que aparece quando o deep link aponta pra quem não tem conta.
        // Ele fica por cima de tudo, então o PRÓXIMO contato do lote abre atrás do diálogo e falha
        // igual: uma falha isolada virava uma sequência de falhas, e o operador via o robô "travado"
        // sem entender por quê. Relatado operando em 2026-08-05.
        // Só no caminho de falha: no sucesso a conversa aberta é estado legítimo e mexer nela seria
        // interferência à toa.
        if (!resultado.Sent)
        {
            await DispensarDialogoAsync(ct);
        }
        return resultado;
    }

    /// <summary>Por que o campo de envio não apareceu: lê a tela e distingue "o WhatsApp respondeu que
    /// este número não tem conta" de "a tela não respondeu".</summary>
    /// <remarks>
    /// 🔴 A mensagem antiga era "botão enviar não apareceu (o chat não abriu ou o texto não
    /// preencheu)" — um OU que juntava duas causas com consertos opostos: uma é qualidade da lista,
    /// a outra é o aparelho. O operador passou horas em 2026-08-05 sem conseguir distinguir, tentando
    /// o mesmo número várias vezes porque o erro não dizia que o próprio WhatsApp já tinha respondido.
    ///
    /// <para>A resposta está NA TELA: quando o número não tem conta, o app mostra um diálogo dizendo
    /// isso. Basta ler. Casar por TRECHO e em vários idiomas porque o texto exato muda com a versão e
    /// com o idioma do aparelho, e um casamento exato quebraria em silêncio na próxima atualização.</para>
    ///
    /// <para>Na dúvida devolve a mensagem genérica: afirmar a causa errada é pior que admitir que não
    /// se sabe — foi justamente o que custou as horas acima.</para>
    /// </remarks>
    /// <returns>O veredito e o XML que o produziu. O XML sai junto porque quem chama precisa da MESMA
    /// tela para tentar desbloqueá-la: dumpar de novo custaria outro processo `adb` e, pior, poderia
    /// ler uma tela DIFERENTE — o diagnóstico falaria de um aviso e a evidência guardada, de outro.</returns>
    private async Task<(WhatsAppSendResult Veredito, string? Xml)> DiagnosticarChatNaoAbertoAsync(
        CancellationToken ct)
    {
        var generico = WhatsAppSendResult.Fail(
            FalhaCausa.ConversaNaoAbriu,
            "botão enviar não apareceu (o chat não abriu ou o texto não preencheu).");
        var xml = await DumpUiAsync(ct);
        if (string.IsNullOrWhiteSpace(xml))
        {
            // Sem leitura de tela não dá pra afirmar que a culpa é do número, e o palpite errado aqui
            // faz o lote seguir em frente contra um aparelho travado. Na dúvida, é falha do aparelho.
            return (generico, null);
        }

        // 🔴 A CONTA DECLARADA RESTRITA VEM PRIMEIRO, antes de qualquer outra leitura. É a única
        // condição de CERTEZA sobre o chip que existe aqui, e ela explica TODAS as outras: com a conta
        // restrita o campo de mensagem some, o botão de enviar não aparece, e o número parece morto.
        // Diagnosticar como "sem conta" ou "aparelho travado" nesse estado é culpar o inocente.
        if (ContaRestrita(xml))
        {
            var telaSalva = GuardarTela(xml);
            return (WhatsAppSendResult.Restringida(
                "o WhatsApp declarou NA TELA que ESTA CONTA está restringida. Não é o número, não é o "
                + "aparelho: é o chip. Enquanto durar, nenhuma mensagem sai para ninguém, e insistir é "
                + "o que transforma restrição temporária em banimento."
                + (telaSalva is null ? "" : $" Tela salva em {telaSalva}.")), xml);
        }

        // Aparelho DORMINDO é a causa mais provável num lote sem ninguém por perto: a tela apaga em
        // ~10 min e a pausa entre blocos é maior que isso. O driver acorda antes de cada envio, então
        // chegar aqui bloqueado significa cadeado COM SENHA, que nenhum comando resolve.
        if (TelaBloqueada(xml))
        {
            return (WhatsAppSendResult.Fail(
                FalhaCausa.TelaBloqueada,
                "o aparelho está com a TELA BLOQUEADA e o desbloqueio automático não deu conta, o que "
                + "indica PIN, padrão ou biometria. Não é o número nem a conversa. No aparelho: tire o "
                + "bloqueio de tela e ligue \"Permanecer ativo\" nas Opções do desenvolvedor (ou rode "
                + "tools\\preparar-aparelho.cmd, que já faz isso)."), xml);
        }

        if (!NumeroSemConta(xml))
        {
            return (generico, xml);
        }

        // 🔴 GUARDA A TELA TAMBÉM AQUI, e não só no caminho da tela desconhecida.
        //
        // Este veredito é o mais FORTE que o driver emite: ele encerra o contato, culpa o número e
        // manda o operador conferir a lista. E ele nasce de uma BUSCA POR TEXTO — o NumeroSemConta
        // registra ali por que uma pista mal escolhida afirma a causa errada com confiança.
        //
        // Descartar a tela deixava a afirmação INAUDITÁVEL: um lote inteiro podia sair marcando gente
        // boa como inexistente e não haveria com que discordar. MEDIDO operando em 2026-08-10: dois
        // contatos seguidos, ambos JÁ NA AGENDA do aparelho, negados nas DUAS formas do número. Pode
        // ser lista ruim, pode ser cache envenenado do app, pode ser falso positivo daqui. Sem a tela
        // não dá pra saber qual, e as três pedem consertos diferentes.
        //
        // A dedup por conteúdo faz 87 diálogos iguais virarem UM arquivo, então isto não enche disco.
        var arquivo = GuardarTela(xml);
        return (WhatsAppSendResult.NoAccount(
            "o WhatsApp respondeu que ESTE NÚMERO não tem conta. Não é o aparelho nem a conexão: "
            + "é a forma do número ou o contato mesmo. Se o contato existe, confira o 9º dígito."
            + (arquivo is null ? "" : $" Tela salva em {arquivo}.")), xml);
    }

    /// <summary>Tenta tirar da frente um aviso que está por cima da conversa, para a mensagem poder
    /// sair NESTE contato em vez de só no próximo.</summary>
    /// <remarks>
    /// 🔴 O CASO QUE MOTIVOU: conversa com MENSAGENS TEMPORÁRIAS ligadas abre uma tela informativa por
    /// cima. Enquanto ela está lá, nem o campo de mensagem nem o botão de enviar existem na árvore, e
    /// o envio falhava com "a conversa não abriu" — diagnóstico que aponta para o aparelho quando o
    /// aparelho está perfeito. Pior: o contato ia para o balde de falha de APARELHO, que é o contador
    /// que dispara o alerta de celular travado.
    ///
    /// <para>Já existia recuperação para isso, o <see cref="DispensarDialogoAsync"/>, mas ela roda
    /// DEPOIS do resultado: limpa a tela para o PRÓXIMO contato e perde o atual. Aqui a limpeza
    /// acontece antes de desistir, e o envio segue.</para>
    ///
    /// <para>Duas saídas, nessa ordem: um botão de rótulo neutro (ver
    /// <see cref="BotoesQueSoFecham"/>) e, se não houver, um BACK. O BACK é o plano B porque o aviso
    /// pode ser tela cheia sem botão reconhecível, e porque ele não confirma nada — no pior caso sai
    /// da conversa, e a conversa é reaberta por deep link no próximo contato de qualquer forma.</para>
    ///
    /// <para>⚠️ OS RÓTULOS NÃO FORAM MEDIDOS EM APARELHO. Vieram do que o app usa em avisos desse tipo,
    /// não de um dump real da tela de mensagens temporárias. É por isso que o caminho de desistência
    /// GUARDA A TELA (ver <see cref="DescreverTelaDesconhecidaAsync"/>) em vez de só devolver falha:
    /// esperar alguém estar presente no momento certo pra rodar o dump é esperar por uma coincidência,
    /// e o disparo roda justamente sem ninguém por perto.</para>
    /// </remarks>
    /// <param name="xml">A tela JÁ LIDA por quem chama. Não dumpa de novo: seria outro processo `adb` no
    /// caminho de falha e, pior, poderia ler uma tela diferente da que foi diagnosticada.</param>
    /// <returns>null quando reconheceu e fechou o aviso; caso contrário, o que dava pra apurar da tela,
    /// pra quem chama anexar à falha.</returns>
    private async Task<string?> TentarDesbloquearTelaAsync(string? xml, CancellationToken ct)
    {
        if (xml is not null)
        {
            // Aqui NÃO se exige botão de verdade nem aviso por cima, ao contrário do
            // FecharAvisoSobreAConversaAsync: um aviso em tela cheia pode muito bem fechar por um
            // TextView clicável, e exigir `class` de botão perderia esse caso.
            //
            // ⚠️ O QUE SEGURA ISSO É A PRÉ-CONDIÇÃO DE QUEM CHAMA: só se chega aqui com o diagnóstico
            // GENÉRICO, ou seja, sem campo de mensagem E sem a tela ter se explicado. A conta
            // restringida, que é o caso em que a lista de mensagens fica à mostra sem o campo, sai
            // antes e não passa por aqui. Sobra um resíduo conhecido: tela que esconde o campo e
            // mantém as mensagens por outro motivo (removido do grupo, canal só de leitura). Fechar
            // esse resíduo pede um resource-id da lista de mensagens MEDIDO em aparelho, e id chutado
            // aqui é o mesmo erro que este arquivo já pagou duas vezes.
            foreach (var (rotulo, centro) in Clicaveis(xml))
            {
                if (centro is { } ponto && RotuloSoFecha(rotulo))
                {
                    await TapAsync(ponto, ct);
                    await Task.Delay(600, ct);
                    return null;
                }
            }
        }

        // Nenhum rótulo conhecido: registra a tela ANTES do BACK, que é o que a faz sumir.
        var evidencia = xml is null ? null : DescreverTelaDesconhecida(xml);
        await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
        await Task.Delay(600, ct);
        return evidencia;
    }

    /// <summary>Fecha um aviso que o WhatsApp abre POR CIMA de uma conversa que JÁ abriu, pra o toque
    /// voltar a chegar no campo de mensagem.</summary>
    /// <remarks>
    /// 🔴 O CASO, relatado operando em 2026-08-18: conversa com MENSAGENS TEMPORÁRIAS ligadas mostra um
    /// aviso com "OK" na frente do chat. Enquanto ele está lá o toque morre nele, a digitação não entra
    /// em lugar nenhum e o contato falha.
    ///
    /// <para>Já existia recuperação pra aviso na frente da conversa, o
    /// <see cref="TentarDesbloquearTelaAsync"/>. Ela nunca alcançava ESTE caso porque só roda quando o
    /// campo de mensagem NÃO foi encontrado, e este aviso não tira o campo da árvore: o
    /// <c>uiautomator dump</c> traz a Activity inteira, então o <c>id/entry</c> continua lá atrás. O
    /// driver conclui que a conversa abriu, digita contra um toque bloqueado, e a falha chega ao
    /// console como "campo ficou com nada caracteres", que culpa o teclado.</para>
    ///
    /// <para>⚠️ SÓ TOCA EM BOTÃO DE VERDADE (ver <see cref="EhBotao"/>). Rótulo neutro não basta com a
    /// conversa aberta: a bolha de mensagem é clicável e o rótulo dela é o texto da pessoa, então
    /// alguém que respondeu "ok" viraria alvo de toque.</para>
    ///
    /// <para>⚠️ E NÃO TEM BACK COMO PLANO B, também ao contrário do outro método. Lá a conversa já tinha
    /// falhado e o BACK não custava nada. Aqui ela está aberta e na esmagadora maioria das vezes
    /// funcionando: um BACK disparado por engano sairia dela e quebraria um envio que ia dar certo. Sem
    /// botão reconhecido, não faz nada e o caminho de sempre segue.</para>
    ///
    /// <para>⚠️ SÓ CHAMAR COM O CAMPO (ou o botão de enviar) JÁ ENCONTRADO. Com a conversa fechada, o
    /// que está na tela pode ser o diálogo de "este número não está no WhatsApp", que também tem um
    /// "OK": fechá-lo aqui apagaria a prova de que a causa é o NÚMERO, e a falha voltaria a ser
    /// classificada como problema de aparelho. Esse caso é do
    /// <see cref="DiagnosticarChatNaoAbertoAsync"/>, que lê antes de dispensar.</para>
    /// </remarks>
    /// <returns>true quando tocou em algo: a tela mudou e as coordenadas de antes viraram pó.</returns>
    /// <param name="telaJaLida">A tela que quem chama ACABOU de ler, quando existe. A primeira volta
    /// usa ela em vez de dumpar de novo: seria outro processo `adb` no caminho mais repetido do driver,
    /// e uma tela diferente daquela em que o campo foi encontrado.</param>
    private async Task<bool> FecharAvisoSobreAConversaAsync(string? telaJaLida, CancellationToken ct)
    {
        var fechouAlgum = false;
        var tela = telaJaLida;
        // 🔴 MAIS DE UMA VOLTA, mas com teto. Os avisos do WhatsApp vêm EMPILHADOS (mensagens
        // temporárias e, atrás dele, o de criptografia ou o de conta comercial), e um deles pode entrar
        // na tela um instante DEPOIS de o campo aparecer, ou seja, depois da primeira leitura. Uma
        // passada só fechava o primeiro e deixava o lote parado no segundo, com o mesmo sintoma.
        //
        // O teto existe porque um aviso que NÃO fecha pelo próprio botão giraria em laço dentro do
        // envio. Três voltas e segue: o que sobrar vira falha COM a tela guardada, que é o que ensina
        // o rótulo novo.
        for (var volta = 0; volta < MaxAvisosPorEnvio; volta++)
        {
            tela ??= await DumpUiAsync(ct);
            if (tela is not { } xml || LerAvisoSobreOCampo(xml) is not ({ }, { } ponto))
            {
                return fechouAlgum;
            }

            // 🔴 TELA QUE JÁ SE EXPLICOU NÃO SE FECHA. O diálogo de "não está no WhatsApp" e a tela de
            // conta restringida também são outra janela do app, e o primeiro tem um botão OK. A regra
            // de só rodar com o campo encontrado deveria bastar, mas ela supõe que o campo achado é o
            // da conversa PEDIDA — e quando o aparelho ignora os BACK do
            // SairDaConversaAnteriorAsync, o campo na tela é o da conversa ANTERIOR e o diálogo está
            // por cima dela. Fechar aqui apagaria a única prova de que a causa é o NÚMERO ou o CHIP, e
            // esses dois vereditos valem mais que fechar um aviso: um tira o contato da lista, o outro
            // PARA o lote. Na dúvida não fecha, e o diagnóstico lê a tela intacta logo adiante.
            if (TelaJaSeExplicou(xml))
            {
                return fechouAlgum;
            }

            // Guarda a tela ANTES do toque. É o dump REAL do aviso, o dado que faltava pra manter
            // BotoesQueSoFecham medida em vez de adivinhada, e a única prova de que o aviso existiu:
            // depois do toque ele não volta pro mesmo contato.
            GuardarAvisoAberto(xml);
            await TapAsync(ponto, ct);
            await Task.Delay(600, ct);
            fechouAlgum = true;
            tela = null; // o toque mexeu na tela: a volta seguinte tem que ler de novo
        }
        return fechouAlgum;
    }

    /// <summary>Fecha o que estiver na frente e reencontra o alvo, porque a tela mexeu.</summary>
    /// <remarks>
    /// Os dois caminhos de envio faziam exatamente isto, um com o campo de mensagem e o outro com o
    /// botão de enviar. Eram cinco linhas iguais em dois lugares, e o risco de duas cópias não é o
    /// tamanho: é uma delas receber o próximo ajuste sozinha, e o mesmo defeito real passar a sair no
    /// relatório em dois baldes conforme um toggle de configuração.
    /// </remarks>
    private async Task<(int X, int Y)?> ReencontrarSemAvisoAsync(
        Regex alvo, (int X, int Y)? achado, string? tela, CancellationToken ct)
    {
        if (achado is null || !await FecharAvisoSobreAConversaAsync(tela, ct))
        {
            return achado;
        }
        // As coordenadas de antes viraram pó: com o aviso fora, a conversa reflui.
        return await PollNodeCenterAsync(alvo, Math.Min(_opts.WhatsAppOpenWaitMs, 3000), ct);
    }

    /// <summary>Quantos avisos empilhados o driver tenta fechar antes de desistir e falhar com a tela
    /// guardada.</summary>
    private const int MaxAvisosPorEnvio = 3;

    /// <summary>Guarda a tela de um aviso que FOI fechado, e só as primeiras deste processo.</summary>
    /// <remarks>
    /// 🔴 O TETO EXISTE PORQUE A DEDUP NÃO SALVA ESTE CASO. <see cref="GuardarTela"/> deduplica pelo
    /// hash do XML INTEIRO, e o XML inteiro inclui a conversa que está atrás do aviso — que muda a cada
    /// contato. Ou seja: o mesmo aviso, nos 87 contatos do lote, grava 87 arquivos diferentes, e a
    /// rotação (<see cref="PhoneOptions.UiDumpKeep"/>, 20 por padrão) apaga as telas mais antigas para
    /// caberem. As mais antigas são justamente as telas DESCONHECIDAS, que são o dado raro: aviso
    /// fechado com sucesso a gente já sabe fechar.
    ///
    /// <para>Duas cópias bastam pro que esta captura serve: confirmar o rótulo do botão e as medidas da
    /// tela. A partir daí ela só empurra dado melhor pra fora da pasta.</para>
    /// </remarks>
    private void GuardarAvisoAberto(string xml)
    {
        if (_avisosGuardados >= MaxAvisosGuardados)
        {
            return;
        }
        _avisosGuardados++;
        GuardarTela(xml);
    }

    /// <inheritdoc cref="GuardarAvisoAberto"/>
    private int _avisosGuardados;

    /// <inheritdoc cref="GuardarAvisoAberto"/>
    private const int MaxAvisosGuardados = 2;

    /// <summary>Lê a tela procurando um aviso na frente do campo de mensagem: se tem alguma coisa
    /// cobrindo o campo, e qual botão fecharia ela.</summary>
    /// <remarks>
    /// 🔴 O PERIGO QUE ESTE MÉTODO EXISTE PRA EVITAR não é o aviso: é o toque errado. Dentro de uma
    /// conversa existem botões de VERDADE que não fecham nada. Mensagem de template traz botões de
    /// RESPOSTA RÁPIDA, e o rótulo deles é escolhido por quem mandou: "OK" e "Continuar" são os mais
    /// comuns que existem. Tocar num deles MANDA UMA MENSAGEM para o contato. Seria o disparo
    /// escrevendo por conta própria na conversa de outra pessoa, irreversível, e sem ninguém por perto.
    ///
    /// <para>Por isso o rótulo neutro e a `class` de botão NÃO BASTAM. Exige-se também a prova de que
    /// existe um aviso, e ela sai da JANELA (ver abaixo). Botão de resposta rápida mora DENTRO da lista
    /// de mensagens, na MESMA janela da conversa e antes do campo: nenhuma das duas fontes o alcança.</para>
    ///
    /// <para>🔴 A PROVA SAI DA JANELA, E NÃO DA ORDEM DOS NÓS. Diálogo e bottom sheet são OUTRA JANELA
    /// do app, e o `uiautomator dump` concatena as janelas da tela: os filhos diretos de
    /// <c>&lt;hierarchy&gt;</c> são as RAÍZES delas (ver <see cref="RaizesDeJanela"/>). A primeira
    /// versão disto olhava só o trecho POSTERIOR ao campo, apoiada em "quem desenha por cima aparece
    /// depois" — o que vale DENTRO de uma janela, onde o dump é varredura em profundidade, e NÃO entre
    /// janelas, cuja ordem depende de qual delas está ativa. O `uiautomator` serializa a janela ATIVA
    /// primeiro, e a janela ativa é justamente o modal: pela regra antiga a varredura nunca o
    /// alcançava, e o fechamento era um no-op silencioso no caso exato que o motivou. Pior, um falso
    /// que emitisse o aviso depois da conversa daria o teste por verde.</para>
    ///
    /// <para>Duas fontes, e a ordem entre janelas não decide nenhuma:</para>
    /// <para>A) OUTRA RAIZ, com o mesmo <c>package</c> do campo e sem campo próprio. É um diálogo,
    /// bottom sheet ou popup do WhatsApp, e uma janela dessas é modal: o toque não chega no campo nem
    /// quando ela NÃO o cobre geometricamente, que é o caso do diálogo centralizado com o campo no
    /// rodapé. A regra velha, que exigia cobertura do centro do campo, errava esse caso por inteiro.
    /// O tamanho mínimo tira popup miúdo do caminho; a exigência de não ter <c>id/entry</c> próprio
    /// evita confundir com a PRÓPRIA conversa, caso o dump liste a mesma janela duas vezes.</para>
    /// <para>B) MESMA raiz do campo, nó POSTERIOR a ele cobrindo o centro do campo. É o aviso desenhado
    /// dentro da própria conversa (bottom sheet de layout, scrim). Aqui a ordem vale, porque é ordem
    /// DENTRO de uma janela, e é ela que exclui os ancestrais do campo: pai vem antes do filho.</para>
    ///
    /// <para>⚠️ O BOTÃO TEM QUE SER DO AVISO. Ele é procurado DENTRO dele: dentro da raiz, no caso A;
    /// dentro do retângulo do nó que cobre, no caso B. As duas perguntas eram independentes antes, e
    /// isso deixava o argumento de segurança valendo por coincidência: bastavam algo cobrindo o campo
    /// e um "OK" em qualquer canto da árvore para o toque sair, sem que um tivesse relação com o outro.</para>
    ///
    /// <para>⚠️ CONSEQUÊNCIA ACEITA: aviso cujo botão não é reconhecível não é fechado. Isso é falha COM
    /// tela guardada e rótulos no erro (ver <see cref="EvidenciaDeCampoQueNaoRecebeu"/>), que é
    /// recuperável na próxima versão. Um toque errado numa conversa real não é.</para>
    /// </remarks>
    /// <returns><c>Motivo</c>: a frase que descreve o que está na frente da conversa, ou null quando
    /// não há nada. <c>BotaoQueFecha</c>: onde tocar, quando dá pra reconhecer. Só é seguro tocar com
    /// os DOIS presentes, e é por isso que saem juntos: são uma leitura só da mesma tela, e separá-las
    /// deixaria o diagnóstico afirmando uma coisa enquanto a ação decide por outra regra.</returns>
    private static (string? Motivo, (int X, int Y)? BotaoQueFecha) LerAvisoSobreOCampo(string xml)
    {
        // Sem campo na tela não é este o caso: quem trata é o TentarDesbloquearTelaAsync.
        if (EntryNodeRx.Match(xml) is not { Success: true } campo
            || RetanguloDe(campo.Value) is not { } r)
        {
            return (null, null);
        }
        // O dobro da área do campo, e o dobro não é preciosismo: sem ele um filho do próprio campo (a
        // dica de texto) contaria como aviso no caso B, porque filho também vem depois do pai. Painel,
        // scrim e janela de diálogo são grandes por natureza; o que pertence ao campo, não.
        var areaMinima = Math.Max(1L, AreaDe(r)) * 2;

        var raizes = RaizesDeJanela(xml);
        var daConversa = raizes.FindIndex(z => campo.Index >= z.Start && campo.Index < z.End);
        var pacote = AtributoDe(PacoteRx, campo.Value);

        // O primeiro aviso VISTO, que nem sempre é o primeiro com botão reconhecível. Guardar o motivo
        // e seguir procurando é o que faz avisos EMPILHADOS funcionarem: parar na primeira janela
        // candidata devolvia "tem aviso, não sei fechar" mesmo com o botão à mão na janela seguinte.
        string? motivo = null;

        // ── A: outra JANELA do próprio app ──
        // Sem package legível não dá pra afirmar que a outra janela é do app, e afirmar errado aqui
        // autoriza um toque. Nesse caso sobra o caso B, que não depende disso.
        if (daConversa >= 0 && pacote.Length > 0)
        {
            for (var i = 0; i < raizes.Count; i++)
            {
                var (inicio, fim) = raizes[i];
                if (i == daConversa
                    || NoRx.Match(xml, inicio) is not { Success: true } cabeca
                    || AtributoDe(PacoteRx, cabeca.Value) != pacote
                    || RetanguloDe(cabeca.Value) is not { } janela
                    || AreaDe(janela) < areaMinima
                    || TemNoTrecho(EntryNodeRx, xml, inicio, fim))
                {
                    continue;
                }
                motivo ??= AvisoEmOutraJanela;
                if (BotaoQueFechaEm(xml, inicio, fim, null) is { } botao)
                {
                    return (AvisoEmOutraJanela, botao);
                }
            }
        }

        // ── B: mesma janela, nó posterior ao campo ──
        var limite = daConversa >= 0 ? raizes[daConversa].End : xml.Length;
        var centroX = (r.X1 + r.X2) / 2;
        var centroY = (r.Y1 + r.Y2) / 2;
        for (var no = NoRx.Match(xml, campo.Index + campo.Length);
             no.Success && no.Index < limite;
             no = no.NextMatch())
        {
            if (RetanguloDe(no.Value) is not { } c || !Cobre(c, centroX, centroY, areaMinima))
            {
                continue;
            }
            motivo ??= AvisoCobrindoOCampo;
            if (BotaoQueFechaEm(xml, no.Index, limite, c) is { } botao)
            {
                return (AvisoCobrindoOCampo, botao);
            }
        }
        return (motivo, null);
    }

    /// <inheritdoc cref="LerAvisoSobreOCampo"/>
    private const string AvisoEmOutraJanela =
        " Havia OUTRA JANELA do WhatsApp na frente da conversa (um aviso ou diálogo do app): o texto "
        + "não entrou porque o toque não chegou no campo, e não porque o teclado falhou.";

    /// <inheritdoc cref="LerAvisoSobreOCampo"/>
    private const string AvisoCobrindoOCampo =
        " Havia ALGO POR CIMA do campo de mensagem: o texto não entrou porque o toque não chegou "
        + "no campo, e não porque o teclado falhou.";

    /// <summary>As RAÍZES DE JANELA do dump: onde cada uma começa e termina no XML.</summary>
    /// <remarks>
    /// 🔴 O `uiautomator dump` não despeja UMA árvore: ele despeja as janelas da tela, uma atrás da
    /// outra, como filhas diretas de <c>&lt;hierarchy&gt;</c>. A conversa é uma; o diálogo por cima
    /// dela é outra; a barra de status e o teclado são outras. Saber onde cada uma começa é o que
    /// permite perguntar "o campo e este botão estão na MESMA janela?", que é a pergunta que separa um
    /// aviso do conteúdo da conversa — e é a única que não depende da ordem em que elas saíram.
    /// <para>Conta profundidade em vez de casar aninhamento: <c>&lt;node ...&gt;</c> abre,
    /// <c>&lt;/node&gt;</c> fecha e <c>&lt;node ... /&gt;</c> é folha. Regex não faz aninhamento, e um
    /// parser de XML de verdade custaria caro no caminho mais repetido do driver, para responder uma
    /// pergunta que a contagem já responde.</para>
    /// <para>Árvore truncada (dump cortado no meio) devolve o resto como uma raiz só: sem isso o trecho
    /// final ficaria fora de qualquer janela e o campo pareceria não ter janela nenhuma.</para>
    /// <para>Percorre com <c>EnumerateMatches</c> e não com <c>Matches</c>: isto varre o dump INTEIRO,
    /// que passa de 100 KB numa conversa cheia, e roda em todo envio. A versão com <c>Match</c> alocava
    /// um objeto por nó, ou seja, milhares de objetos por contato para responder uma pergunta que só
    /// precisa de dois índices.</para>
    /// </remarks>
    private static List<(int Start, int End)> RaizesDeJanela(string xml)
    {
        var raizes = new List<(int Start, int End)>();
        var profundidade = 0;
        var inicio = -1;
        foreach (var tag in TagRx.EnumerateMatches(xml))
        {
            if (xml[tag.Index + 1] == '/')
            {
                if (profundidade > 0 && --profundidade == 0 && inicio >= 0)
                {
                    raizes.Add((inicio, tag.Index + tag.Length));
                    inicio = -1;
                }
                continue;
            }
            var folha = xml[tag.Index + tag.Length - 2] == '/';
            if (profundidade > 0)
            {
                if (!folha)
                {
                    profundidade++;
                }
            }
            else if (folha)
            {
                raizes.Add((tag.Index, tag.Index + tag.Length));
            }
            else
            {
                inicio = tag.Index;
                profundidade = 1;
            }
        }
        if (inicio >= 0)
        {
            raizes.Add((inicio, xml.Length));
        }
        return raizes;
    }

    /// <summary>O trecho [inicio, fim) do XML tem algum nó que casa com este padrão?</summary>
    private static bool TemNoTrecho(Regex rx, string xml, int inicio, int fim) =>
        rx.Match(xml, inicio) is { Success: true } m && m.Index < fim;

    /// <summary>O primeiro botão que só FECHA um aviso dentro do trecho [inicio, fim), opcionalmente
    /// exigindo que ele caiba dentro de um retângulo. null = não há um reconhecível.</summary>
    private static (int X, int Y)? BotaoQueFechaEm(
        string xml, int inicio, int fim, (int X1, int Y1, int X2, int Y2)? dentroDe)
    {
        for (var no = NoRx.Match(xml, inicio); no.Success && no.Index < fim; no = no.NextMatch())
        {
            if (BotaoQueSoFecha(no.Value) is not { } ponto)
            {
                continue;
            }
            if (dentroDe is { } limite
                && (RetanguloDe(no.Value) is not { } b || !Dentro(limite, b)))
            {
                continue;
            }
            return ponto;
        }
        return null;
    }

    /// <summary>Este retângulo passa POR CIMA do centro do campo de mensagem, e é grande o bastante
    /// para ser um painel em vez de um pedaço do próprio campo?</summary>
    private static bool Cobre((int X1, int Y1, int X2, int Y2) c, int x, int y, long areaMinima) =>
        x >= c.X1 && x <= c.X2 && y >= c.Y1 && y <= c.Y2 && AreaDe(c) >= areaMinima;

    /// <summary>O retângulo <paramref name="dentro"/> cabe inteiro em <paramref name="fora"/>?</summary>
    private static bool Dentro(
        (int X1, int Y1, int X2, int Y2) fora, (int X1, int Y1, int X2, int Y2) dentro) =>
        dentro.X1 >= fora.X1 && dentro.Y1 >= fora.Y1 && dentro.X2 <= fora.X2 && dentro.Y2 <= fora.Y2;

    private static long AreaDe((int X1, int Y1, int X2, int Y2) r) =>
        (long)(r.X2 - r.X1) * (r.Y2 - r.Y1);

    /// <summary>O valor de um atributo do nó. "" = ausente.</summary>
    private static string AtributoDe(Regex rx, string no) =>
        rx.Match(no) is { Success: true } m ? m.Groups[1].Value : "";

    /// <summary>O ponto pra tocar, se este nó for um botão que só FECHA um aviso. null = não é.</summary>
    private static (int X, int Y)? BotaoQueSoFecha(string no) =>
        no.Contains("clickable=\"true\"", StringComparison.Ordinal)
        && EhBotao(no)
        && RotuloSoFecha(RotuloDe(no))
            ? CentroDe(no)
            : null;

    /// <summary>Os nós CLICÁVEIS da tela: o rótulo e, quando dá pra ler, o centro para tocar.</summary>
    /// <remarks>
    /// Uma varredura só, e não duas. Achar o botão que fecha um aviso e listar os candidatos quando
    /// nenhum serve são a MESMA pergunta feita para fins diferentes, e estavam escritas duas vezes:
    /// dois laços sobre <see cref="NoRx"/>, duas leituras de <c>clickable</c>, duas extrações de texto.
    /// Duplicar isso não custava só linhas — o caminho de falha varria a árvore inteira duas vezes, e as
    /// duas cópias podiam divergir na definição de "rótulo" no primeiro ajuste.
    /// <para>Lê <c>text</c> e, na falta dele, <c>content-desc</c>: botão só com ícone não tem texto, e é
    /// justamente o caso em que ninguém adivinha o rótulo olhando a tela de longe.</para>
    /// </remarks>
    private static IEnumerable<(string Rotulo, (int X, int Y)? Centro)> Clicaveis(string xml)
    {
        foreach (Match no in NoRx.Matches(xml))
        {
            if (!no.Value.Contains("clickable=\"true\"", StringComparison.Ordinal))
            {
                continue;
            }
            var texto = RotuloDe(no.Value);
            if (texto.Length == 0)
            {
                continue;
            }
            yield return (texto, CentroDe(no.Value));
        }
    }

    /// <summary>O rótulo do nó: o <c>text</c> e, na falta dele, o <c>content-desc</c>. "" = nenhum.</summary>
    /// <remarks>Extraído porque a definição de "rótulo" é usada em DOIS lugares (a listagem de
    /// clicáveis e o reconhecimento do botão que fecha um aviso), e duas cópias divergiriam no primeiro
    /// ajuste. É a mesma razão que já tinha juntado as duas varreduras em <see cref="Clicaveis"/>.</remarks>
    private static string RotuloDe(string no) =>
        AtributoDe(TextoRx, no).Trim() is { Length: > 0 } texto ? texto : AtributoDe(DescRx, no).Trim();

    /// <summary>Este rótulo é de um botão que apenas FECHA um aviso?</summary>
    /// <remarks>Um lugar só, porque a pergunta é feita nos dois caminhos de recuperação e com regras de
    /// segurança diferentes em volta. O que NÃO pode divergir é a lista.</remarks>
    private static bool RotuloSoFecha(string rotulo) => BotoesQueSoFecham.Contains(rotulo);

    /// <summary>O nó é um CONTROLE (botão) e não conteúdo da conversa?</summary>
    /// <remarks>
    /// Dois sinais, porque nenhum sozinho cobre o app: a <c>class</c> termina em "Button" (o caso
    /// normal, inclusive dos botões Material, que se declaram <c>android.widget.Button</c>), ou o
    /// resource-id é de botão (<c>android:id/button1</c> do diálogo do sistema, <c>id/..._btn</c> do
    /// próprio WhatsApp) para o caso de o aviso usar um TextView clicável no lugar de um botão.
    /// <para>Bolha de mensagem não passa por nenhum dos dois: ela é ViewGroup/TextView com id de
    /// conversa. É exatamente essa a separação que este método existe pra fazer.</para>
    /// </remarks>
    private static bool EhBotao(string no)
    {
        if (ClasseRx.Match(no) is { Success: true } c
            && c.Groups[1].Value.EndsWith("Button", StringComparison.Ordinal))
        {
            return true;
        }
        var id = IdRx.Match(no);
        return id.Success
            && (id.Groups[1].Value.Contains("button", StringComparison.OrdinalIgnoreCase)
                || id.Groups[1].Value.Contains("_btn", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>O retângulo do nó a partir do atributo <c>bounds</c>. null = não deu pra ler.</summary>
    private static (int X1, int Y1, int X2, int Y2)? RetanguloDe(string no)
    {
        var b = BoundsRx.Match(no);
        return b.Success
            && int.TryParse(b.Groups[1].Value, out var x1)
            && int.TryParse(b.Groups[2].Value, out var y1)
            && int.TryParse(b.Groups[3].Value, out var x2)
            && int.TryParse(b.Groups[4].Value, out var y2)
                ? (x1, y1, x2, y2)
                : null;
    }

    /// <summary>Centro do nó a partir do atributo <c>bounds</c>. null = não deu pra ler.</summary>
    private static (int X, int Y)? CentroDe(string no) =>
        RetanguloDe(no) is { } r ? ((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2) : null;

    /// <summary>Apura o que dava pra saber de uma tela que bloqueou o envio e não foi reconhecida:
    /// os rótulos clicáveis dela, e o XML inteiro guardado em disco.</summary>
    /// <remarks>
    /// 🔴 A PERGUNTA QUE ISTO RESPONDE é "qual botão fecha esse aviso?", e ela só tem resposta enquanto
    /// o aviso está na tela. O envio não pode parar e esperar: o lote continua, o contato seguinte abre
    /// outra conversa, e a tela que interessa é substituída em segundos. Ou se captura no instante, ou
    /// se perde.
    ///
    /// <para>Os rótulos vão na MENSAGEM DE ERRO de propósito, e não só no arquivo. O erro já é gravado
    /// no CSV do console (coluna `erro`) e no log do dispatcher, sem nada novo pra configurar — então a
    /// resposta chega a quem lê o resultado do lote de manhã, mesmo que a pasta de telas esteja num
    /// container que já morreu.</para>
    ///
    /// <para>⚠️ CAPTURA, NÃO APRENDE. O sistema NÃO passa a tocar no botão que descobriu: a lista de
    /// <see cref="BotoesQueSoFecham"/> continua fechada e editada à mão. Tocar em texto desconhecido é
    /// tocar às cegas numa tela que pode ter "Bloquear" ou "Denunciar", e o custo de errar é a conta.
    /// Automatizar o DIAGNÓSTICO é seguro; automatizar a DECISÃO não é.</para>
    /// </remarks>
    private string DescreverTelaDesconhecida(string xml)
    {
        var rotulos = RotulosClicaveis(xml);
        var arquivo = GuardarTela(xml);
        var lista = rotulos.Count == 0
            ? "nenhum com texto legível"
            : string.Join(" | ", rotulos.Take(12));
        return " Uma tela NÃO RECONHECIDA estava na frente da conversa e o aviso não pôde ser fechado "
            + $"pelo botão (só por BACK, que sai do chat). Botões visíveis: {lista}."
            + (arquivo is null ? "" : $" Tela salva em {arquivo}.");
    }

    /// <summary>O que dava pra apurar da tela quando a digitação não chegou no campo.</summary>
    /// <remarks>
    /// 🔴 "Campo ficou com nada caracteres" descreve o SINTOMA e esconde as duas causas, que têm
    /// consertos opostos: ou o teclado falhou, ou alguma coisa estava por cima da conversa comendo o
    /// toque. O <see cref="FecharAvisoSobreAConversaAsync"/> fecha os avisos cujo botão o driver
    /// reconhece; quando ele não reconhece, esta linha é o que sobra pra alguém descobrir o rótulo novo
    /// sem precisar estar olhando o aparelho na hora exata.
    /// <para>Lista de botões CURTA de propósito: isto vai pra uma coluna de CSV, ao lado das outras
    /// falhas do lote, e a tela inteira já está no arquivo pra quem quiser o resto.</para>
    /// </remarks>
    private string EvidenciaDeCampoQueNaoRecebeu(string? xml)
    {
        if (xml is null)
        {
            return "";
        }
        var rotulos = RotulosClicaveis(xml);
        var arquivo = GuardarTela(xml);
        // A causa provável vem PRIMEIRO e afirmada, quando a tela permite afirmar. "Tinha algo na
        // frente" manda mexer no aviso; a lista de botões sozinha manda adivinhar.
        var naFrente = LerAvisoSobreOCampo(xml).Motivo ?? "";
        return naFrente
            + " Botões na tela nesse instante: "
            + (rotulos.Count == 0 ? "nenhum com texto legível" : string.Join(" | ", rotulos.Take(8)))
            + ". Se um deles fecha o aviso, o rótulo dele precisa entrar na lista de botões neutros."
            + (arquivo is null ? "" : $" Tela salva em {arquivo}.");
    }

    /// <summary>Os rótulos clicáveis da tela, sem repetir e sem parágrafo. É a lista de candidatos a
    /// "botão que fecha isto" — o dado que falta pra decidir o que entra em
    /// <see cref="BotoesQueSoFecham"/>.</summary>
    /// <remarks>Corta texto longo porque um aviso inteiro às vezes é clicável, e o parágrafo dele não
    /// cabe numa coluna de CSV nem ajuda a escolher rótulo de botão.</remarks>
    private static List<string> RotulosClicaveis(string xml) =>
        [.. Clicaveis(xml)
            .Select(c => c.Rotulo.Length <= 40 ? c.Rotulo : c.Rotulo[..40] + "…")
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Grava a tela em disco. Devolve o caminho, ou null se não deu pra gravar.</summary>
    /// <remarks>
    /// Deduplica pelo CONTEÚDO: o mesmo aviso aparecendo em trinta contatos do lote grava um arquivo
    /// só. Sem isso a pasta enche de cópias idênticas e a tela NOVA, que é a que interessa, fica
    /// enterrada. E rotaciona, porque diagnóstico que enche o disco do operador vira outro problema.
    /// <para>Best-effort: pasta sem permissão ou disco cheio NÃO derruba o envio. Os rótulos já foram
    /// para a mensagem de erro, que é a parte que responde a pergunta.</para>
    /// </remarks>
    private string? GuardarTela(string xml)
    {
        try
        {
            var pasta = string.IsNullOrWhiteSpace(_opts.UiDumpDir)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MtrxSys", "telas")
                : _opts.UiDumpDir;
            Directory.CreateDirectory(pasta);

            var assinatura = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))[..8]
                .ToLowerInvariant();
            if (Directory.EnumerateFiles(pasta, $"*-{assinatura}.xml").FirstOrDefault() is { } jaTem)
            {
                return jaTem;
            }

            var caminho = Path.Combine(
                pasta,
                $"tela-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{assinatura}.xml");
            File.WriteAllText(caminho, xml, Encoding.UTF8);

            foreach (var velho in Directory.EnumerateFiles(pasta, "tela-*.xml")
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(Math.Max(1, _opts.UiDumpKeep)))
            {
                File.Delete(velho);
            }
            return caminho;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Devolve o aparelho a um estado neutro depois de uma falha, para o próximo contato do
    /// lote não herdar um diálogo aberto na tela.</summary>
    /// <remarks>
    /// Dois BACK e não um: o "não está no WhatsApp" às vezes empilha o diálogo sobre uma conversa
    /// meio aberta. O segundo BACK é inofensivo se o primeiro já resolveu — a tela inicial do WhatsApp
    /// (ou a Home) é destino aceitável, já que o próximo envio abre a conversa por deep link de
    /// qualquer forma.
    /// <para>Best-effort de propósito: um erro aqui NÃO pode mascarar o erro de envio, que é o que o
    /// operador precisa ler. Por isso engole a exceção e não altera o resultado.</para>
    /// </remarks>
    private async Task DispensarDialogoAsync(CancellationToken ct)
    {
#pragma warning disable CA1031 // recuperação best-effort: ver o remarks
        try
        {
            await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
            await Task.Delay(400, ct);
            await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // aparelho sumiu no meio da limpeza: o erro de envio ja explica o lote
        }
#pragma warning restore CA1031
    }

    /// <summary>Fecha a conversa que ficou aberta do envio anterior, para "achei o campo de mensagem"
    /// voltar a significar "a conversa que EU pedi abriu".</summary>
    /// <remarks>
    /// 🔴 O RISCO É MANDAR PARA A PESSOA ERRADA, e ele nasce de dois fatos que só juntos fazem estrago.
    /// Primeiro: no SUCESSO o driver deixa a conversa aberta de propósito, então o aparelho começa o
    /// próximo envio DENTRO da conversa do contato anterior. Segundo: `am start` devolver 0 significa
    /// que a intent foi despachada, não que o app trocou de tela (app ocupado, cold start, URI de
    /// contato que não resolve mais).
    ///
    /// <para>Junte os dois no caminho da DIGITAÇÃO: a abertura "dá certo", o poll acha o
    /// <c>id/entry</c> — que é o da conversa ANTERIOR, ainda na tela —, o driver digita ali e toca
    /// enviar. O contato anterior recebe uma segunda mensagem, o atual não recebe nada, e o console
    /// grava SUCESSO para o atual. Falha silenciosa, e das caras: dobra mensagem em quem já recebeu,
    /// que é o comportamento que queima número.</para>
    ///
    /// <para>O caminho do deep link não precisa disto porque o texto vai NA URL: sem navegação o campo
    /// fica vazio, o botão de enviar não existe e o envio falha limpo. Quem digita não tem essa
    /// proteção de graça, porque é o próprio driver que põe o texto no campo que estiver na tela. Era a
    /// metade da defesa que faltava, e justamente no caminho que roda por padrão.</para>
    ///
    /// <para>⚠️ BEST-EFFORT DE PROPÓSITO, e esta decisão custou uma tentativa. A primeira versão FALHAVA
    /// o envio quando não conseguia sair, seguindo o <see cref="GarantirTelaSemRascunhoAsync"/> — e isso
    /// atropelava diagnósticos que valem mais que ela. Numa conversa de conta RESTRINGIDA o campo fica
    /// na tela o tempo todo; um aparelho que não respondesse aos BACK devolveria "não consegui sair"
    /// no lugar de <c>ContaRestringida</c>, que é o ÚNICO veredito que manda parar o lote. Trocar o
    /// aviso de "pare, o chip está restrito" por "confira o aparelho" é o caminho do banimento.
    ///
    /// <para>Então: quando os BACK funcionam (o caso normal), a proteção é total, porque achar o campo
    /// depois disso só pode significar que a conversa pedida abriu. Quando não funcionam, o
    /// comportamento é o de antes desta mudança, sem nada novo quebrado, e o resto do driver segue
    /// diagnosticando. Proteção que não atrapalha vale mais que proteção que atropela.</para>
    ///
    /// <para>Tela ILEGÍVEL também não barra nada: sem leitura de tela os polls seguintes falham
    /// sozinhos, então o envio morre sem tocar em coisa nenhuma.</para>
    /// </remarks>
    private async Task SairDaConversaAnteriorAsync(CancellationToken ct)
    {
        for (var tentativa = 0; tentativa < 3; tentativa++)
        {
            if (await DumpUiAsync(ct) is not { } xml || !EntryNodeRx.IsMatch(xml))
            {
                return;
            }
            await _adb.ShellAsync("input keyevent KEYCODE_BACK", ct);
            await Task.Delay(500, ct);
        }
    }

    /// <summary>Garante que a tela NÃO tem texto pendente no campo antes de abrir a próxima conversa.
    /// null = pode seguir; string = motivo pra não enviar.</summary>
    /// <remarks>
    /// 🔴 O toque em enviar é a única ação deste driver que produz efeito irreversível, e ele nunca
    /// confirmou EM QUAL conversa está. `am start` devolver 0 diz que a intent foi despachada, não que
    /// o WhatsApp navegou; o poll do `id/send` casa já no primeiro dump, que pode ainda mostrar a tela
    /// anterior; e o `id/send` existe sempre que o campo tem texto. Junte as três e o caminho do deep
    /// link podia tocar enviar numa conversa ALHEIA: o contato anterior recebia, e o console registrava
    /// sucesso pro contato atual.
    ///
    /// <para>A verificação vem ANTES e não depois de propósito. Conferir o texto do campo depois de
    /// abrir não resolve, porque dois contatos do mesmo lote costumam sortear o MESMO template: um
    /// rascunho do anterior é idêntico ao texto pretendido pro atual, e a comparação passaria. Já a
    /// pré-condição ataca a causa — sem botão de enviar na tela, o toque em conversa errada deixa de
    /// ter como acontecer, e um deep link que não navegou vira um poll que estoura, ou seja, uma falha
    /// limpa e legível.</para>
    ///
    /// <para>Dump ilegível NÃO barra o envio: nesse caso o poll seguinte falha sozinho, e travar o lote
    /// por uma leitura de tela que não veio seria trocar um risco raro por uma parada garantida.</para>
    /// </remarks>
    private async Task<string?> GarantirTelaSemRascunhoAsync(CancellationToken ct)
    {
        if (LerEstadoDoCampo(await DumpUiAsync(ct)) is not EstadoDoCampo.ComTexto)
        {
            return null;
        }
        // Os mesmos dois BACK da limpeza de falha: sair da conversa faz o WhatsApp guardar o texto como
        // RASCUNHO daquela conversa, em vez de deixá-lo na tela onde o próximo envio tropeça nele.
        await DispensarDialogoAsync(ct);
        await Task.Delay(600, ct);
        return LerEstadoDoCampo(await DumpUiAsync(ct)) is not EstadoDoCampo.ComTexto
            ? null
            : "a tela do aparelho está numa conversa com texto pendente no campo e não consegui sair "
              + "dela. Não enviei: daqui o toque em enviar iria para a conversa errada. Confira o "
              + "aparelho (alguém digitando nele?) e retome.";
    }

    private async Task<WhatsAppSendResult> SendByDeepLinkAsync(string digits, string text, CancellationToken ct)
    {
        var url = WhatsAppUi.DeepLink(digits, text);
        if (url.Contains('\'', StringComparison.Ordinal))
        {
            return WhatsAppSendResult.Fail(
                FalhaCausa.EntradaInvalida, "texto gerou aspa simples (não esperado no URL-encode).");
        }

        await _uiLock.WaitAsync(ct);
        var tocouEnviar = false;
        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, _opts.WhatsAppSendTimeoutSeconds)));
            var sct = sendCts.Token;

            // ANTES da leitura de tela, não depois: a pré-condição abaixo lê a árvore, e a árvore de um
            // aparelho dormindo é a do keyguard. Sem acordar primeiro, ela julgaria a tela errada.
            await AcordarAparelhoAsync(sct);

            if (await GarantirTelaSemRascunhoAsync(sct) is { } sujeira)
            {
                return WhatsAppSendResult.Fail(FalhaCausa.RascunhoPendente, sujeira);
            }

            var (rc, outp, err) = await OpenChatAsync(url, sct);
            if (rc != 0)
            {
                return WhatsAppSendResult.Fail(
                    FalhaCausa.AdbFalhou, string.IsNullOrWhiteSpace(err) ? outp : err);
            }
            var achado = await PollNoAsync(SendNodeRx, _opts.WhatsAppOpenWaitMs, sct);

            // Mesmo aviso por cima da conversa do caminho da digitação: aqui ele bloquearia o toque em
            // ENVIAR, e a falha sairia como "o texto continua no campo". Só com o botão já encontrado,
            // pelo motivo descrito no método.
            var send = await ReencontrarSemAvisoAsync(
                SendNodeRx, achado?.Centro, achado?.Xml, sct);

            if (send is null)
            {
                // 🔴 DIAGNOSTICA ANTES DE LIMPAR. O diálogo de "não está no WhatsApp" também tem um
                // botão OK, então dispensar primeiro apagaria a única prova de que a causa é o NÚMERO,
                // e a falha voltaria a ser classificada como problema de aparelho.
                var (diagnostico, tela) = await DiagnosticarChatNaoAbertoAsync(sct);
                if (diagnostico.Causa is not FalhaCausa.ConversaNaoAbriu)
                {
                    return diagnostico;
                }

                var evidencia = await TentarDesbloquearTelaAsync(tela, sct);
                send = await PollNodeCenterAsync(
                    SendNodeRx, Math.Min(_opts.WhatsAppOpenWaitMs, 3000), sct);
                if (send is null)
                {
                    // Devolve o diagnóstico ORIGINAL: depois do BACK a tela mudou, e um segundo
                    // diagnóstico descreveria a tela que eu mesmo criei, não a que causou a falha.
                    // A evidência da tela desconhecida vem ANEXADA, não no lugar: ela descreve o que
                    // bloqueou, enquanto o diagnóstico diz o que o envio concluiu.
                    // A causa sobe de genérica para TelaInesperada quando houve evidência, igual ao
                    // caminho da digitação: mesma situação real, mesma causa gravada no log, senão o
                    // relatório agruparia o mesmo defeito em dois baldes conforme um toggle de config.
                    return evidencia is null
                        ? diagnostico
                        : diagnostico with
                        {
                            Error = diagnostico.Error + evidencia,
                            Causa = FalhaCausa.TelaInesperada,
                        };
                }
            }

            // Daqui pra frente NADA é conclusivo. O toque é irreversível e não tem confirmação síncrona:
            // tudo o que vier depois só pode dizer "confirmei que saiu" ou "não confirmei", nunca
            // "garanto que não saiu". A marca existe pra o catch lá embaixo saber de que lado do toque
            // o tempo estourou.
            tocouEnviar = true;
            await TapAsync(send.Value, sct);

            return await PollEntryClearedAsync(_opts.WhatsAppSendWaitMs, sct) switch
            {
                EstadoDoCampo.Vazio => WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct)),
                EstadoDoCampo.ComTexto => WhatsAppSendResult.Fail(
                    FalhaCausa.TextoContinuouNoCampo,
                    "toquei enviar e o texto CONTINUA no campo: a mensagem não saiu."),
                _ => WhatsAppSendResult.Unconfirmed(
                    FalhaCausa.ToqueNaoConfirmado,
                    "toquei enviar mas não consegui ler a tela pra confirmar. Pode ter saído; não vou "
                    + "tentar de novo sozinho pra não mandar duas vezes."),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            const string motivo = "envio excedeu o tempo total (aparelho lento/travado?).";
            return tocouEnviar
                ? WhatsAppSendResult.Unconfirmed(
                    FalhaCausa.Timeout,
                    motivo + " O toque em enviar JÁ tinha acontecido, então pode ter saído.")
                : WhatsAppSendResult.Fail(
                    FalhaCausa.Timeout, motivo + " Estourou antes de tocar enviar, então nada saiu.");
        }
        finally
        {
            _uiLock.Release();
        }
    }

    private async Task<WhatsAppSendResult> SendByTypingAsync(
        string digits, string text, string? chatUri, CancellationToken ct)
    {
        if (await ResolveTypingChannelAsync(text, ct) is not { } typing)
        {
            // FALHA em vez de cair no deep link. Voltar ao caminho antigo em silêncio devolveria o
            // comportamento que se quis remover, e o log diria "enviado" do mesmo jeito.
            return WhatsAppSendResult.Fail(
                FalhaCausa.DigitacaoFalhou,
                $"digitação humana exigida mas indisponível: nem o teclado {_opts.TypingImePackage} está "
                + "instalado/ativo, nem o texto é simples o bastante pro `input text` (acento e emoji "
                + "quebram). Instale o IME no aparelho ou desligue Phone__HumanTyping.");
        }

        await _uiLock.WaitAsync(ct);
        var tocouEnviar = false;
        try
        {
            // Teto calculado: digitar é a única etapa cujo custo depende do TAMANHO da mensagem.
            var typingBudget = TimeSpan.FromMinutes(
                2.0 * text.Length / Math.Clamp(_opts.TypingCharsPerMinute, 60, 1200));
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(
                TimeSpan.FromSeconds(Math.Max(60, _opts.WhatsAppSendTimeoutSeconds)) + typingBudget);
            var sct = sendCts.Token;

            // Mesmo motivo do caminho do deep link: sem isto, o primeiro envio de cada bloco encontra o
            // aparelho dormindo e falha como se a conversa não tivesse aberto.
            await AcordarAparelhoAsync(sct);

            await SairDaConversaAnteriorAsync(sct);

            // 🔴 CASCATA DE ABERTURA. O deep link por número exige que o app RESOLVA o número no
            // servidor, e é exatamente aí que ele nega gente que TEM WhatsApp: quando a conta guarda o
            // número na outra forma do 9º dígito, e quando a conta está restringida. Os dois primeiros
            // níveis não passam por resolução nenhuma — usam um contato que o app JÁ reconheceu.
            //
            // Cada nível só custa quando o anterior falha, e o último é o comportamento de sempre. Ou
            // seja: no pior caso isto gasta duas tentativas de abertura a mais e faz o que já fazia.
            var abertoPeloRegistro = false;
            var entry = chatUri is null ? null : await AbrirPeloRegistroAsync(chatUri, sct);
            if (entry is not null)
            {
                abertoPeloRegistro = true;
            }
            else
            {
                entry ??= await AbrirPelaListaAsync(digits, sct);
                abertoPeloRegistro = entry is not null;
            }

            // A tela em que o campo foi encontrado, quando quem achou soube devolvê-la. Os níveis
            // alternativos da cascata devolvem só o ponto, e aí o fechamento lê a tela por conta.
            string? telaDoCampo = null;
            if (entry is null)
            {
                var (rc, outp, err) = await OpenChatAsync(WhatsAppUi.DeepLinkEmpty(digits), sct);
                if (rc != 0)
                {
                    return WhatsAppSendResult.Fail(
                        FalhaCausa.AdbFalhou, string.IsNullOrWhiteSpace(err) ? outp : err);
                }
                var achado = await PollNoAsync(EntryNodeRx, _opts.WhatsAppOpenWaitMs, sct);
                entry = achado?.Centro;
                telaDoCampo = achado?.Xml;
            }

            // 🔴 CAMPO ENCONTRADO NÃO É CAMPO ALCANÇÁVEL: um aviso por cima da conversa deixa o
            // `id/entry` na árvore e rouba o toque. Ver FecharAvisoSobreAConversaAsync, inclusive por
            // que isto roda SÓ com o campo já encontrado.
            entry = await ReencontrarSemAvisoAsync(EntryNodeRx, entry, telaDoCampo, sct);

            if (entry is null)
            {
                // 🔴 O MESMO TRATAMENTO DO OUTRO CAMINHO, e não é simetria decorativa: este aqui é o
                // que roda com digitação humana LIGADA, que é o default. Enquanto só o deep link
                // diagnosticava, um número sem conta chegava ao console como "a conversa não abriu",
                // ou seja, entrava no contador de falha de APARELHO e disparava o alerta de celular
                // travado com o celular perfeito.
                var (diagnostico, tela) = await DiagnosticarChatNaoAbertoAsync(sct);

                // 🔴 O DIAGNÓSTICO CONCLUSIVO ENCERRA AQUI, SEM TENTAR DESBLOQUEAR NADA.
                //
                // Duas razões, e as duas custaram caro. A primeira: este bloco já jogou o `diagnostico`
                // fora uma vez. Quando a tela dizia "Sua conta foi restringida", o veredito nascia certo
                // ali em cima e morria aqui, e o console recebia "a conversa não abriu", ou seja falha
                // de APARELHO. `ContaRestringida` é a única condição que autoriza PARAR o lote, e é ela
                // que cancela a segunda chance: sem o flag, o lote seguia contra um chip restrito com
                // cada contato pagando a tentativa extra — medido em 2026-08-10, 22 dos 23 fracassos
                // daquele lote —, que é o que transforma restrição temporária em banimento.
                //
                // A segunda: o TentarDesbloquearTelaAsync TOCA por rótulo, sem exigir botão de verdade,
                // e a premissa dele é que a conversa não está na tela. Numa conta restringida ela ESTÁ:
                // o app tira o campo de mensagem e deixa a lista de mensagens à mostra. Se alguma delas
                // for template com botão de RESPOSTA RÁPIDA (rótulo escolhido por quem mandou, "OK" é o
                // mais comum), o desbloqueio tocaria nele e MANDARIA uma mensagem na conversa da pessoa.
                // Tela que já se explicou não tem o que desbloquear, então nem se tenta.
                if (diagnostico.Causa is not FalhaCausa.ConversaNaoAbriu)
                {
                    return diagnostico;
                }

                var evidencia = await TentarDesbloquearTelaAsync(tela, sct);
                entry = await PollNodeCenterAsync(
                    EntryNodeRx, Math.Min(_opts.WhatsAppOpenWaitMs, 3000), sct);
                if (entry is null)
                {
                    // A tela desconhecida, quando houve uma, é a causa mais específica: ela diz que algo
                    // ESTAVA na frente da conversa, e não que a conversa simplesmente não veio.
                    return WhatsAppSendResult.Fail(
                        evidencia is null ? FalhaCausa.ConversaNaoAbriu : FalhaCausa.TelaInesperada,
                        "campo de mensagem não apareceu (a conversa não abriu)." + evidencia);
                }
            }
            await TapAsync(entry.Value, sct);

            var previousIme = typing is TypingChannel.Ime ? await SelectTypingImeAsync(sct) : null;
            try
            {
                // Campo LIMPO antes de começar: o envio reabre a MESMA conversa, então sobra de uma
                // tentativa anterior entraria embaralhada com a nova (medido em 2026-07-27).
                if (!await ClearEntryAsync(typing, sct))
                {
                    return WhatsAppSendResult.Fail(
                        FalhaCausa.DigitacaoFalhou,
                        "o campo de mensagem já tinha texto e não consegui limpar.");
                }
                await Task.Delay(Random.Shared.Next(900, 2600), sct);

                var (motivo, jaSaiuAlgo) = await TypeInChunksAsync(typing, text, sct);
                if (motivo is not null)
                {
                    await ClearEntryAsync(typing, sct);
                    // 🔴 "Digitação abortada" nem sempre significa "nada saiu". Com a opção "Tecla Enter
                    // para enviar" LIGADA, o Enter da primeira quebra de linha JÁ mandou a mensagem
                    // pela metade — o aborto impede o resto, não desfaz o que foi. Devolver isso como
                    // falha conclusiva fazia o console reabrir a conversa na outra forma do número e
                    // mandar de novo, somando uma segunda mensagem à metade que a pessoa já recebeu.
                    return jaSaiuAlgo
                        ? WhatsAppSendResult.Unconfirmed(
                            FalhaCausa.ToqueNaoConfirmado, $"digitação abortada, campo limpo. {motivo}")
                        : WhatsAppSendResult.Fail(
                            FalhaCausa.DigitacaoFalhou, $"digitação abortada, campo limpo. {motivo}");
                }

                // Confere o TAMANHO antes de enviar: o IME pode engolir trecho em silêncio, e mensagem
                // truncada é pior que mensagem nenhuma.
                // Uma leitura só, e o XML fica: quando o campo não recebeu o texto, a MESMA tela é a
                // prova do porquê. Reler depois traria outra tela, e dumpar duas vezes no caminho de
                // falha custa outro processo `adb`.
                var telaDigitada = await DumpUiAsync(sct);
                var typed = EntryTextFrom(telaDigitada);
                if (typed is null || Math.Abs(typed.Length - text.Length) > Math.Max(4, text.Length / 20))
                {
                    var achou = typed?.Length.ToString(CultureInfo.InvariantCulture) ?? "nada";
                    await ClearEntryAsync(typing, sct);
                    return WhatsAppSendResult.Fail(
                        FalhaCausa.DigitacaoFalhou,
                        $"campo ficou com {achou} caracteres, esperava ~{text.Length}; "
                        + "envio abortado pra não mandar truncado."
                        + EvidenciaDeCampoQueNaoRecebeu(telaDigitada));
                }
            }
            finally
            {
                await RestoreImeAsync(previousIme);
            }

            await Task.Delay(Random.Shared.Next(700, 2200), sct);
            var send = await PollNodeCenterAsync(SendNodeRx, _opts.WhatsAppSendWaitMs, sct);
            if (send is null)
            {
                await ClearEntryAsync(typing, sct);

                // 🔴 PERGUNTA À TELA ANTES DE CULPAR O APARELHO. A cascata de abertura usa um contato
                // que o app JÁ reconhece, então ela abre a conversa mesmo com a conta restrita — e aí
                // a restrição só se manifesta AQUI, no botão de enviar que não aparece. Sem esta
                // pergunta, o único caso em que a cascata funciona é justamente o que esconde o motivo.
                //
                // SÓ a restrição é promovida. "Número sem conta" seria absurdo neste ponto: a conversa
                // abriu e o texto foi digitado, então o número resolveu. Custa um dump, e só na falha.
                var (diagnostico, _) = await DiagnosticarChatNaoAbertoAsync(sct);
                if (diagnostico.ContaRestringida)
                {
                    return diagnostico;
                }

                return WhatsAppSendResult.Fail(
                    FalhaCausa.ConversaNaoAbriu,
                    "botão enviar não apareceu mesmo com o campo preenchido.");
            }

            // Ver a mesma marca no caminho do deep link: depois do toque não existe mais conclusão
            // negativa, só confirmação ou ausência dela.
            tocouEnviar = true;
            await TapAsync(send.Value, sct);

            return await PollEntryClearedAsync(_opts.WhatsAppSendWaitMs, sct) switch
            {
                EstadoDoCampo.Vazio => WhatsAppSendResult.Ok(await ReadLastMessageStatusAsync(sct))
                    with { AbertoPeloRegistro = abertoPeloRegistro },
                EstadoDoCampo.ComTexto => WhatsAppSendResult.Fail(
                    FalhaCausa.TextoContinuouNoCampo,
                    "toquei enviar e o texto CONTINUA no campo: a mensagem não saiu."),
                _ => WhatsAppSendResult.Unconfirmed(
                    FalhaCausa.ToqueNaoConfirmado,
                    "toquei enviar mas não consegui ler a tela pra confirmar. Pode ter saído; não vou "
                    + "tentar de novo sozinho pra não mandar duas vezes."),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var motivo = $"envio excedeu o teto (base {_opts.WhatsAppSendTimeoutSeconds}s + orçamento de "
                + $"digitação pra {text.Length} caracteres a {_opts.TypingCharsPerMinute} cpm).";
            return tocouEnviar
                ? WhatsAppSendResult.Unconfirmed(
                    FalhaCausa.Timeout,
                    motivo + " O toque em enviar JÁ tinha acontecido, então pode ter saído.")
                : WhatsAppSendResult.Fail(
                    FalhaCausa.Timeout, motivo + " Estourou antes de tocar enviar, então nada saiu.");
        }
        finally
        {
            _uiLock.Release();
        }
    }

    /// <summary>Consegue digitar este texto? null = sim; string = motivo, pra avisar ANTES do lote.</summary>
    public async Task<string?> CheckTypingCapabilityAsync(string sampleText, CancellationToken ct)
    {
        if (!_opts.HumanTyping)
        {
            return null; // deep link entrega o texto pronto no campo: não há digitação pra falhar
        }
        if (await ResolveTypingChannelAsync(sampleText ?? string.Empty, ct) is not null)
        {
            return null;
        }
        return $"o texto tem acento e/ou emoji, e este aparelho não tem o teclado {_opts.TypingImePackage} "
            + "instalado. O `input text` do Android não digita esses caracteres, então TODOS os envios "
            + "vão falhar. Instale o IME no aparelho, ou use Phone__HumanTyping=false (o texto vai pronto "
            + "pelo deep link, sem simular digitação).";
    }

    private Task<(int Code, string Out, string Err)> OpenChatAsync(string url, CancellationToken ct) =>
        // Aspas simples: '&' e '#' da URL não podem ser interpretados pelo shell do aparelho.
        _adb.ShellAsync($"am start -a android.intent.action.VIEW -d '{url}'", ct);

    /// <summary>NÍVEL 1: abre a conversa pelo REGISTRO do contato na agenda. null = não abriu.</summary>
    /// <remarks>
    /// Um comando. A URI aponta pra linha `vnd.com.whatsapp.profile` que o próprio app publicou pra
    /// quem ele já reconheceu como usuário — a mesma que a lista do "+" usa. Não há número pra resolver,
    /// então não há o que o app negar.
    /// <para>Poll curto de propósito: se não abriu rápido, o nível seguinte é melhor do que insistir.</para>
    /// </remarks>
    private async Task<(int X, int Y)?> AbrirPeloRegistroAsync(string chatUri, CancellationToken ct)
    {
        var (rc, _, _) = await _adb.ShellAsync(
            $"am start -a android.intent.action.VIEW -d '{chatUri}'", ct);
        return rc != 0 ? null : await PollNodeCenterAsync(EntryNodeRx, AberturaAlternativaMs, ct);
    }

    /// <summary>NÍVEL 2: abre pelo botão "+" do WhatsApp, procurando o número na lista. null = não abriu.</summary>
    /// <remarks>
    /// 🔴 É O FLUXO QUE UMA PESSOA FAZ: abre o app, toca no "+", busca o número, toca no contato. A
    /// lista do "+" vem do banco INTERNO do WhatsApp, que é mais rico do que o que ele publica de volta
    /// na agenda do Android — por isso este nível alcança contato que o nível 1 não enxerga, que é
    /// justamente o caso relatado de "tem WhatsApp e o disparo não identifica".
    ///
    /// <para>E se o contato aparece nessa lista, ele TEM conta: a lista só mostra usuários. Achar ali é
    /// prova, não inferência.</para>
    ///
    /// <para>⚠️ OS RESOURCE-IDS NÃO FORAM MEDIDOS neste aparelho. Vieram do que o app usa há várias
    /// versões, e podem estar errados. É por isso que este nível é intermediário e time-boxed: se
    /// qualquer passo não responder, ele desiste e o deep link assume, ou seja, o comportamento volta a
    /// ser o de hoje. E a tela é GUARDADA na desistência, então o primeiro lote real entrega os ids
    /// certos sem ninguém precisar estar na frente do celular.</para>
    /// </remarks>
    private async Task<(int X, int Y)?> AbrirPelaListaAsync(string digits, CancellationToken ct)
    {
        // 🔴 DESISTE DO NÍVEL DEPOIS DE FALHAR SEGUIDO. Este caminho custa ~4s e 7 processos adb, e
        // NAVEGA o app (dois BACK, launcher, mais dois BACK na saída) antes de entregar os pontos. Se os
        // resource-ids não baterem neste aparelho — e eles não foram medidos —, ele falharia assim em
        // TODOS os contatos do lote, cobrando o pedágio 87 vezes e sacudindo a tela à toa antes de cada
        // deep link.
        //
        // Três tentativas bastam pra concluir: ou a tela do seletor é a esperada, ou não é, e isso não
        // muda no meio do lote. Quem descobre que mudou é o próximo processo, porque o contador vive
        // nesta instância. E a tela já foi guardada nas primeiras desistências, que é o que interessa.
        if (_listaFalhouSeguidas >= MaxFalhasDaLista)
        {
            return null;
        }
        // 🔴 SAIR DA CONVERSA ANTERIOR ANTES DE TUDO. No sucesso o driver DEIXA a conversa aberta de
        // propósito, então o app já está em primeiro plano numa conversa — e o launcher traz a tarefa
        // existente de volta EXATAMENTE COMO ESTAVA, não na lista de conversas. Sem estes BACK, o botão
        // "+" nunca estaria na tela e este nível desistiria em todo envio a partir do segundo, sem
        // ninguém entender por quê.
        await _adb.ShellAsync("input keyevent KEYCODE_BACK; input keyevent KEYCODE_BACK", ct);
        await Task.Delay(400, ct);

        // Launcher em vez de activity nomeada: nome de activity muda entre versões, categoria não.
        await _adb.ShellAsync(
            $"monkey -p {WhatsAppPackage} -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1", ct);
        await Task.Delay(800, ct);

        if (await PollNodeCenterAsync(FabRx, AberturaAlternativaMs, ct) is not { } fab)
        {
            return await DesistirDaListaAsync(ct);
        }
        await TapAsync(fab, ct);
        await Task.Delay(600, ct);

        if (await PollNodeCenterAsync(BuscaRx, AberturaAlternativaMs, ct) is not { } busca)
        {
            return await DesistirDaListaAsync(ct);
        }
        await TapAsync(busca, ct);
        // Dígitos são ASCII, então o `input text` do Android dá conta e não precisa do IME.
        await _adb.ShellAsync($"input text {digits}", ct);
        await Task.Delay(900, ct);

        // 🔴 NÃO TOCA SE A TELA OFERECE CONVITE. Quando o número NÃO é usuário, o seletor mostra a linha
        // dele numa seção de "Convidar", e tocar ali dispara um CONVITE POR SMS — irreversível, para um
        // estranho, e cobrado. É a mesma doutrina da lista fechada de botões: tocar por posição é tocar
        // às cegas, e aqui o custo de errar sai do bolso e da reputação do número.
        //
        // Na dúvida, desiste: o deep link do nível 3 resolve o caso legítimo, e um contato a menos por
        // este caminho custa muito menos que um convite disparado sem querer.
        var telaDaBusca = await DumpUiAsync(ct);
        if (telaDaBusca is null || OfereceConvite(telaDaBusca))
        {
            return await DesistirDaListaAsync(ct);
        }

        // A linha do contato no resultado. Tocar no PRIMEIRO resultado só é seguro porque a busca foi
        // pelo número inteiro: não há dois contatos distintos casando os mesmos 12-13 dígitos.
        if (await PollNodeCenterAsync(LinhaContatoRx, AberturaAlternativaMs, ct) is not { } linha)
        {
            return await DesistirDaListaAsync(ct);
        }
        await TapAsync(linha, ct);
        var aberta = await PollNodeCenterAsync(EntryNodeRx, AberturaAlternativaMs, ct);
        if (aberta is not null)
        {
            _listaFalhouSeguidas = 0;
        }
        return aberta;
    }

    /// <summary>Falhas seguidas do nível da lista. Zera quando ele funciona.</summary>
    private int _listaFalhouSeguidas;

    private const int MaxFalhasDaLista = 3;

    /// <summary>A tela de resultado está oferecendo CONVIDAR em vez de abrir conversa?</summary>
    /// <remarks>
    /// Trechos sem acento e em minúsculas, pra sobreviver a variação de texto e de idioma. Diferente
    /// das pistas de "não tem conta", aqui um falso positivo é BARATO (só desiste deste nível) e um
    /// falso negativo é CARO (convite por SMS disparado). Por isso a lista aqui é generosa, ao
    /// contrário da outra, que é restrita de propósito.
    /// </remarks>
    private static bool OfereceConvite(string xml)
    {
        var tela = xml.ToLowerInvariant();
        string[] pistas = ["convidar", "convite", "invite", "nao esta no whatsapp", "não está no whatsapp"];
        return pistas.Any(p => tela.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Sai da navegação e guarda a tela, que é o que ensina os resource-ids certos.</summary>
    /// <remarks>A tela é a única saída deste método que serve pra alguma coisa: os ids do seletor não
    /// foram medidos neste aparelho, e é o primeiro lote real que os entrega.</remarks>
    private async Task<(int X, int Y)?> DesistirDaListaAsync(CancellationToken ct)
    {
        _listaFalhouSeguidas++;
        if (await DumpUiAsync(ct) is { } xml)
        {
            GuardarTela(xml);
        }
        // Volta pra um estado neutro: o deep link do nível 3 abre a conversa de qualquer tela.
        await _adb.ShellAsync("input keyevent KEYCODE_BACK; input keyevent KEYCODE_BACK", ct);
        await Task.Delay(400, ct);
        return null;
    }

    /// <summary>Teto de espera dos níveis alternativos. Curto: falhar rápido aqui é melhor que insistir,
    /// porque existe um caminho conhecido logo abaixo e o orçamento do envio é compartilhado.</summary>
    private const int AberturaAlternativaMs = 2500;

    private static readonly Regex FabRx =
        new("<node[^>]*com\\.whatsapp:id/fab\"[^>]*>", RegexOptions.Compiled);

    private static readonly Regex BuscaRx =
        new("<node[^>]*com\\.whatsapp:id/(menuitem_search|search_src_text)\"[^>]*>", RegexOptions.Compiled);

    private static readonly Regex LinhaContatoRx =
        new("<node[^>]*com\\.whatsapp:id/contactpicker_row_name\"[^>]*>", RegexOptions.Compiled);

    private string WhatsAppPackage =>
        string.IsNullOrWhiteSpace(_opts.WhatsAppPackage) ? "com.whatsapp" : _opts.WhatsAppPackage;

    /// <summary>Acorda a tela e tira o cadeado simples da frente, antes de abrir a conversa.</summary>
    /// <remarks>
    /// 🔴 O CASO QUE MOTIVOU (2026-08-10): a tela do aparelho apaga sozinha em 10 min e a pausa entre
    /// blocos é de ~30. Ou seja, TODO primeiro envio depois de uma pausa encontrava o aparelho dormindo.
    /// O `am start` despacha a intent, mas a Activity nasce ATRÁS do keyguard: nem `id/entry` nem
    /// `id/send` entram na árvore, e o envio falhava como "a conversa não abriu" — o diagnóstico que
    /// culpa o número. Como a falha era do APARELHO, ela ainda alimentava o contador do disjuntor e o
    /// aviso de celular travado, com o celular perfeito.
    ///
    /// <para>A tentação era encurtar a pausa pra caber nos 10 min da tela. Seria consertar o sintoma
    /// pelo lado errado: a pausa é o que desmancha o padrão de máquina E o que segura o volume diário
    /// no platô de 120 (ver <c>PhoneConsoleCommand._bloco</c>). Acordar o aparelho custa um comando.</para>
    ///
    /// <para><c>KEYCODE_WAKEUP</c> e não <c>KEYCODE_POWER</c>: power é uma CHAVE (liga e desliga), então
    /// numa tela já acesa ele APAGARIA a tela — o caminho mais direto de transformar uma proteção em
    /// defeito. WAKEUP é idempotente: acende, e não faz nada se já estiver acesa.</para>
    ///
    /// <para><c>wm dismiss-keyguard</c> resolve o cadeado SEM senha (deslize). Com PIN ou biometria ele
    /// só traz o pedido de senha pra frente, e aí não há o que este driver possa fazer: o aparelho de
    /// disparo tem que estar sem bloqueio de tela. É por isso que a falha continua sendo diagnosticada
    /// (ver <see cref="TelaBloqueada"/>) em vez de silenciosamente tentada de novo.</para>
    ///
    /// <para>Best-effort: aparelho que não conhece um destes comandos segue o fluxo normal. Se ainda
    /// estiver dormindo, o poll falha e a captura de tela guarda a prova.</para>
    /// </remarks>
    private async Task AcordarAparelhoAsync(CancellationToken ct)
    {
        // Os dois numa linha só, pelo mesmo motivo do DumpUiAsync: cada ShellAsync é um processo `adb`
        // novo no PC, e isto passou a rodar antes de CADA envio. São comandos independentes e sem
        // decisão entre eles — não há o que inspecionar no meio que justifique pagar dois processos.
        await _adb.ShellAsync("input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard", ct);
        // A animação de destravar leva um instante, e um dump tirado no meio dela lê a tela antiga.
        await Task.Delay(500, ct);
    }

    /// <summary>A tela lida é a de bloqueio?</summary>
    /// <remarks>
    /// ⚠️ HEURÍSTICA, e os marcadores NÃO foram medidos neste aparelho. São os resource-ids que o
    /// systemui usa no keyguard. Se errar, erra pro lado seguro: o envio já falhou de qualquer forma, e
    /// o que muda é só a frase do erro.
    /// <para>Vale a pena mesmo assim porque separa dois consertos opostos. "A conversa não abriu" manda
    /// conferir número e WhatsApp; "o aparelho está bloqueado" manda tirar o PIN e ligar o "manter tela
    /// ativa". Sem a distinção, o segundo se disfarça de primeiro por semanas.</para>
    /// <para>A tela capturada (ver <see cref="GuardarTela"/>) é o que permite confirmar ou corrigir
    /// estes marcadores sem precisar de alguém na frente do celular.</para>
    ///
    /// <para>🔴 EXIGE O PACOTE <c>com.android.systemui</c>, e isso não é zelo: casar a palavra
    /// "keyguard" ou "lock" solta no XML pega o PRÓPRIO WhatsApp, que tem cadeado de conversa e aviso
    /// de criptografia na tela de conversa. O falso positivo aqui não erra uma ação (o envio já falhou
    /// de qualquer forma), erra a INSTRUÇÃO: mandaria o operador tirar um bloqueio de tela que não
    /// existe enquanto a causa real seguisse intocada. Errar para o lado do diagnóstico genérico é
    /// barato, porque a tela capturada continua lá para ser lida.</para>
    /// </remarks>
    /// <summary>O app está DECLARANDO na tela que a conta foi restringida?</summary>
    /// <remarks>
    /// 🔴 MEDIDO em 2026-08-10, Galaxy RQ8M908C0ZX, no meio de um lote de 87. Depois de 30 entregas
    /// normais, a tela da conversa passou a trazer, entre os nós clicáveis:
    /// <c>Sua conta foi restringida. Você não pode…</c>. Mais adiante o app foi inteiro para a tela de
    /// recurso, cujos únicos botões eram <c>Mais opções</c> e <c>PEDIR ANÁLISE</c>.
    ///
    /// <para>⚠️ ESTES DOIS SÃO MEDIDOS, ao contrário dos rótulos de <see cref="BotoesQueSoFecham"/>.
    /// Os demais idiomas são extrapolação e estão aqui só para o caso de aparelho com outro locale;
    /// quem confirmar, anote a DATA e o APARELHO, como manda o resto deste arquivo.</para>
    ///
    /// <para>Casa por TRECHO e sem acento porque a frase é cortada na árvore ("Você não pode…") e o
    /// texto exato muda com a versão. "pedir analise" entra porque, quando o app vai para a tela de
    /// recurso, a frase da restrição some e sobra só o botão.</para>
    /// </remarks>
    private static bool ContaRestrita(string xml) => CasaPista(xml.ToLowerInvariant(), PistasDeRestricao);

    /// <inheritdoc cref="ContaRestrita"/>
    private static readonly string[] PistasDeRestricao =
    [
        "conta foi restringida", "sua conta foi restringida",
        "pedir analise", "pedir análise",
        "account has been restricted", "request a review",
    ];

    /// <summary>O app está DECLARANDO na tela que este número não tem conta?</summary>
    /// <remarks>
    /// ⚠️ SÓ pistas inequívocas. "convidar para o WhatsApp" foi tirado de propósito: essa palavra
    /// aparece em outras telas do app, e um falso positivo aqui afirma a causa errada COM CONFIANÇA,
    /// que é o defeito que o <see cref="DiagnosticarChatNaoAbertoAsync"/> existe pra corrigir. O
    /// diálogo de número sem conta sempre traz alguma variação de "não está no WhatsApp"; isso basta.
    /// <para>Compare com <see cref="OfereceConvite"/>, cuja lista é GENEROSA de propósito: lá o falso
    /// positivo custa desistir de um nível de abertura, e o falso negativo dispara um convite por SMS.
    /// Aqui é o contrário, e por isso as duas listas não são a mesma.</para>
    /// </remarks>
    private static bool NumeroSemConta(string xml) => CasaPista(xml.ToLowerInvariant(), PistasSemConta);

    /// <inheritdoc cref="NumeroSemConta"/>
    private static readonly string[] PistasSemConta =
    [
        "nao esta no whatsapp", "não está no whatsapp",
        "nao existe no whatsapp", "não existe no whatsapp",
        "isn't on whatsapp", "is not on whatsapp", "not on whatsapp",
    ];

    /// <summary>A tela já declarou uma causa que vale mais do que fechar um aviso?</summary>
    /// <remarks>Uma passada só de <c>ToLowerInvariant</c>: o dump passa de 100 KB, e isto roda no
    /// momento em que o driver está prestes a TOCAR na tela.</remarks>
    private static bool TelaJaSeExplicou(string xml)
    {
        var tela = xml.ToLowerInvariant();
        return CasaPista(tela, PistasDeRestricao) || CasaPista(tela, PistasSemConta);
    }

    /// <summary>Casa por TRECHO, sem acento e em minúsculas, porque o texto exato muda com a versão do
    /// app e com o idioma do aparelho, e um casamento exato quebraria em silêncio na atualização.</summary>
    private static bool CasaPista(string telaMinuscula, string[] pistas) =>
        pistas.Any(p => telaMinuscula.Contains(p, StringComparison.Ordinal));

    private static bool TelaBloqueada(string xml) =>
        xml.Contains("com.android.systemui:id/keyguard", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("com.android.systemui:id/lock_icon", StringComparison.OrdinalIgnoreCase)
        || xml.Contains("com.android.systemui:id/lockscreen", StringComparison.OrdinalIgnoreCase);

    // ── Leitura de tela ──────────────────────────────────────────────────────────────────────────

    /// <summary>Árvore de acessibilidade da tela atual. null = não deu pra ler.</summary>
    /// <remarks>Não exige root, nem no aparelho físico — confirmado em 2026-07-29.</remarks>
    public async Task<string?> DumpUiAsync(CancellationToken ct)
    {
        // 🔴 APAGA ANTES DE DUMPAR. O caminho é fixo, então o arquivo sobrevive entre chamadas. Há
        // versões do `uiautomator` que falham ("could not get idle state"), imprimem o erro e ainda
        // saem com código 0 — e aí o `cat` devolveria o dump ANTERIOR, com a tela de antes passando
        // por tela de agora. Não medi este A14 fazendo isso, e provocar sob demanda é caro; apagar
        // antes torna a pergunta irrelevante, porque leitura obsoleta vira arquivo ausente, o `cat`
        // falha, e isto devolve null. Null aqui significa "não sei", que é tratado como incerteza em
        // todo mundo que lê a tela.
        // UM comando, não dois. Cada ShellAsync é um processo `adb` novo no PC, e ler a tela é a
        // operação mais repetida deste driver: os polls dumpam a cada 500ms, então um envio faz dezenas
        // de leituras. Juntar `dump` e `cat` numa linha corta METADE dos processos no caminho quente.
        //
        // O `rc` do dump deixa de ser consultado, e isso NÃO afrouxa a checagem de tela obsoleta que o
        // `rm -f` protege: sem o arquivo, um dump que falha faz o `cat` falhar, a saída vem vazia e este
        // método devolve null. É o mesmo desfecho de antes, por um caminho a menos.
        //
        // A saída do dump vai pro lixo porque ele imprime "UI hierchary dumped to: ..." em algumas
        // versões, e isso entraria concatenado ANTES do XML.
        var (_, xml, _) = await _adb.ShellAsync(
            $"rm -f {DumpPath}; uiautomator dump {DumpPath} >/dev/null 2>&1; cat {DumpPath}", ct);
        return string.IsNullOrWhiteSpace(xml) ? null : xml;
    }

    private static readonly Regex BoundsRx =
        new("bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"", RegexOptions.Compiled);

    private static readonly Regex NoRx = new("<node[^>]*>", RegexOptions.Compiled);

    /// <summary>Abertura, folha e FECHAMENTO de nó, que é o que permite contar profundidade.</summary>
    /// <inheritdoc cref="RaizesDeJanela"/>
    private static readonly Regex TagRx = new("<node[^>]*>|</node>", RegexOptions.Compiled);

    /// <summary>O app dono do nó. É o que separa uma janela do WhatsApp da barra de status e do
    /// teclado, que são de outros pacotes e não são aviso nenhum.</summary>
    private static readonly Regex PacoteRx = new("package=\"([^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex TextoRx = new("text=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>content-desc do nó: o rótulo de acessibilidade, único texto de botão só com ícone.</summary>
    private static readonly Regex DescRx = new("content-desc=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <inheritdoc cref="EhBotao"/>
    private static readonly Regex ClasseRx = new("class=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <inheritdoc cref="EhBotao"/>
    private static readonly Regex IdRx = new("resource-id=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>Rótulos de botão que apenas FECHAM um aviso, sem confirmar nada.</summary>
    /// <remarks>
    /// 🔴 LISTA FECHADA, e só palavra neutra. Tocar em botão pelo texto é tocar às cegas, e a tela pode
    /// ter "Bloquear", "Denunciar", "Sair do grupo". Um aviso mal dispensado é um aviso que volta; um
    /// botão errado tocado é irreversível e pode custar o contato ou a conta. Na dúvida, não toca:
    /// existe o BACK como saída, e ele não confirma nada.
    /// <para>Conjunto e não vetor, comparando sem caixa: o rótulo vem do dump em qualquer caixa, e a
    /// versão anterior chamava <c>ToLowerInvariant</c> em cada candidato só pra poder comparar.</para>
    /// </remarks>
    private static readonly HashSet<string> BotoesQueSoFecham = new(StringComparer.OrdinalIgnoreCase)
        { "ok", "continuar", "entendi", "fechar", "agora não", "agora nao", "got it", "continue", "close" };

    /// <summary>Nó do botão "enviar" e nó do campo de texto, os dois únicos que o driver procura.</summary>
    /// <remarks>
    /// Compilados e estáticos porque o poll roda a cada 500ms: montar o padrão por interpolação a cada
    /// volta reconstruía a expressão no caminho mais repetido do driver. Como são dois alvos fixos, não
    /// há por que aceitar resource-id em texto — e passar o padrão pronto tira de vez o risco de alguém
    /// montar um com caractere não escapado.
    /// <para>Casa o NÓ INTEIRO, e não o atributo isolado, porque o `text` vem ANTES do `resource-id` no
    /// dump: assim a ordem dos atributos deixa de importar e o texto sai de dentro do nó já encontrado.
    /// A aspa no fim do id não é enfeite — sem ela, <c>id/entry</c> casaria também um eventual
    /// <c>id/entry_qualquer_coisa</c>.</para>
    /// </remarks>
    private static readonly Regex SendNodeRx =
        new("<node[^>]*com\\.whatsapp:id/send\"[^>]*>", RegexOptions.Compiled);

    /// <inheritdoc cref="SendNodeRx"/>
    private static readonly Regex EntryNodeRx =
        new("<node[^>]*com\\.whatsapp:id/entry\"[^>]*>", RegexOptions.Compiled);

    /// <summary>Centro do nó procurado, com POLL até o timeout. null = não apareceu.</summary>
    /// <remarks>Poll e não sleep fixo: chat lento (cold start, rede ruim) abriria depois da espera e o
    /// envio seria abortado à toa.</remarks>
    private async Task<(int X, int Y)?> PollNodeCenterAsync(Regex no, int timeoutMs, CancellationToken ct) =>
        await PollNoAsync(no, timeoutMs, ct) is { } achado ? achado.Centro : null;

    /// <summary>O mesmo poll, devolvendo TAMBÉM a tela em que o nó foi encontrado.</summary>
    /// <remarks>
    /// 🔴 A TELA SAI JUNTO PELO MESMO MOTIVO DO <see cref="DiagnosticarChatNaoAbertoAsync"/>: quem
    /// achou o campo costuma precisar responder outra pergunta sobre a MESMA tela, e dumpar de novo
    /// custa outro processo `adb` e pode ler uma tela DIFERENTE — decidindo sobre uma e agindo sobre
    /// outra. Aqui isso valia um dump inteiro por envio: o poll achava o campo, e o
    /// <see cref="FecharAvisoSobreAConversaAsync"/> logo em seguida lia a tela outra vez pra perguntar
    /// se havia aviso na frente. Ler a tela é a operação mais cara e mais repetida deste driver.
    /// </remarks>
    private async Task<((int X, int Y) Centro, string Xml)?> PollNoAsync(
        Regex no, int timeoutMs, CancellationToken ct)
    {
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            if (await DumpUiAsync(ct) is { } xml
                && no.Match(xml) is { Success: true } achado
                && CentroDe(achado.Value) is { } centro)
            {
                return (centro, xml);
            }
            if (i < attempts)
            {
                await Task.Delay(500, ct);
            }
        }
        return null;
    }

    // ⚠️ CAMPO VAZIO NÃO É text="". O uiautomator devolve a DICA quando não há texto — medido nos DOIS
    // aparelhos: vazio sai como text="Mensagem". Comparar com string vazia nunca dá verdadeiro, e quem
    // dependesse disso ("já limpou", "enviou") ficaria preso pra sempre.
    //
    // O sinal confiável é o BOTÃO DE ENVIAR: o WhatsApp mostra o microfone com o campo vazio e troca
    // pelo botão de enviar quando tem texto. Não depende de idioma nem do texto da dica.

    /// <summary>O que a tela respondeu sobre o campo de mensagem. TRÊS estados, e o terceiro é o que
    /// importa.</summary>
    /// <remarks>
    /// 🔴 Isto era um bool, e o bool não tinha onde pôr "não consegui ler". O
    /// <c>HasSendButton(string? xml)</c> resolvia o nulo devolvendo false, e false ali significava
    /// "campo vazio", ou seja, ENVIO CONFIRMADO. Dump falho virava prova de entrega: o contato saía da
    /// lista, o CSV gravava "sim" e a pessoa podia não ter recebido nada.
    ///
    /// <para>E o agravante estava no relógio. O primeiro poll roda LOGO DEPOIS do toque, com a tela
    /// animando, que é quando o `uiautomator dump` mais falha ("could not get idle state"). Como o
    /// primeiro sucesso encerrava o laço, o dump com maior chance de falhar era justamente aquele cuja
    /// falha virava sucesso.</para>
    ///
    /// <para>O <see cref="PollNodeCenterAsync"/>, no mesmo arquivo, sempre tratou o nulo certo
    /// (<c>if (xml is not null)</c>). Eram dois tratamentos opostos da mesma incerteza a poucas linhas
    /// um do outro, e o daqui nunca foi decidido: veio junto com o operador de nulo.</para>
    /// </remarks>
    private enum EstadoDoCampo
    {
        /// <summary>Botão de enviar na tela: tem texto no campo.</summary>
        ComTexto,

        /// <summary>Tela LIDA, e sem botão de enviar: o campo está vazio.</summary>
        Vazio,

        /// <summary>Não deu pra ler a tela. NÃO é vazio, é desconhecido.</summary>
        NaoLido,
    }

    private static EstadoDoCampo LerEstadoDoCampo(string? xml) =>
        xml is null ? EstadoDoCampo.NaoLido
        : xml.Contains("com.whatsapp:id/send\"", StringComparison.Ordinal) ? EstadoDoCampo.ComTexto
        : EstadoDoCampo.Vazio;

    /// <summary>Depois de tocar enviar: o campo esvaziou?</summary>
    /// <returns>
    /// <see cref="EstadoDoCampo.Vazio"/> = envio confirmado.
    /// <see cref="EstadoDoCampo.ComTexto"/> = o texto continua lá, então NÃO saiu (conclusivo).
    /// <see cref="EstadoDoCampo.NaoLido"/> = nunca consegui ler a tela; não dá pra afirmar nada.
    /// </returns>
    private async Task<EstadoDoCampo> PollEntryClearedAsync(int timeoutMs, CancellationToken ct)
    {
        // Só vira conclusão quando ALGUMA leitura deu certo. Sem isso, um poll inteiro de dumps falhos
        // devolveria a mesma coisa que um poll que viu o texto parado no campo, e são fatos diferentes:
        // um é "não saiu", o outro é "não sei".
        var conclusao = EstadoDoCampo.NaoLido;
        var attempts = Math.Max(1, timeoutMs / 500);
        for (var i = 0; i <= attempts; i++)
        {
            var estado = LerEstadoDoCampo(await DumpUiAsync(ct));
            if (estado is EstadoDoCampo.Vazio)
            {
                return EstadoDoCampo.Vazio;
            }
            if (estado is EstadoDoCampo.ComTexto)
            {
                conclusao = EstadoDoCampo.ComTexto;
            }
            if (i < attempts)
            {
                await Task.Delay(500, ct);
            }
        }
        return conclusao;
    }

    private static string? EntryTextFrom(string? xml)
    {
        var node = xml is null ? null : EntryNodeRx.Match(xml);
        if (node is not { Success: true })
        {
            return null;
        }
        var t = TextoRx.Match(node.Value);
        // O dump escapa XML (&#10; etc). Desescapa pra o COMPRIMENTO bater com o texto original.
        return t.Success ? System.Net.WebUtility.HtmlDecode(t.Groups[1].Value) : "";
    }

    private async Task<string?> ReadEntryTextAsync(CancellationToken ct) =>
        EntryTextFrom(await DumpUiAsync(ct));

    private static readonly Regex StatusRx =
        new("resource-id=\"com.whatsapp:id/status\"[^>]*?content-desc=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>Entrega NORMALIZADA (locale-independente): sent | delivered | read | null.</summary>
    public async Task<string?> ReadLastMessageStatusAsync(CancellationToken ct)
    {
        var xml = await DumpUiAsync(ct);
        if (xml is null)
        {
            return null;
        }
        var raw = StatusRx.Matches(xml).Cast<Match>().LastOrDefault()?.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var r = raw.ToLowerInvariant();
        if (r.Contains("entreg", StringComparison.Ordinal) || r.Contains("deliver", StringComparison.Ordinal)) return "delivered";
        if (r.Contains("lida", StringComparison.Ordinal) || r.Contains("lido", StringComparison.Ordinal) || r.Contains("read", StringComparison.Ordinal)) return "read";
        if (r.Contains("enviad", StringComparison.Ordinal) || r.Contains("sent", StringComparison.Ordinal)) return "sent";
        return null; // desconhecido → não inventa entrega
    }

    // ── Toque e digitação ────────────────────────────────────────────────────────────────────────

    private async Task TapAsync((int X, int Y) p, CancellationToken ct)
    {
        // Desvio de alguns pixels: toque humano não acerta o centro geométrico duas vezes seguidas.
        var x = p.X + Random.Shared.Next(-6, 7);
        var y = p.Y + Random.Shared.Next(-4, 5);
        await _adb.ShellAsync($"input tap {x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)}", ct);
    }

    /// <summary>Por onde o texto entra no aparelho. null = não há caminho capaz de digitar ESTE texto.</summary>
    private enum TypingChannel
    {
        /// <summary>IME que aceita Unicode por broadcast (acento e emoji).</summary>
        Ime,

        /// <summary>`input text` do Android: só ASCII imprimível, e sem aspa simples.</summary>
        InputText,
    }

    // `input text` NÃO digita acento nem emoji — devolve NullPointerException (medido, Android 14).
    // A QUEBRA DE LINHA é exceção: ela não passa como texto, mas passa como TECLA (KEYCODE_ENTER), e
    // é assim que SendChunkAsync a envia. Confirmado com o operador em 2026-07-30 que a opção
    // "Tecla Enter para enviar" do WhatsApp está DESLIGADA neste aparelho — com ela ligada, o Enter
    // mandaria a mensagem pela metade.
    private static bool IsTypeableByInputText(string text) =>
        text.All(c => c == '\n' || (c is >= ' ' and <= '~' && c != '\''));

    private async Task<TypingChannel?> ResolveTypingChannelAsync(string text, CancellationToken ct)
    {
        if (await IsTypingImeReadyAsync(ct))
        {
            return TypingChannel.Ime;
        }
        return IsTypeableByInputText(text) ? TypingChannel.InputText : null;
    }

    private int _typingImeReady;

    /// <summary>O IME de digitação está instalado e habilitado?</summary>
    /// <remarks>No aparelho físico NÃO se instala nada por aqui: instalar APK à revelia num celular de
    /// uso real é invasivo, e o `input text` já cobre o caso do piloto (mensagem curta em ASCII, tipo
    /// "oi"). Se precisar de acento/emoji, instale o IME no aparelho e ele será detectado aqui.</remarks>
    private async Task<bool> IsTypingImeReadyAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _typingImeReady) == 1)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(_opts.TypingImePackage))
        {
            return false;
        }
        var (lc, list, _) = await _adb.ShellAsync("ime list -s", ct);
        if (lc != 0 || !(list ?? "").Contains(_opts.TypingImePackage, StringComparison.Ordinal))
        {
            return false;
        }
        Interlocked.Exchange(ref _typingImeReady, 1);
        return true;
    }

    /// <summary>Seleciona o teclado de digitação e devolve o que estava antes, pra ser restaurado.</summary>
    /// <remarks>Deixar este IME como padrão tira o teclado DA TELA (ele não desenha teclas, só recebe
    /// texto por broadcast). Quem for mexer no aparelho à mão ficaria sem conseguir digitar, e o sintoma
    /// ("o teclado sumiu") não apontaria pra cá. Por isso só em volta da digitação.</remarks>
    private async Task<string?> SelectTypingImeAsync(CancellationToken ct)
    {
        var (rc, current, _) = await _adb.ShellAsync("settings get secure default_input_method", ct);
        var previous = rc == 0 ? (current ?? "").Replace("\r", "").Replace("\n", "").Trim() : "";
        await _adb.ShellAsync($"ime set {_opts.TypingImeComponent}", ct);
        return string.IsNullOrWhiteSpace(previous) || previous == "null" ? null : previous;
    }

    /// <summary>Devolve o teclado de antes. SEM token do envio, de propósito.</summary>
    /// <remarks>
    /// 🔴 Isto recebia o token do envio e rodava num `finally`. Quando o envio acabava por CANCELAMENTO
    /// (Ctrl+C do operador, ou o teto de tempo estourando), o token já estava cancelado, o
    /// <c>DockerCli.RunAsync</c> relançava, e o `ime set` de volta simplesmente não acontecia. Ou seja:
    /// funcionava em todos os caminhos MENOS naqueles em que era necessário.
    ///
    /// <para>O estrago é no aparelho, não no lote. O IME de digitação não desenha teclas (ver
    /// <see cref="SelectTypingImeAsync"/>), então o celular ficava sem teclado utilizável para quem o
    /// pegasse na mão, e "o teclado sumiu" não aponta pra cá. Ctrl+C é o jeito documentado de
    /// interromper um lote, então esse era o caminho comum, não o raro.</para>
    ///
    /// <para>Token próprio com teto curto, e engolindo exceção: é limpeza, e limpeza não pode nem
    /// pendurar o encerramento nem mascarar o erro que o operador precisa ler.</para>
    /// </remarks>
    private async Task RestoreImeAsync(string? previous)
    {
        if (string.IsNullOrWhiteSpace(previous))
        {
            return;
        }
#pragma warning disable CA1031 // limpeza best-effort: ver o remarks
        try
        {
            using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _adb.ShellAsync($"ime set {previous}", restoreCts.Token);
        }
        catch
        {
            // aparelho sumiu: o teclado fica pro operador arrumar, e o erro do envio é o que importa
        }
#pragma warning restore CA1031
    }

    /// <summary>Esvazia o campo. false = não consegui confirmar que ficou vazio.</summary>
    private async Task<bool> ClearEntryAsync(TypingChannel channel, CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            // 🔴 SÓ `Vazio` encerra. Antes, dump falho também encerrava (o bool não separava "li e o
            // campo está vazio" de "não li"), e aí a digitação começava por cima de um campo que podia
            // estar cheio. Não ler é motivo pra dar outra volta, nunca pra concluir.
            if (LerEstadoDoCampo(await DumpUiAsync(ct)) is EstadoDoCampo.Vazio)
            {
                return true; // sem botão de enviar = campo vazio
            }
            if (channel is TypingChannel.Ime)
            {
                await _adb.ShellAsync("am broadcast -a ADB_CLEAR_TEXT", ct);
            }
            else
            {
                // Sem IME não há comando de limpar. SELECIONAR TUDO e apagar resolve em um golpe,
                // independente do tamanho: CTRL+A (keycombination existe do Android 11 pra cima) e um
                // backspace, que apaga a seleção inteira.
                var (kc, _, _) = await _adb.ShellAsync("input keycombination 113 29", ct);
                if (kc == 0)
                {
                    await _adb.ShellAsync("input keyevent 67", ct);
                }
                else
                {
                    // 🔴 Fallback pra Android antigo. 120 por volta × 12 voltas = 1440 caracteres, com
                    // folga sobre o maior template (~1000). Antes eram 40 por volta = teto de 480, que
                    // NÃO limpava um template e deixava o envio abortado com o campo sujo — e o campo
                    // sujo faz a digitação seguinte entrar em cima da anterior.
                    await _adb.ShellAsync("input keyevent " + string.Join(" ", Enumerable.Repeat("67", 120)), ct);
                }
            }
            await Task.Delay(300, ct);
        }
        return false;
    }

    /// <summary>Digita o texto.</summary>
    /// <returns>
    /// <c>Motivo</c> null = digitou tudo. <c>PodeTerSaido</c> = alguma coisa JÁ foi para o destinatário
    /// antes da parada, então abortar não desfaz nada e reenviar somaria uma segunda mensagem.
    /// </returns>
    private async Task<(string? Motivo, bool PodeTerSaido)> TypeInChunksAsync(
        TypingChannel channel, string text, CancellationToken ct)
    {
        var perChar = 60_000.0 / Math.Clamp(_opts.TypingCharsPerMinute, 60, 1200);
        var quebraConferida = false;
        foreach (var chunk in SplitForTyping(text))
        {
            if (!await SendChunkAsync(channel, chunk, ct))
            {
                return ("o aparelho recusou um trecho da digitação.", false);
            }

            // 🔴 Confere a PRIMEIRA quebra de linha: se a opção "Tecla Enter para enviar" estiver
            // ligada no WhatsApp, o Enter manda a mensagem em vez de quebrar a linha. Sem esta
            // checagem, um texto de 4 linhas viraria 4 mensagens picadas, uma por Enter. Aqui o
            // estrago para na primeira: aborta antes de digitar o resto.
            if (chunk == "\n" && !quebraConferida)
            {
                quebraConferida = true;
                if (string.IsNullOrEmpty(await ReadEntryTextAsync(ct)))
                {
                    // PodeTerSaido = true: o Enter MANDOU o que já estava digitado. Parar aqui evita as
                    // outras três mensagens picadas, mas a primeira já chegou ao destinatário.
                    return ("o campo esvaziou na primeira quebra de linha: a opção \"Tecla Enter para "
                        + "enviar\" está LIGADA no WhatsApp deste aparelho, então o Enter mandou a "
                        + "mensagem em vez de quebrar a linha. O começo do texto JÁ FOI ENVIADO. "
                        + "Desligue a opção (WhatsApp → Configurações → Conversas), ou use templates de "
                        + "uma linha só.", true);
                }
            }
            var waitMs = (int)(chunk.Length * perChar * (0.75 + (Random.Shared.NextDouble() * 0.7)));
            // De vez em quando alguém para pra pensar. Sem isso a cadência é uniforme demais.
            if (Random.Shared.Next(100) < 12)
            {
                waitMs += Random.Shared.Next(700, 2400);
            }
            await Task.Delay(waitMs, ct);
        }
        return (null, false);
    }

    private async Task<bool> SendChunkAsync(TypingChannel channel, string chunk, CancellationToken ct)
    {
        // Quebra de linha vai como TECLA, nos dois canais: nem o `input text` nem o broadcast do IME
        // carregam o caractere 10. É o Enter do teclado, que no campo do WhatsApp quebra a linha
        // enquanto "Tecla Enter para enviar" estiver desligado.
        if (chunk == "\n")
        {
            var (ec, _, _) = await _adb.ShellAsync("input keyevent 66", ct);
            return ec == 0;
        }
        if (channel is TypingChannel.Ime)
        {
            var (rc, _, _) = await _adb.ShellAsync($"am broadcast -a ADB_INPUT_TEXT --es msg {ShellQuote(chunk)}", ct);
            return rc == 0;
        }
        // `input text` não recebe espaço: o adb junta os argumentos, então o espaço vira separador.
        // %s é o marcador que o próprio `input` traduz de volta.
        var (tc, _, _) = await _adb.ShellAsync($"input text {ShellQuote(chunk.Replace(" ", "%s", StringComparison.Ordinal))}", ct);
        return tc == 0;
    }

    /// <summary>Aspas simples pro shell DO APARELHO (o adb junta os argumentos e o shell de lá reinterpreta).</summary>
    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // Trechos de 12 a 28 caracteres, cortando no espaço seguinte pra não partir palavra. Quebra de linha
    // vira fim de trecho: é onde uma pessoa naturalmente pausa.
    private static IEnumerable<string> SplitForTyping(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var target = Math.Min(text.Length, i + Random.Shared.Next(12, 29));
            var nl = text.IndexOf('\n', i);
            if (nl >= 0 && nl < target)
            {
                // A quebra sai SOZINHA no seu próprio trecho: SendChunkAsync a reconhece e manda a
                // tecla. Junto com o texto ela iria pro `input text`, que não a digita.
                if (nl > i)
                {
                    yield return text[i..nl];
                }
                yield return "\n";
                i = nl + 1;
                continue;
            }
            else
            {
                while (target < text.Length && text[target] != ' ')
                {
                    target++;
                }
                if (target < text.Length)
                {
                    target++; // leva o espaço junto
                }
            }
            yield return text[i..target];
            i = target;
        }
    }
}
