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

    /// <summary>Entregas do lote INTEIRO. Diferente dos outros contadores, este NÃO zera.</summary>
    /// <remarks>
    /// 🔴 O QUE ELE RESPONDE: "esta conta já provou que resolve número hoje?". Uma entrega exige que o
    /// WhatsApp tenha resolvido o destinatário e aceitado a mensagem, então uma única entrega no lote é
    /// prova de que o canal funciona.
    /// </remarks>
    public int TotalDelivered { get; private set; }

    /// <summary>O lote ainda não entregou NADA. É o que muda o PESO de uma sequência de recusas.</summary>
    /// <remarks>
    /// 🔴 A MESMA sequência de 3 recusas significa coisas MUITO diferentes conforme o que veio antes:
    /// <list type="bullet">
    /// <item>Depois de 20 entregas: a conta resolve número, então a causa provável são aqueles três
    /// números. É o caso comum, e travar ou gritar alto aqui era o que atrapalhava o operador.</item>
    /// <item>Com ZERO entregas no lote inteiro: nada resolveu ainda, nem uma vez. Aí a suspeita
    /// legítima deixa de ser "três números ruins" e passa a ser algo comum a todos.</item>
    /// </list>
    /// <para>Este contador não elimina a ambiguidade (só o aparelho saber o estado da conta
    /// eliminaria), mas usa informação que o lote JÁ TINHA e ninguém lia para separar o alarme forte do
    /// fraco. Reduzir falso positivo com dado existente é mais barato que qualquer sonda.</para>
    /// </remarks>
    public bool NadaEntregouAinda => TotalDelivered == 0;

    /// <summary>A suspeita recai sobre a CONTA (e não sobre a lista)?</summary>
    /// <remarks>
    /// 🔴 <see cref="TotalDelivered"/> é fato sobre o PASSADO, e restrição pode começar no MEIO do
    /// lote. Um lote que entregou 12 nas duas primeiras horas e depois emenda 40 recusas não autoriza
    /// dizer "a conta resolve número": autorizava duas horas atrás. Sem esta segunda condição, a
    /// mensagem branda tranquilizaria justamente no cenário em que o chip acabou de cair — e
    /// tranquilizar errado é pior que calar.
    ///
    /// <para>Por isso só o PRIMEIRO alerta (a sequência ainda no limiar) pode ser brando. Qualquer
    /// repetição significa que a sequência passou de {LimiarSequencia} e continuou crescendo, e aí o
    /// que veio antes deixa de explicar o que está acontecendo agora.</para>
    /// </remarks>
    public bool SuspeitaRecaiSobreAConta =>
        NadaEntregouAinda || ConsecutiveNoAccount > LimiarSequencia;

    /// <summary>Entregou: zera as sequências. Uma entrega no meio prova as duas coisas de uma vez, que
    /// o aparelho está bom e que a lista não é toda lixo.</summary>
    public void Delivered()
    {
        TotalDelivered++;
        ConsecutiveDeviceFailures = 0;
        ConsecutiveNoAccount = 0;
        ConsecutiveFailures = 0;
        ConsecutiveContradicoes = 0;
    }

    /// <summary>Falha que acusa o aparelho. Inclui o envio NÃO CONFIRMADO: "toquei enviar e não
    /// consegui confirmar" fala da leitura de tela, não do número.</summary>
    public void DeviceFailure()
    {
        ConsecutiveDeviceFailures++;
        ConsecutiveFailures++;
    }

    /// <summary>O app afirmou que este número não tem conta no WhatsApp.</summary>
    /// <param name="contradizidoPelaAgenda">O ESPELHO que o próprio WhatsApp publica na agenda do
    /// Android diz que este número É usuário. Ver <see cref="ConsecutiveContradicoes"/>.</param>
    public void NoAccount(bool contradizidoPelaAgenda = false)
    {
        ConsecutiveNoAccount++;
        ConsecutiveFailures++;
        if (contradizidoPelaAgenda)
        {
            ConsecutiveContradicoes++;
        }
    }

    /// <summary>Recusas em que o app se CONTRADISSE: disse "não tem conta" para um número que o espelho
    /// dele mesmo, na agenda do Android, marca como usuário do WhatsApp.</summary>
    /// <remarks>
    /// 🔴 É A ÚNICA EVIDÊNCIA DE GRAU DE CERTEZA que existe sem root e sem WhatsApp Web. Duas fontes
    /// independentes descrevem o mesmo fato e discordam: o deep link diz que o número não tem conta, e
    /// o `vnd.com.whatsapp.profile` publicado pelo sync de contatos diz que tem. Um número que o
    /// próprio app marcou como usuário não deixa de ser usuário de um dia pro outro — então quem está
    /// errado é a resposta de agora, e o motivo de ela estar errada é a conta ter parado de resolver.
    ///
    /// <para>NÃO zera numa recusa comum, só numa entrega. Uma lista pode intercalar número morto de
    /// verdade (espelho não sabe) com número contradito; zerar no meio apagaria justamente o rastro que
    /// interessa. Entrega zera porque ela PROVA que a conta resolve.</para>
    ///
    /// <para>A assimetria que torna isto seguro: <c>IsOnWhatsAppAsync</c> nunca devolve false, só true
    /// ou null. Então esta contagem só consegue produzir evidência A FAVOR de restrição, jamais um
    /// "está tudo bem" falso. Foi o defeito fatal da ideia do canário, e aqui ele não existe.</para>
    /// </remarks>
    public int ConsecutiveContradicoes { get; private set; }

    /// <summary>Limiar de contradições. DOIS, e não três como o resto: a evidência é qualitativamente
    /// mais forte, porque não depende de estimar probabilidade de coincidência. Uma contradição isolada
    /// ainda pode ser espelho velho (alguém que saiu do WhatsApp recentemente); duas, em números
    /// independentes e sem nenhuma entrega no meio, não.</summary>
    public const int LimiarContradicao = 2;

    /// <summary>O app se contradisse o bastante: a conta muito provavelmente parou de resolver número.</summary>
    public bool ContaProvavelmenteRestrita => ConsecutiveContradicoes >= LimiarContradicao;

    /// <summary>Cruzou AGORA o limiar de contradições: hora de dizer, com todas as letras.</summary>
    public bool AcabouDeConfirmarContradicao => ConsecutiveContradicoes == LimiarContradicao;

    /// <summary>Já é uma SEQUÊNCIA de falhas, e não uma falha isolada? Governa o RITMO.</summary>
    public bool InFailureStreak => ConsecutiveFailures >= LimiarSequencia;

    /// <summary>Cruzou AGORA o limiar de falhas de aparelho: hora de gritar, uma vez só.</summary>
    /// <remarks>
    /// Igualdade e não "maior ou igual" de propósito: repetir o mesmo alerta a cada contato vira ruído
    /// que a pessoa aprende a pular, e junto com ele ela pula o resto da tela.
    /// </remarks>
    public bool AcabouDeAcusarAparelho => ConsecutiveDeviceFailures == LimiarSequencia;

    /// <summary>De quantas em quantas recusas o alerta se repete depois de disparar a primeira vez.</summary>
    private const int RepeteAviso = 10;

    /// <summary>Hora de alertar sobre números NEGADOS em sequência. Dispara no limiar e volta a cada
    /// <see cref="RepeteAviso"/> recusas, enquanto a sequência durar.</summary>
    /// <remarks>
    /// 🔴 O CONTADOR EXISTIA E NINGUÉM LIA. <see cref="ConsecutiveNoAccount"/> era incrementado, tinha
    /// teste, e nenhum consumidor em produção: uma lista inteira sendo recusada rodava até o fim sem
    /// alarme nenhum. MEDIDO operando em 2026-08-10 — dois contatos seguidos negados nas duas formas do
    /// número, ambos JÁ na agenda do aparelho, e só a desconfiança do operador interrompeu o lote.
    ///
    /// <para>Por que 3 negados seguidos merecem alarme, se um negado é rotina: um número morto não
    /// prevê nada sobre o próximo (é por isso que ele NÃO alimenta o alerta de aparelho). Três seguidos
    /// preveem — a chance de três números independentes estarem mortos em sequência é baixa perto da
    /// chance de haver causa comum: lista de origem ruim, cache do WhatsApp envenenado (ver
    /// <c>WhatsAppContactsReader.SaveContactAsync</c>, medido em 2026-08-05), ou a CONTA restringida,
    /// que deixa de resolver número e faz TODO contato voltar como "sem conta".
    ///
    /// <para>AVISA, NÃO TRAVA. Cheguei a fazer isto PARAR o lote, e recuei: o operador tem muitos
    /// lotes com três recusas seguidas sem restrição nenhuma, e travar interrompia o fluxo justamente
    /// no caso comum. Some-se que o dano de seguir é menor do que eu supus — o
    /// <see cref="InFailureStreak"/> já devolve o ritmo normal depois de 3 falhas, então o lote não
    /// metralha, ele espalha. Quem quiser parada automática tem o <c>parar N</c>.</para>
    ///
    /// <para>🔴 REPETE, ao contrário do <see cref="AcabouDeAcusarAparelho"/>, e a diferença é
    /// deliberada. Lá "gritar uma vez" evita ruído. Aqui, com a conta restrita, um único aviso no 3º
    /// contato deixaria os 84 seguintes em SILÊNCIO: quem chegasse na frente da tela no meio do lote
    /// não veria nada e concluiria que está tudo bem. Espaçar em {RepeteAviso} é o meio-termo entre
    /// virar ruído e sumir.</para>
    ///
    /// <para>Com a parada fora, um alarme falso passa a custar três linhas na tela em vez de um lote
    /// interrompido — e é isso que permite manter o limiar sensível em 3.</para>
    /// </remarks>
    /// <remarks>
    /// <para>🔴 A SEGUNDA CLÁUSULA É A TRANSIÇÃO, e ela faltava. Com entregas antes, o alerta do limiar
    /// sai BRANDO ("a conta resolveu número há pouco, provavelmente são esses três"). Se a sequência
    /// continua, <see cref="SuspeitaRecaiSobreAConta"/> vira true na recusa seguinte — mas o aviso só
    /// voltaria em {RepeteAviso} contatos, ou seja, meia hora depois no ritmo normal. O MOMENTO
    /// informativo é exatamente esse: a hipótese branda acabou de ser desmentida, e o operador ficaria
    /// sem saber. Avisar na virada não é ruído, é a notícia.</para>
    /// <para>Sem entregas antes o limiar já sai forte, então a cláusula não dispara e não há aviso
    /// repetido em sequência.</para>
    /// </remarks>
    public bool DeveAlertarRecusas =>
        ConsecutiveNoAccount == LimiarSequencia
        || (ConsecutiveNoAccount == LimiarSequencia + 1 && TotalDelivered > 0)
        || (ConsecutiveNoAccount > LimiarSequencia
            && (ConsecutiveNoAccount - LimiarSequencia) % RepeteAviso == 0);

    /// <summary>Parar agora? Só quando o operador pediu um teto explícito.</summary>
    public bool ShouldStop => _teto > 0 && ConsecutiveFailures >= _teto;
}
