namespace MtrxSys.Core.Application.Options;

public sealed class FunnelOptions
{
    public const string SectionName = "Funnel";

    /// <summary>Liga a auto-resposta no 1º inbound: quando um contato com convite de funil ABERTO te
    /// manda a 1ª mensagem, o sistema dispara automaticamente o AutoReplyText do convite. Default OFF —
    /// o operador liga quando quiser o comportamento "a pessoa chama → o sistema já responde".</summary>
    public bool AutoReplyEnabled { get; set; }
}
