namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Fonte de links de grupo de WhatsApp captados da web aberta (ex.: canais de Telegram).
/// Abstrai a fonte: hoje Telegram (grátis), no futuro outra sem mexer no resto do Coletor.</summary>
public interface IGroupLinkSource
{
    /// <summary>Lê UM canal/fonte e devolve os links crus encontrados + outros canais referenciados
    /// (pra descoberta automática). Nunca lança por canal vazio — devolve coleções vazias.</summary>
    Task<ChannelHarvest> FetchChannelAsync(string channel, CancellationToken ct);
}

/// <summary>Link cru de convite, antes de qualquer enriquecimento.</summary>
public sealed record RawGroupLink(string InviteCode, string InviteUrl, string SourceChannel);

/// <summary>Resultado da leitura de um canal: links encontrados + canais mencionados.</summary>
public sealed record ChannelHarvest(IReadOnlyList<RawGroupLink> Links, IReadOnlyList<string> ReferencedChannels);
