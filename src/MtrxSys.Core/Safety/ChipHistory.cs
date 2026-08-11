namespace MtrxSys.Core.Safety;

/// <summary>Um dia de disparo de um aparelho, já resumido.</summary>
/// <param name="Enviadas">Mensagens que saíram.</param>
/// <param name="Recusadas">Números que o app negou ("sem conta").</param>
/// <param name="EntregasConfirmadas">Subconjunto de <paramref name="Enviadas"/> com tique na tela.</param>
public sealed record DiaDoChip(int Enviadas, int Recusadas, int EntregasConfirmadas);

/// <summary>Em que ponto da vida o chip está. Governa o quanto de cautela a tela recomenda.</summary>
public enum FaseDoChip
{
    /// <summary>Período de risco máximo. Ver <see cref="ChipHistory.DiasChipNovo"/>.</summary>
    Novo,

    /// <summary>Já tem histórico, ainda não tem folga.</summary>
    Aquecendo,

    /// <summary>Pode operar no platô. Ver <see cref="ChipHistory.DiasChipMaduro"/>.</summary>
    Maduro,
}

/// <summary>O que o histórico de um aparelho sugere para o disparo de hoje.</summary>
/// <param name="DiasAtivos">Dias distintos em que esse aparelho já disparou.</param>
/// <param name="Fase">Novo, aquecendo ou maduro.</param>
/// <param name="Sugestao">Quantas mensagens o histórico sugere hoje.</param>
/// <param name="Motivo">Por que essa sugestão, em uma frase, para aparecer na tela.</param>
public sealed record SugestaoDoChip(int DiasAtivos, FaseDoChip Fase, int Sugestao, string Motivo);

/// <summary>Deriva do histórico do próprio aparelho quantas mensagens sugerir hoje.</summary>
/// <remarks>
/// 🔴 DERIVADO, NUNCA TABELADO, e isso foi decisão explícita do operador em 2026-08-11. A tentação era
/// trazer a curva do <c>WarmupManager</c> (1, 2, 3, 4, 6, 8… 120) para o console. Recusada com razão:
/// aquela curva foi calibrada para o motor, que tem sinal de RESPOSTA, e o console não tem nenhum.
/// Número fixo vindo de outro contexto é palpite com cara de regra.
///
/// <para>🔴 SUGERE, NUNCA CORTA. Quem decide é o operador, e o console só põe o número na tela ANTES
/// do "confirmar". Mesma doutrina da checagem de agenda, que ficou em modo observação depois que o
/// operador mostrou que ela seguraria contato bom em massa.</para>
///
/// <para>A mecânica é a mais simples que responde ao dado: dia limpo sugere crescer sobre o que aquele
/// chip JÁ fez; dia ruim sugere metade. O chip encontra o próprio limite em vez de obedecer ao nosso.
/// Não há tabela em lugar nenhum deste arquivo.</para>
///
/// <para>Por que existe teto: sem ele, semanas boas seguidas empurrariam a sugestão para números que
/// ninguém defende. 120 é o platô que o resto do projeto já usa, então é o único número emprestado —
/// e ele é LIMITE, não alvo.</para>
/// </remarks>
public static class ChipHistory
{
    /// <summary>Platô do projeto. Limite da sugestão, não meta.</summary>
    public const int TetoSugestao = 120;

    /// <summary>Até quantos dias de disparo o chip é considerado NOVO.</summary>
    /// <remarks>
    /// Dez, e o número não é meu: as fontes de 2026 convergem em que número novo carrega risco MÁXIMO
    /// nos primeiros 10 dias, e recomendam aquecimento de 10 a 14 dias como padrão. É o único ponto em
    /// que vale importar número de fora, porque ele descreve o comportamento do WhatsApp, não uma
    /// preferência nossa.
    /// </remarks>
    public const int DiasChipNovo = 10;

