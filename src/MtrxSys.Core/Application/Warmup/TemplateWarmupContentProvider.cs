using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Core.Application.Warmup;

/// <summary>Banco de frases casuais VARIADAS pro aquecimento (MVP, grátis, sem dependência). Sorteia
/// abertura + resposta + (às vezes) um seguimento, de bancos distintos, com pequenas variações — pra
/// não repetir a mesma conversa. Não é IA, mas cobre o essencial de "papo casual entre conhecidos".</summary>
public sealed class TemplateWarmupContentProvider(IRandomSource rng) : IWarmupContentProvider
{
    private static readonly string[] Openers =
    [
        "oi, tudo bem?", "opa, e aí?", "eae, como vc tá?", "oi! tudo certo?", "fala, tudo tranquilo?",
        "oi sumido kkk", "e aí, novidades?", "bom dia! tudo bem?", "oi, como foi o dia?",
        "opa, tudo bom por aí?", "oi, td certo contigo?", "eae, de boa?", "oi, como vc anda?",
    ];

    private static readonly string[] Replies =
    [
        "tudo e vc?", "tudo ótimo, e você?", "de boa, e aí?", "tudo tranquilo, e contigo?",
        "tudo sim, graças a deus kkk", "tudo certo por aqui, e vc?", "opa tudo, e você como tá?",
        "td bem sim! e aí?", "tudo indo, e vc?", "de boa demais, e você?",
    ];

    private static readonly string[] FollowUps =
    [
        "que bom!", "aah que bom saber", "boaa", "kkkk pois é", "então tá ótimo", "showww",
        "legal demais", "que ótimo então", "poxa que bom", "massa", "aí sim!", "bora marcar algo qualquer dia",
        "depois te chamo aqui", "vlw por perguntar", "tmj sempre",
    ];

    public IReadOnlyList<WarmupTurn> BuildConversation()
    {
        var turns = new List<WarmupTurn>
        {
            new(0, Pick(Openers)),
            new(1, Pick(Replies)),
        };
        // ~60% das conversas têm um 3º turno; ~25% dessas ainda um 4º — pra o tamanho variar.
        if (rng.NextDouble() < 0.60)
        {
            turns.Add(new WarmupTurn(0, Pick(FollowUps)));
            if (rng.NextDouble() < 0.40)
            {
                turns.Add(new WarmupTurn(1, Pick(FollowUps)));
            }
        }
        return turns;
    }

    private string Pick(string[] bank) => bank[rng.NextInt(0, bank.Length)];
}
