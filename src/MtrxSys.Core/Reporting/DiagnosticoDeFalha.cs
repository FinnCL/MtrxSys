using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Core.Reporting;

/// <summary>De quem é a culpa. É o eixo pelo qual um lote de 23 fracassos vira três linhas.</summary>
/// <remarks>
/// 🔴 EXISTE PORQUE A CAUSA É FINA DEMAIS PRA DECIDIR. <see cref="FalhaCausa"/> tem 13 valores, e é
/// certo que tenha: são 13 consertos diferentes quando se está na frente de UM contato. Mas quem lê o
/// fecho do lote de manhã não faz 13 perguntas, faz uma só, "é o aparelho, o chip ou a lista?", e é
/// essa pergunta que decide se o próximo lote roda hoje.
/// </remarks>
public enum GrupoDaFalha
{
    /// <summary>Saiu.</summary>
    Nenhum = 0,

    /// <summary>O número. A lista é que precisa de conserto.</summary>
    Numero,

    /// <summary>O chip. Não adianta trocar de contato nem de aparelho.</summary>
    Chip,

    /// <summary>O aparelho. Se repete no próximo contato, então parar cedo economiza a lista.</summary>
    Aparelho,

    /// <summary>Ninguém está errado, só está devagar.</summary>
    Lentidao,

    /// <summary>Configuração do sistema, e não do lote. Nenhum contato vai sair enquanto durar.</summary>
    Configuracao,

    /// <summary>Não deu pra saber se saiu. Pede uma pessoa, não um ajuste.</summary>
    Incerto,
}

/// <summary>Traduz a causa de uma falha em grupo, rótulo legível e o que fazer a respeito.</summary>
/// <remarks>
/// 🔴 MORA NO CORE, E NÃO JUNTO DA PLANILHA, por dois motivos. O primeiro é teste: o CLI não tem
/// projeto de teste (ver o aviso em EnvioPontaAPontaTests), e este mapa é exatamente o tipo de coisa
/// que se quebra em silêncio ao acrescentar uma causa nova. O segundo é reúso: o mesmo mapa serve
/// planilha, tela do console e, no dia em que existir, o relatório do painel.
///
/// <para>NÃO decide nada sozinho. <see cref="ContatoMorto"/> é a única pergunta com consequência, e
/// ela responde apenas sobre o CONTATO, nunca sobre o lote: parar o lote continua sendo assunto do
/// BatchStopPolicy, que enxerga a sequência e não uma falha isolada.</para>
/// </remarks>
public static class DiagnosticoDeFalha
{
    /// <summary>De quem é a culpa.</summary>
    public static GrupoDaFalha Grupo(FalhaCausa causa) => causa switch
    {
        FalhaCausa.Nenhuma => GrupoDaFalha.Nenhum,
        FalhaCausa.NumeroSemConta => GrupoDaFalha.Numero,
        FalhaCausa.ContaRestringida => GrupoDaFalha.Chip,
        FalhaCausa.ToqueNaoConfirmado => GrupoDaFalha.Incerto,
        FalhaCausa.Timeout => GrupoDaFalha.Lentidao,
        FalhaCausa.DigitacaoFalhou or FalhaCausa.EntradaInvalida or FalhaCausa.NaoSuportado
            => GrupoDaFalha.Configuracao,
        _ => GrupoDaFalha.Aparelho,
    };

    /// <summary>O grupo escrito como se lê, para cabeçalho e coluna.</summary>
    public static string RotuloDoGrupo(GrupoDaFalha grupo) => grupo switch
    {
        GrupoDaFalha.Nenhum => "",
        GrupoDaFalha.Numero => "número",
        GrupoDaFalha.Chip => "chip",
        GrupoDaFalha.Aparelho => "aparelho",
        GrupoDaFalha.Lentidao => "lentidão",
        GrupoDaFalha.Configuracao => "configuração",
        GrupoDaFalha.Incerto => "incerto",
        _ => "",
    };