    /// <summary>A partir de quantos dias de disparo o chip é considerado MADURO.</summary>
    /// <remarks>
    /// Vinte, e este vem de dentro: é onde a curva do <c>WarmupManager</c> chega ao platô de 120. Duas
    /// origens independentes (pesquisa externa e a curva feita à mão aqui) apontando para a mesma
    /// ordem de grandeza é o que dá alguma confiança nos dois.
    /// </remarks>
    public const int DiasChipMaduro = 20;

    /// <summary>Janela usada na conta do intervalo quando o operador não restringiu horário.</summary>
    /// <remarks>
    /// 🔴 NÃO USA 24H mesmo com a janela aberta. Ninguém dispara a noite toda, e espalhar o volume por
    /// 24h produziria intervalos calculados sobre um horário em que mandar mensagem é, por si só,
    /// comportamento de robô. Doze horas é o dia útil largo.
    /// </remarks>
    public const int JanelaPadraoHoras = 12;

    /// <summary>Piso do intervalo, em segundos, independente da conta.</summary>
    /// <remarks>
    /// As fontes apontam 1 mensagem por minuto como o teto onde a VELOCIDADE começa a pesar. 150s é
    /// 2,5 vezes mais folgado, e é o piso que o console já usava. Não há motivo pra chegar perto do
    /// teto: o ganho de vazão é pequeno e o risco não é.
    /// </remarks>
    public const int IntervaloMinimoSegundos = 150;

    /// <summary>Espalhamento em volta do centro. 40% pra cada lado.</summary>
    /// <remarks>
    /// É a proporção que o console já usava (150-360 tem centro 255). Intervalo apertado demais vira
    /// REGULARIDADE, que é assinatura de máquina — o mesmo motivo do jitter dos blocos e das pausas.
    /// </remarks>
    private const double Espalhamento = 0.4;

    /// <summary>A fase do chip, pelos dias em que ele já disparou.</summary>
    public static FaseDoChip FaseDe(int diasAtivos) =>
        diasAtivos >= DiasChipMaduro ? FaseDoChip.Maduro
        : diasAtivos > DiasChipNovo ? FaseDoChip.Aquecendo
        : FaseDoChip.Novo;

    /// <summary>Intervalo entre mensagens que ESPALHA o volume do dia pela janela.</summary>
    /// <remarks>
    /// 🔴 VOLUME E INTERVALO SÃO UM PARÂMETRO SÓ, e tratá-los separado foi o erro que passou
    /// despercebido: o console vinha com 150-360s, que é o ritmo de 120 mensagens em 8 horas, ou seja
    /// o ajuste de um chip no PLATÔ. Usado num chip novo com volume baixo, ele despacha o dia inteiro
    /// numa hora e depois silencia — concentração seguida de silêncio é mais parecido com máquina do
    /// que o mesmo volume espalhado.
    /// <para>A conta é a mais óbvia possível, e é isso que a torna defensável: divide a janela pelo
    /// número de mensagens. Se ela reproduz o ajuste que alguém escolheu à mão para o platô, e
    /// reproduz, então ela está descrevendo a mesma realidade.</para>
    /// </remarks>
    public static (int Min, int Max) IntervaloPara(int mensagensDoDia, int janelaHoras)
    {
        if (mensagensDoDia <= 0)
        {
            // Guarda, não caminho real: quem chama sempre passa pelo menos 1 (a sugestão nunca é zero e
            // o console usa Math.Max). Devolve o par HISTÓRICO do console em vez de uma conta com
            // divisão por zero disfarçada — antes havia aqui uma fórmula que produzia 350 e não
            // significava nada.
            return (IntervaloMinimoSegundos, 360);
        }
        var janela = Math.Clamp(janelaHoras <= 0 ? JanelaPadraoHoras : janelaHoras, 1, JanelaPadraoHoras);
        var centro = (double)janela * 3600 / mensagensDoDia;
        var min = Math.Max(IntervaloMinimoSegundos, (int)Math.Round(centro * (1 - Espalhamento)));
        var max = Math.Max(min + 1, (int)Math.Round(centro * (1 + Espalhamento)));
        return (min, max);
    }

