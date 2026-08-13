using System.Globalization;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Core.Reporting;

/// <summary>Os três desfechos que o log de envios distingue.</summary>
/// <remarks>
/// 🔴 TRÊS, E NÃO DOIS. O CSV grava "sim", "incerto" e "nao" desde que o resultado do driver parou de
/// ser um bool, e o meio é o que existe justamente porque não cabe nos outros dois: no
/// <see cref="Incerto"/> o toque em enviar já aconteceu e ninguém conseguiu ler a tela pra confirmar.
/// Achatá-lo em enviado faria riscar da lista quem talvez não recebeu; achatá-lo em não-enviado faria
/// mandar de novo pra quem talvez já recebeu, que é o pior desfecho possível com contato frio.
/// </remarks>
public enum ResultadoDoEnvio
{
    /// <summary>Saiu, confirmado pelo campo esvaziado.</summary>
    Enviado,

    /// <summary>Toquei enviar e não deu pra confirmar. Pode ter saído.</summary>
    Incerto,

    /// <summary>Não saiu, e isso é conclusivo.</summary>
    NaoSaiu,
}

/// <summary>Uma tentativa de envio, como o CSV a guardou.</summary>
/// <param name="Quando">Instante do registro, com o offset em que foi gravado.</param>
/// <param name="Numero">O número que REALMENTE foi usado, não necessariamente o que estava na lista.</param>
/// <param name="Variante">Índice do template sorteado, começando em 1.</param>
/// <param name="Entrega">"sent" | "delivered" | "read" | null.</param>
/// <param name="Contradito">O app negou o número mas a agenda do aparelho diz que ele É usuário.</param>
/// <param name="Abertura">"registro" (pelo contato salvo) ou "numero" (pelo deep link).</param>
/// <param name="Causa">
/// <see cref="FalhaCausa.Nenhuma"/> no sucesso, e também nas linhas de log anteriores à coluna de causa.
/// </param>
public sealed record LinhaDeEnvio(
    DateTimeOffset Quando,
    string Numero,
    string? Nome,
    int Variante,
    ResultadoDoEnvio Resultado,
    string? Entrega,
    string? Erro,
    string Texto,
    bool Contradito,
    string Abertura,
    FalhaCausa Causa)
{
    /// <summary>O dia a que esta linha pertence, como "2026-08-13".</summary>
    /// <remarks>
    /// No offset em que foi GRAVADA, e não convertido para o fuso de quem lê. O log é do aparelho e do
    /// operador que estava na frente dele; um relatório aberto noutro fuso não pode mover envios de dia
    /// e, com isso, mexer na curva de aquecimento. É a mesma data que o LerLog obtém cortando os 10
    /// primeiros caracteres do carimbo ISO-8601.
    /// </remarks>
    public string Dia => Quando.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>O tique de entrega já tinha aparecido na tela quando o log foi escrito.</summary>
    /// <remarks>
    /// Só "delivered" e "read". "sent" é o estado normal segundos depois do toque, e contá-lo como
    /// entrega faria a planilha afirmar recebimento que ninguém viu.
    /// </remarks>
    public bool EntregaConfirmada =>
        Entrega is "delivered" or "read";

    /// <summary>Gastou cota do chip?</summary>
    /// <remarks>
    /// Inclui o <see cref="ResultadoDoEnvio.Incerto"/>, e tem que incluir: a conversa foi aberta e a
    /// mensagem pode ter saído. É a MESMA regra do laço do lote e da releitura do CSV, e as três
    /// precisam concordar, senão o teto do dia seguinte sai menor sem explicação.
    /// </remarks>
    public bool GastouCota => Resultado is ResultadoDoEnvio.Enviado or ResultadoDoEnvio.Incerto;
}

/// <summary>Quantas falhas de cada causa, e que fatia do total elas são.</summary>
public sealed record ResumoPorCausa(FalhaCausa Causa, int Quantidade, double Fracao);

/// <summary>Um dia de trabalho do chip.</summary>
public sealed record ResumoPorDia(string Dia, int Enviadas, int NaoSairam, int EntregasConfirmadas);

/// <summary>O que o recorte de linhas produziu, já contado.</summary>
/// <param name="Tentativas">Conversas abertas. É o que desenha padrão para o WhatsApp.</param>
/// <param name="CotaGasta">Enviadas mais incertas. É o que conta contra o teto do dia.</param>
/// <param name="Contradicoes">
/// O app negou o número e a agenda do aparelho discordou. Em série, é sinal precoce de restrição.
/// </param>
public sealed record ResumoDoRelatorio(
    int Tentativas,
    int Enviadas,
    int EntregasConfirmadas,
    int Incertas,
    int SemConta,
    int OutrasFalhas,
    int CotaGasta,
    int Contradicoes,
    IReadOnlyList<ResumoPorCausa> PorCausa,
    IReadOnlyList<ResumoPorDia> PorDia);

