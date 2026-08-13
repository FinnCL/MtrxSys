namespace MtrxSys.Core.Safety;

/// <summary>Decide se vale tirar da lista os contatos que o app negou neste lote.</summary>
/// <remarks>
/// 🔴 EXISTE PORQUE TIRAR DA LISTA É A ÚNICA AÇÃO DESTRUTIVA DO CONSOLE. Todo o resto informa e devolve
/// a decisão pro operador; esta mexe na lista dele sozinha. Uma ação assim precisa de uma pergunta que
/// possa ser testada, e não de um <c>if</c> no meio de um laço de 500 linhas que ninguém consegue rodar.
///
/// <para>O perigo concreto: um chip sob restrição SILENCIOSA faz o WhatsApp responder "este número não
/// tem conta" para TODO mundo. O aparelho está bom, os números estão bons, e ainda assim o veredito
/// chega igual ao de um número morto de verdade. Sem esta guarda, um único lote nesse estado apagaria a
/// lista inteira, e o operador só descobriria depois.</para>
///
/// <para>A defesa individual (o espelho da agenda contradizendo o app) não cobre esse caso, porque ela
/// só fala quando o contato JÁ ESTÁ salvo e sincronizado no aparelho. Lista fria recém-colada não está,
/// e é justamente ela que se perderia. Sobra a defesa estatística, que é esta.</para>
/// </remarks>
public static class FaxinaDaLista
{
    /// <summary>Abaixo disto a proporção não significa nada: 2 de 3 é ruído, não padrão.</summary>
    public const int MinimoParaConcluir = 4;

    /// <summary>Vale tirar da lista os contatos negados?</summary>
    /// <param name="tentativas">Contatos que produziram algum resultado no lote: enviados mais falhas.</param>
    /// <param name="semConta">Quantos deles o app recusou dizendo que o número não tem conta.</param>
    /// <remarks>
    /// Metade é um limiar grosseiro DE PROPÓSITO, e a alternativa não seria melhor. Lista fria de
    /// verdade traz número morto na casa de 5 a 20%; passar de metade não distingue "lista muito ruim"
    /// de "app quebrado", e um limiar mais fino só daria aparência de precisão a um chute. Quando não
    /// dá pra distinguir, o lado seguro é não apagar nada: manter na lista custa uma tentativa no
    /// próximo lote, e apagar custa o contato.
    /// <para>O empate (exatamente metade) LIBERA a faxina. Metade morta ainda é plausível numa lista
    /// comprada, e o limiar precisa de um lado: pôr o empate no lado destrutivo seria escolher o
    /// prejuízo maior para o caso mais ambíguo.</para>
    /// </remarks>
    public static bool PodeSuspender(int tentativas, int semConta) =>
        tentativas < MinimoParaConcluir || semConta * 2 <= tentativas;

    /// <summary>Por que a faxina foi segurada. Vazio quando ela não foi.</summary>
    public static string MotivoDaRecusa(int tentativas, int semConta) =>
        PodeSuspender(tentativas, semConta)
            ? ""
            : $"mais da metade do lote ({semConta} de {tentativas}) foi recusado pela MESMA razão. "
              + "categoria única em bloco é sintoma de causa comum, não de lista ruim: pode ser o chip "
              + "sob restrição silenciosa, e aí os números estão bons.";
}
