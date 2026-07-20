namespace MtrxSys.Core.Safety;

/// <summary>Estágio do aquecimento de um chip novo: ResponderOnly (primeiros dias, só quem respondeu),
/// Hybrid (círculo escolhido + frios novos intercalados, até o platô) e Mature (chip pronto, disparo
/// livre). Fora do aquecimento (trava off, sem marco, ou já no platô) = Mature.</summary>
public enum WarmingStage { ResponderOnly, Hybrid, Mature }

/// <summary>Definição ÚNICA da FASE DE AQUECIMENTO POR RESPONDEDORES — pra a trava do disparo
/// (CampaignsEndpoints) e o reset diário (WarmingDailyResetService) não divergirem. Puro/sem I/O:
/// recebe os dados já lidos (marco do chip + dias ativos + config) e diz se o chip ainda está na fase.
///
/// A fase vale nos primeiros N dias ATIVOS (dias com envio, não de calendário — chip parado não
/// amadurece) a partir do marco do chip. Nela, o disparo só aceita "Respondeu" (quem já escreveu neste
/// chip = seguro, sem 463); depois abre pra todas as audiências.</summary>
public static class WarmingPhase
{
    /// <summary>Ainda aquecendo? Exige a trava ligada (warmingDays &gt; 0), o marco do chip presente
    /// (WarmupStartedOn) e não ter cumprido os N dias ativos (activeDays &lt; warmingDays).
    /// activeDays = dias com envio ANTES de hoje (ver CountActiveDaysBeforeAsync).</summary>
    public static bool IsActive(DateOnly? warmupStartedOn, int activeDays, int warmingDays) =>
        warmingDays > 0 && warmupStartedOn is not null && activeDays < warmingDays;

    /// <summary>Classifica o estágio pelos dias ATIVOS. ResponderOnly enquanto activeDays &lt;
    /// responderDays; Hybrid daí até o platô (activeDays &lt; plateauDayIndex) se ligado; senão Mature.
    /// Trava off (responderDays &lt;= 0) ou sem marco → Mature (disparo normal, comportamento anterior).</summary>
    public static WarmingStage Classify(
        DateOnly? warmupStartedOn, int activeDays, int responderDays, int plateauDayIndex, bool hybridEnabled)
    {
        if (warmupStartedOn is null || responderDays <= 0) return WarmingStage.Mature;
        if (activeDays < responderDays) return WarmingStage.ResponderOnly;
        if (hybridEnabled && activeDays < plateauDayIndex) return WarmingStage.Hybrid;
        return WarmingStage.Mature;
    }
}