/// <summary>Conta um conjunto de linhas de envio. Puro: não lê disco, não escreve arquivo.</summary>
/// <remarks>
/// 🔴 A SEPARAÇÃO É O PONTO. Ler o CSV é do console, escrever o .xlsx é do CLI, e CONTAR é aqui —
/// porque contar é a única das três que pode estar sutilmente errada sem quebrar nada. Um "enviadas"
/// que engole o incerto não estoura exceção, não some da tela e não aparece no build: só faz a
/// planilha afirmar entregas que ninguém confirmou, e faz isso todo dia.
/// </remarks>
public static class RelatorioDeEnvios
{
    /// <summary>Traduz a coluna `enviado` do CSV. null quando o valor não é reconhecido.</summary>
    /// <remarks>
    /// Devolve nulo em vez de chutar "não saiu": linha ilegível é linha ilegível, e transformá-la em
    /// fracasso encheria o relatório de falhas que nunca aconteceram.
    /// </remarks>
    public static ResultadoDoEnvio? Interpretar(string? enviado) => enviado switch
    {
        "sim" => ResultadoDoEnvio.Enviado,
        "incerto" => ResultadoDoEnvio.Incerto,
        "nao" => ResultadoDoEnvio.NaoSaiu,
        _ => null,
    };

    /// <summary>Lê o nome de uma <see cref="FalhaCausa"/> gravada no CSV.</summary>
    /// <remarks>
    /// Coluna vazia devolve <see cref="FalhaCausa.Nenhuma"/>, e é assim que os logs anteriores a esta
    /// coluna continuam abrindo. Nome desconhecido também: um enum que ganhou valor novo depois não
    /// pode impedir a leitura do que veio antes.
    /// </remarks>
    public static FalhaCausa LerCausa(string? texto) =>
        Enum.TryParse<FalhaCausa>(texto, ignoreCase: false, out var causa) && Enum.IsDefined(causa)
            ? causa
            : FalhaCausa.Nenhuma;

    /// <summary>Conta as linhas.</summary>
    public static ResumoDoRelatorio Resumir(IReadOnlyList<LinhaDeEnvio> linhas)
    {
        ArgumentNullException.ThrowIfNull(linhas);

        var enviadas = 0;
        var entregas = 0;
        var incertas = 0;
        var semConta = 0;
        var outrasFalhas = 0;
        var contradicoes = 0;
        var porCausa = new Dictionary<FalhaCausa, int>();
        var porDia = new Dictionary<string, (int Enviadas, int NaoSairam, int Entregas)>(StringComparer.Ordinal);

        foreach (var l in linhas)
        {
            if (l.Contradito)
            {
                contradicoes++;
            }

            // Numa variável, e não `l.Dia` duas vezes: a propriedade FORMATA a data a cada leitura, e
            // ler no TryGetValue e de novo no indexador dobrava a alocação de string por linha do log.
            var chave = l.Dia;
            porDia.TryGetValue(chave, out var dia);
            switch (l.Resultado)
            {
                case ResultadoDoEnvio.Enviado:
                    enviadas++;
                    dia.Enviadas++;
                    if (l.EntregaConfirmada)
                    {
                        entregas++;
                        dia.Entregas++;
                    }
                    break;

                case ResultadoDoEnvio.Incerto:
                    incertas++;
                    // 🔴 CONTA COMO SAÍDA NO DIA, e não como recusa. A pergunta que a linha do dia
                    // responde é quanto volume aquele chip fez, e o volume é a conversa ABERTA. Pôr o
                    // incerto do lado das recusas faria a curva de aquecimento enxergar um dia mais
                    // leve do que o que de fato aconteceu, que é o erro caro nessa direção.
                    dia.Enviadas++;
                    IncrementarCausa(porCausa, l.Causa);
                    break;

                case ResultadoDoEnvio.NaoSaiu:
                default:
                    if (l.Causa is FalhaCausa.NumeroSemConta)
                    {
                        semConta++;
                    }
                    else
                    {
                        outrasFalhas++;
                    }
                    dia.NaoSairam++;
                    IncrementarCausa(porCausa, l.Causa);
                    break;
            }
            porDia[chave] = dia;
        }

        // O denominador é tudo que NÃO saiu confirmado, incerto incluído, porque é o mesmo conjunto que
        // o fecho do lote chama de "falha(s)". Duas contagens de falha com totais diferentes na mesma
        // tela seria a planilha discordando do console.
        var totalFalhas = incertas + semConta + outrasFalhas;
        var causas = porCausa
            .Select(p => new ResumoPorCausa(
                p.Key, p.Value, totalFalhas == 0 ? 0 : (double)p.Value / totalFalhas))
            .OrderByDescending(c => c.Quantidade)
            .ThenBy(c => c.Causa)
            .ToList();

        // Ordem alfabética É ordem cronológica porque a data é ISO-8601, e ordenar texto evita um parse
        // que só existiria pra ser descartado logo em seguida.
        var dias = porDia
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ResumoPorDia(p.Key, p.Value.Enviadas, p.Value.NaoSairam, p.Value.Entregas))
            .ToList();

        return new ResumoDoRelatorio(
            Tentativas: linhas.Count,
            Enviadas: enviadas,
            EntregasConfirmadas: entregas,
            Incertas: incertas,
            SemConta: semConta,
            OutrasFalhas: outrasFalhas,
            CotaGasta: enviadas + incertas,
            Contradicoes: contradicoes,
            PorCausa: causas,
            PorDia: dias);
    }

    private static void IncrementarCausa(Dictionary<FalhaCausa, int> acc, FalhaCausa causa)
    {
        acc.TryGetValue(causa, out var n);
        acc[causa] = n + 1;
    }
}
