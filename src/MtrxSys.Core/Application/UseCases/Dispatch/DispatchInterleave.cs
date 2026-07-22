namespace MtrxSys.Core.Application.UseCases.Dispatch;

/// <summary>
/// Intercala a lista de PRIORIDADE (o "seed" do aquecimento — o Círculo/quem RESPONDEU) com a de FRIOS,
/// abrindo com a prioridade e espalhando o resto uniformemente (Bresenham): no slot i coloca prioridade
/// se ainda há E (os frios acabaram OU já "deveríamos" ter colocado mais um do seed: i·P ≥ pi·total).
/// Ex.: P=3, N=9 → seed nos slots 0, 4, 8. P=0 → só frio; N=0 → só seed. É o padrão mais orgânico que
/// "todos do seed e depois todos frios", e garante que o 1º da fila é um do seed.
/// <para>Fonte ÚNICA da intercalação — usada no reset diário da fase híbrida (HybridCycleEnqueuer) e no
/// Disparar manual (CampaignsEndpoints), pra a ordem "seed primeiro" não divergir entre os dois.</para>
/// </summary>
public static class DispatchInterleave
{
    public static List<T> Interleave<T>(IReadOnlyList<T> priority, IReadOnlyList<T> filler)
    {
        int p = priority.Count, n = filler.Count, total = p + n;
        var seq = new List<T>(total);
        int pi = 0, fi = 0;
        for (var i = 0; i < total; i++)
        {
            var placePriority = pi < p && (fi >= n || (long)i * p >= (long)pi * total);
            seq.Add(placePriority ? priority[pi++] : filler[fi++]);
        }
        return seq;
    }
}