    /// <summary>A causa escrita como se lê. Frase curta: é célula de planilha, não parágrafo.</summary>
    public static string Rotulo(FalhaCausa causa) => causa switch
    {
        FalhaCausa.Nenhuma => "",
        FalhaCausa.NumeroSemConta => "o app negou este número",
        FalhaCausa.ContaRestringida => "conta restringida",
        FalhaCausa.TelaBloqueada => "tela bloqueada",
        FalhaCausa.RascunhoPendente => "rascunho pendente na tela",
        FalhaCausa.ConversaNaoAbriu => "a conversa não abriu",
        FalhaCausa.TelaInesperada => "tela desconhecida na frente",
        FalhaCausa.DigitacaoFalhou => "a digitação falhou",
        FalhaCausa.TextoContinuouNoCampo => "o texto ficou no campo",
        FalhaCausa.ToqueNaoConfirmado => "toquei enviar e não confirmei",
        FalhaCausa.Timeout => "estourou o tempo",
        FalhaCausa.AdbFalhou => "o adb devolveu erro",
        FalhaCausa.EntradaInvalida => "número ou texto inválido",
        FalhaCausa.NaoSuportado => "este engine não envia pela UI",
        _ => "não classificado",
    };

    /// <summary>O passo seguinte de quem está lendo o relatório.</summary>
    /// <remarks>
    /// Uma ação por causa, e sempre uma ação de VERDADE. "Verifique o problema" não é ação: ocupa a
    /// coluna e devolve a pergunta para quem perguntou.
    /// </remarks>
    public static string OQueFazer(FalhaCausa causa) => causa switch
    {
        FalhaCausa.Nenhuma => "",
        // "todas as formas QUE EXISTEM", e não "as duas": AlternateBrazilianForm devolve null quando a
        // outra forma não existe (tirar o 9 de "11 9 2140-4487" cairia em faixa de fixo), e aí só uma
        // foi tentada. Prometer duas tentativas que não aconteceram manda o operador procurar um rastro
        // que não está no log.
        FalhaCausa.NumeroSemConta =>
            "confira o 9º dígito na origem. o app negou todas as formas do número que existem.",
        FalhaCausa.ContaRestringida =>
            "pare de disparar deste chip até normalizar. insistir é o que vira banimento.",
        FalhaCausa.TelaBloqueada =>
            "tire o bloqueio de tela do aparelho e ligue \"Permanecer ativo\" nas opções do desenvolvedor.",
        FalhaCausa.RascunhoPendente =>
            "alguém deixou texto numa conversa do aparelho. limpe e rode de novo.",
        FalhaCausa.ConversaNaoAbriu =>
            "nada saiu. o contato fica na lista para o próximo lote.",
        FalhaCausa.TelaInesperada =>
            "veja a tela salva citada no erro. se o aviso se repetir, ele precisa entrar na lista de botões que só fecham.",
        FalhaCausa.DigitacaoFalhou =>
            "instale o teclado no aparelho ou desligue Phone__HumanTyping.",
        FalhaCausa.TextoContinuouNoCampo =>
            "nada saiu, e isso é certo. o contato fica na lista para o próximo lote.",
        FalhaCausa.ToqueNaoConfirmado =>
            "abra esta conversa no aparelho antes de mandar de novo. a mensagem pode ter saído.",
        FalhaCausa.Timeout =>
            "aparelho lento ou travado. aumente o intervalo entre mensagens.",
        FalhaCausa.AdbFalhou =>
            "erro cru do adb. o texto na coluna de erro é a única pista.",
        FalhaCausa.EntradaInvalida =>
            "confira o número na lista e o texto do template.",
        FalhaCausa.NaoSuportado =>
            "este engine não envia pela UI. rode pelo aparelho físico.",
        _ => "causa não classificada. este envio veio de um log antigo, anterior à coluna de causa.",
    };

    /// <summary>Insistir neste contato só gasta lote?</summary>
    /// <remarks>
    /// 🔴 SÓ <see cref="FalhaCausa.NumeroSemConta"/>, e a lista curta é o ponto. Aqui a pergunta é se o
    /// CONTATO deve sair da fila, e só uma causa fala do contato: o app afirmou que não existe conta,
    /// depois de as duas formas do 9º dígito terem sido tentadas.
    ///
    /// <para>Tela travada, adb com erro e tempo estourado falam do APARELHO, e um aparelho ruim numa
    /// terça não diz nada sobre o número na quarta. Tirar esses da lista jogaria fora gente boa que
    /// nunca recebeu a mensagem, sem deixar rastro de que foi jogada fora.</para>
    ///
    /// <para>A conta restringida também não entra, e essa é a mais tentadora: com o chip restrito TODOS
    /// os contatos falham, então bastava um lote nesse estado para a lista inteira ser condenada por um
    /// problema que não é dela.</para>
    /// </remarks>
    public static bool ContatoMorto(FalhaCausa causa) => causa is FalhaCausa.NumeroSemConta;
}
