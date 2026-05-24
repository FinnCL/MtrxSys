namespace MtrxSys.Core.Application.Options;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Liga/desliga a sincronização automática em background.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Intervalo entre ciclos de sync (segundos). Mínimo efetivo de 15s.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Quantas mensagens recentes puxar por chat em cada ciclo.</summary>
    public int MessagesPerChat { get; set; } = 50;
}
