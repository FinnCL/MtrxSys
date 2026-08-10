namespace MtrxSys.Core.Safety;

/// <summary>O que fazer diante da SEQUÊNCIA de falhas de um lote de envio pelo aparelho.</summary>
/// <remarks>
/// 🔴 A REGRA CENTRAL: falha NÃO interrompe o lote. Decisão do operador em 2026-08-07, com o caso na
/// mão — num lote de 30, três números na forma errada, seguidos, derrubaram a execução em 22/30 com o
/// aparelho perfeito, e 15 contatos bons ficaram sem receber. O argumento que decidiu: se falhou, nada
/// saiu; mostrar a falha e ir para o próximo contato não custa entrega nenhuma, e travar custa o resto
/// da lista. Contato que falha continua na lista, então nada se perde ao seguir.
///
/// <para>O que sobrou no lugar da parada, porque o custo de seguir é real e não foi ignorado:</para>
/// <list type="number">
/// <item><b>Ritmo</b> (<see cref="InFailureStreak"/>): abrir conversa atrás de conversa para número
/// que não existe é o padrão de bot enumerando, e é a RAJADA que pesa contra o chip, não a falha
/// isolada. Falha avulsa segue rápida; virando sequência, o lote volta ao intervalo normal e o desenho
/// se desfaz.</item>
/// <item><b>Aviso</b> (<see cref="AcabouDeAcusarAparelho"/>): falha de APARELHO é a única que prevê o
/// próximo contato, porque tela bloqueada e WhatsApp fechado continuam lá. Não para mais o lote, mas
/// grita uma vez, para o operador poder resolver em vez de descobrir no fim que nada saiu.</item>
/// <item><b>Teto opcional</b> (<paramref name="failureLimit"/>): zero, o padrão, é "nunca pare". Quem
/// quiser o comportamento antigo pede.</item>
/// </list>
///
/// <para>Vive aqui, e não dentro do laço do console, porque é decisão com estado e casos de borda, e o
/// projeto do CLI não tem como ser testado.</para>
///
/// <para>Não é o <see cref="CircuitBreaker"/> do Dispatcher: aquele é persistente, guarda estado no
/// banco e tem janela de reabertura. Este é em memória e vale por lote.</para>
/// </remarks>
/// <param name="failureLimit">Falhas seguidas que interrompem o lote. ZERO = nunca interrompe.</param>
public sealed class BatchStopPolicy(int failureLimit)
{
    /// <summary>Falhas de aparelho seguidas a partir das quais vale gritar, e o mesmo limiar que
    /// define "isto virou sequência" para o ritmo. Um número só, para o operador não ter que guardar
    /// dois. É o mesmo do <c>CircuitBreaker.FailureThreshold</c> do Dispatcher.</summary>
    public const int LimiarSequencia = 3;

    private readonly int _teto = Math.Max(0, failureLimit);

    /// <summary>Falhas de aparelho desde a última entrega.</summary>
    /// <remarks>
    /// NÃO é zerado por uma falha de número. Se fosse, um lote com números mortos intercalados
    /// esconderia um celular que está falhando: cada falha de número apagaria o rastro, e o aviso
    /// nunca sairia.
    /// </remarks>
    public int ConsecutiveDeviceFailures { get; private set; }

    /// <summary>Números sem conta no WhatsApp desde a última entrega.</summary>
    public int ConsecutiveNoAccount { get; private set; }

    /// <summary>Falhas de qualquer tipo desde a última entrega.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Entregou: zera tudo. Uma entrega no meio prova as duas coisas de uma vez, que o
    /// aparelho está bom e que a lista não é toda lixo.</summary>
    public void Delivered()
    {
        ConsecutiveDeviceFailures = 0;
        ConsecutiveNoAccount = 0;
        ConsecutiveFailures = 0;
    }

    /// <summary>Falha que acusa o aparelho. Inclui o envio NÃO CONFIRMADO: "toquei enviar e não
    /// consegui confirmar" fala da leitura de tela, não do número.</summary>
    public void DeviceFailure()
    {
        ConsecutiveDeviceFailures++;
        ConsecutiveFailures++;
    }

    /// <summary>O app afirmou que este número não tem conta no WhatsApp.</summary>
    public void NoAccount()
    {
        ConsecutiveNoAccount++;
        ConsecutiveFailures++;
    }

    /// <summary>Já é uma SEQUÊNCIA de falhas, e não uma falha isolada? Governa o RITMO.</summary>
    public bool InFailureStreak => ConsecutiveFailures >= LimiarSequencia;

    /// <summary>Cruzou AGORA o limiar de falhas de aparelho: hora de gritar, uma vez só.</summary>
    /// <remarks>
    /// Igualdade e não "maior ou igual" de propósito: repetir o mesmo alerta a cada contato vira ruído
    /// que a pessoa aprende a pular, e junto com ele ela pula o resto da tela.
    /// </remarks>
    public bool AcabouDeAcusarAparelho => ConsecutiveDeviceFailures == LimiarSequencia;

    /// <summary>Cruzou AGORA o limiar de números NEGADOS seguidos: a suspeita deixou de ser da lista e
    /// passou a ser do conjunto. Grita uma vez só, pela mesma razão do alerta de aparelho.</summary>
    /// <remarks>
    /// 🔴 O CONTADOR EXISTIA E NINGUÉM LIA. <see cref="ConsecutiveNoAccount"/> era incrementado, tinha
    /// teste, e nenhum consumidor em produção: uma lista inteira sendo recusada rodava até o fim sem
    /// alarme nenhum. MEDIDO operando em 2026-08-10 — dois contatos seguidos negados nas duas formas do
    /// número, ambos JÁ na agenda do aparelho, e só a desconfiança do operador interrompeu o lote.
    ///
    /// <para>Por que 3 negados seguidos merecem alarme, se um negado é rotina: um número morto não
    /// prevê nada sobre o próximo (é por isso que ele NÃO alimenta o alerta de aparelho). Três seguidos
    /// preveem — a chance de três números independentes estarem mortos em sequência é baixa perto da
    /// chance de haver uma causa comum: lista de origem ruim, ou o cache do WhatsApp envenenado por
    /// contato que um dia foi gravado na agenda em forma que o app não resolve (ver
    /// <c>WhatsAppContactsReader.SaveContactAsync</c>, medido em 2026-08-05).</para>
    ///
    /// <para>E o custo de seguir sem saber é o padrão que este arquivo inteiro tenta evitar: cada
    /// contato negado abre conversa, e conversa atrás de conversa para número que não existe é
    /// enumeração. Numa lista de 87 são até 174 aberturas sem uma entrega.</para>
    ///
    /// <para>AVISA, não trava — mesma doutrina do alerta de aparelho e mesma decisão do operador em
    /// 2026-08-07. Quem quiser parada automática já tem o <c>parar N</c>.</para>
    /// </remarks>
    public bool AcabouDeAcusarLista => ConsecutiveNoAccount == LimiarSequencia;

    /// <summary>Parar agora? Só quando o operador pediu um teto explícito.</summary>
    public bool ShouldStop => _teto > 0 && ConsecutiveFailures >= _teto;
}
