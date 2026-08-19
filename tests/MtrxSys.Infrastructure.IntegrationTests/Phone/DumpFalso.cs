using System.Globalization;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Os pedaços de um `uiautomator dump` de mentira, na forma em que o de verdade sai.</summary>
/// <remarks>
/// 🔴 CADA JANELA É UMA RAIZ. O dump não é uma árvore só: é a concatenação das JANELAS da tela como
/// filhas de `&lt;hierarchy&gt;`. A conversa é uma janela; um diálogo por cima dela é OUTRA. Os falsos
/// emitiam tudo achatado, uma lista de nós soltos sem raiz nenhuma, e nessa forma não existe a pergunta
/// "o campo e este botão estão na mesma janela?" — que é a pergunta de que a decisão do driver depende.
/// Modelar plano era esconder do teste a estrutura sob julgamento.
///
/// <para>Mora aqui, e não em cada falso, porque são DOIS falsos com propósitos diferentes (o
/// <see cref="AndroidFalso"/>, que é máquina de estados, e o <see cref="AdbDeTelaFixa"/>, que congela
/// uma tela) e uma forma de dump só. Duas cópias divergiriam no primeiro ajuste, e aí um dos dois
/// passaria a testar uma tela que o aparelho não produz.</para>
/// </remarks>
internal static class DumpFalso
{
    /// <summary>Envolve os nós numa raiz de janela do WhatsApp, que é como o dump entrega cada uma.</summary>
    public static string Janela(string dentro) =>
        "<node index=\"0\" class=\"android.widget.FrameLayout\" package=\"com.whatsapp\" "
        + "bounds=\"[0,0][1080,2400]\">" + dentro + "</node>";

    /// <summary>O campo de mensagem da conversa.</summary>
    /// <remarks>⚠️ Campo vazio devolve a DICA, não string vazia: é o comportamento medido nos dois
    /// aparelhos, e a razão de o sinal confiável de "tem texto" ser o botão de enviar.</remarks>
    public static string CampoDeMensagem(string texto = "Mensagem") =>
        string.Create(CultureInfo.InvariantCulture,
            $"<node text=\"{texto}\" resource-id=\"com.whatsapp:id/entry\" package=\"com.whatsapp\" "
            + $"class=\"android.widget.EditText\" bounds=\"[{CampoX1},{CampoY1}][{CampoX2},{CampoY2}]\"/>");

    public const int CampoX1 = 50;
    public const int CampoY1 = 1800;
    public const int CampoX2 = 880;
    public const int CampoY2 = 1900;
}
