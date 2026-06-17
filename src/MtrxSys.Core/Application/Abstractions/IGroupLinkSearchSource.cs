namespace MtrxSys.Core.Application.Abstractions;

/// <summary>
/// Fonte de grupos por BUSCA de palavra-chave na web aberta — faz o nicho realmente direcionar a
/// descoberta. Diferente do <see cref="IGroupLinkSource"/> (que varre canais de Telegram), esta
/// recebe o termo e devolve os links crus relevantes. Hoje: SearXNG (buscador auto-hospedado).
/// </summary>
public interface IGroupLinkSearchSource
{
    /// <summary>Nome do motor de busca (ex.: "Serper", "SearXNG") — pra exibir no painel, SEM expor
    /// URL/credencial. Apenas informativo.</summary>
    string Engine { get; }

    /// <summary>Está pronta pra uso (credenciais presentes)? Quando false, o Coletor cai no Telegram
    /// em vez de gastar uma chamada/erro sem credencial.</summary>
    bool IsConfigured { get; }

    /// <summary>Busca até <paramref name="maxResults"/> links de convite relevantes ao termo. NUNCA
    /// lança por erro de rede/quota — devolve o que conseguiu (ou vazio).</summary>
    Task<IReadOnlyList<RawGroupLink>> SearchAsync(string keyword, int maxResults, CancellationToken ct);
}
