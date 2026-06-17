namespace MtrxSys.Core.Application.Options;

public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    /// <summary>Canais de Telegram (sementes) lidos a cada coleta. A descoberta automática
    /// acrescenta outros canais referenciados, dentro do limite de MaxChannelsPerRun.</summary>
    public string[] TelegramChannels { get; set; } = [];

    /// <summary>Teto de canais visitados por coleta (sementes + descobertos) — limita o fan-out.</summary>
    public int MaxChannelsPerRun { get; set; } = 30;

    /// <summary>Quantos links novos são enriquecidos (join-info) por coleta. O excedente fica
    /// como Found e é resolvido numa coleta futura.</summary>
    public int ResolveMaxPerRun { get; set; } = 40;

    // Espaçamento aleatório entre as consultas join-info (cada uma toca o WhatsApp) — anti-bloqueio.
    public int ResolveDelayMinSeconds { get; set; } = 2;
    public int ResolveDelayMaxSeconds { get; set; } = 5;

    // Trava anti-ban da ENTRADA em grupo: intervalo mínimo aleatório entre joins + teto diário.
    public int JoinMinIntervalSeconds { get; set; } = 120;
    public int JoinMaxIntervalSeconds { get; set; } = 300;
    public int MaxJoinsPerDay { get; set; } = 15;

    /// <summary>Esconde da lista grupos que aparentam não ser brasileiros (nome em alfabeto
    /// estrangeiro — árabe, hindi, cirílico, CJK, etc.). É um filtro de NOME, parcial: não pega
    /// grupos estrangeiros de nome em inglês. A garantia real é no contato (ver DispatchOptions).</summary>
    public bool OnlyBrazilian { get; set; } = true;

    // Busca por nicho via SearXNG (buscador open-source auto-hospedado, web inteira, sem chave).
    // URL do serviço. Vazio = desligado: o Coletor cai no Telegram (modo "variados").
    public string? SearxngBaseUrl { get; set; }

    /// <summary>Quantos resultados buscar por nicho (várias páginas; teto 200). 30 ≈ 2-3 páginas.</summary>
    public int MaxResultsPerSearch { get; set; } = 30;

    /// <summary>Template da query de busca; {keyword} vira o nicho. Mira páginas de listagem BR
    /// (rende mais grupos relevantes que só o domínio do convite).</summary>
    public string SearchQueryTemplate { get; set; } = "grupos de whatsapp {keyword}";

    /// <summary>Diretórios (allowlist de domínios) a varrer. Vazio = aceita qualquer página dos
    /// resultados; preenchido = só varre o corpo de páginas desses domínios (foca em diretórios BR).</summary>
    public string[] DirectorySites { get; set; } = [];
}
