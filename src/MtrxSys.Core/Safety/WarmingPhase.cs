namespace MtrxSys.Core.Safety;

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
}
