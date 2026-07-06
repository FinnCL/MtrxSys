namespace MtrxSys.Core.Application.Warmup;

/// <summary>Rampa de teto de conversas do aquecimento. Função pura (testável): dia 0 = start; sobe
/// linearmente até max ao longo de rampDays; depois platô no max. Extraída do WarmupEngine pra poder
/// ser coberta por teste sem subir o motor inteiro.</summary>
public static class WarmupRamp
{
    public static int LimitFor(int start, int max, int rampDays, int dayIndex)
    {
        if (dayIndex < 0)
        {
            dayIndex = 0;
        }
        if (rampDays <= 0 || dayIndex >= rampDays)
        {
            return max;
        }
        var span = max - start;
        return start + (int)Math.Round((double)span * dayIndex / rampDays);
    }
}