    /// <summary>Sugestão para aparelho sem histórico nenhum.</summary>
    /// <remarks>
    /// As fontes de 2026 convergem em que número novo tem risco MÁXIMO nos primeiros 10 dias, e o
    /// <c>WarmupManager</c> deste repositório registra quatro chips perdidos, dois com DUAS mensagens e
    /// um com ZERO. Não existe número seguro; existe número pequeno o bastante para o estrago ser
    /// pequeno se der errado.
    /// </remarks>
    public const int SugestaoChipNovo = 2;

    /// <summary>Cresce sobre o último dia limpo. Modesto de propósito: dobrar todo dia chegaria ao
    /// platô numa semana, que é a pressa que queima chip.</summary>
    private const double Crescimento = 1.3;

    /// <summary>Acima disto, o dia deixa de ser "limpo". Recusa é o sinal mais barato que o console
    /// tem, e recusa em massa costuma preceder problema maior.</summary>
    private const double RecusaAceitavel = 0.2;

    /// <param name="diasAtivos">Dias distintos em que o aparelho já disparou, incluindo hoje.</param>
    /// <param name="ultimoDia">O resumo do último dia com disparo. null = nunca disparou.</param>
    public static SugestaoDoChip Sugerir(int diasAtivos, DiaDoChip? ultimoDia)
    {
        if (ultimoDia is null || ultimoDia.Enviadas == 0)
        {
            // 🔴 A FRASE MUDA CONFORME O MOTIVO. "Sem histórico" para um aparelho que já disparou HOJE
            // seria contradição na mesma tela: o painel logo acima mostra os dias e o que já saiu. Sem
            // dia FECHADO, a diferença é entre "nunca disparou" e "disparou, mas ainda não terminou um
            // dia" — e a segunda não autoriza crescer, porque não se sabe como o dia acaba.
            var semNenhum = diasAtivos <= 0;
            return new SugestaoDoChip(
                Math.Max(0, diasAtivos),
                FaseDe(diasAtivos),
                SugestaoChipNovo,
                semNenhum
                    ? "aparelho sem histórico de envio. os primeiros 10 dias são o período de maior "
                      + "risco para um número, então o começo pequeno é o que limita o estrago se algo "
                      + "der errado"
                    : "ainda não há um dia FECHADO para servir de base: um dia pela metade não diz como "
                      + "ele termina, então a sugestão continua a de aparelho novo");
        }

        var total = ultimoDia.Enviadas + ultimoDia.Recusadas;
        var taxaRecusa = total == 0 ? 0 : (double)ultimoDia.Recusadas / total;

        if (taxaRecusa > RecusaAceitavel)
        {
            // Metade, e não parada: recusa alta pode ser lista ruim, que não diz nada sobre o chip.
            // Encolher responde ao sinal sem tratar suspeita como certeza.
            var metade = Math.Max(1, ultimoDia.Enviadas / 2);
            return new SugestaoDoChip(
                diasAtivos,
                FaseDe(diasAtivos),
                metade,
                $"o último dia teve {ultimoDia.Recusadas} recusa(s) em {total} tentativa(s), "
                + $"{taxaRecusa:P0} — acima disso vale encolher e observar antes de voltar a crescer");
        }

        var crescido = Math.Min(TetoSugestao, (int)Math.Round(ultimoDia.Enviadas * Crescimento));
        return new SugestaoDoChip(
            diasAtivos,
            FaseDe(diasAtivos),
            Math.Max(1, crescido),
            $"o último dia saiu limpo ({ultimoDia.Enviadas} enviada(s), {ultimoDia.Recusadas} recusa(s)), "
            + "então dá pra crescer devagar sobre o que este chip já fez");
    }
}
