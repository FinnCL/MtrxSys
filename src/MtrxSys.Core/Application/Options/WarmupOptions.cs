namespace MtrxSys.Core.Application.Options;

public sealed class WarmupOptions
{
    public const string SectionName = "Warmup";

    // Vazio por padrão DE PROPÓSITO: o binder de configuração do .NET ANEXA itens a um
    // array já populado (não substitui), então um default não-vazio se concatenaria com
    // o do appsettings. Quando vazio, o WarmupManager usa uma curva-padrão segura.
    public int[] Curve { get; set; } = [];
    public DateOnly? StartedOnUtc { get; set; }
}
