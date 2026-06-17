using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Core.Application.UseCases.Groups;

/// <summary>
/// Uma rodada de coleta: descobre links (busca por nicho ou canais de Telegram), deduplica contra
/// o banco, e VALIDA os achados pela PÁGINA PÚBLICA do convite (sem WAHA) — vivo→Resolved,
/// morto→Invalid, estrangeiro→Foreign — com pacing e teto por rodada.
/// </summary>
public sealed class CollectGroupLinksUseCase(
    IGroupLinkSource source,
    IGroupLinkSearchSource search,
    IWhatsAppInviteValidator validator,
    IGroupLinkRepository links,
    IUnitOfWork uow,
    IClock clock,
    IRandomSource rng,
    IOptions<CollectorOptions> collectorOpts)
{
    public async Task<CollectResult> ExecuteAsync(string? keyword, CancellationToken ct)
    {
        var o = collectorOpts.Value;
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();

        // 1) Coleta dos links crus, conforme a fonte:
        //    - nicho informado E busca (Google) configurada → busca direcionada ao nicho;
        //    - senão → varre os canais de Telegram (sementes + descobertos), modo "variados".
        var rawByCode = new Dictionary<string, RawGroupLink>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var channelsFailed = 0;

        if (keyword is not null && search.IsConfigured)
        {
            foreach (var raw in await search.SearchAsync(keyword, o.MaxResultsPerSearch, ct))
            {
                rawByCode.TryAdd(raw.InviteCode, raw);
            }
        }
        else
        {
            // BFS bounded por canais (sementes + descobertos), juntando links únicos por código.
            var pending = new Queue<string>(o.TelegramChannels);
            var maxChannels = Math.Max(1, o.MaxChannelsPerRun);
            while (pending.Count > 0 && visited.Count < maxChannels)
            {
                ct.ThrowIfCancellationRequested();
                var channel = pending.Dequeue();
                if (!visited.Add(channel))
                {
                    continue;
                }
                ChannelHarvest harvest;
                try
                {
                    harvest = await source.FetchChannelAsync(channel, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch
                {
                    channelsFailed++;
                    continue;
                }
#pragma warning restore CA1031
                foreach (var link in harvest.Links)
                {
                    rawByCode.TryAdd(link.InviteCode, link);
                }
                foreach (var referenced in harvest.ReferencedChannels)
                {
                    if (!visited.Contains(referenced))
                    {
                        pending.Enqueue(referenced);
                    }
                }
            }
        }

        // 2) Dedup contra o banco (um SELECT só) e insere os novos como Found.
        var existing = await links.GetByCodesAsync([.. rawByCode.Keys], ct);
        var newLinks = new List<GroupLink>();
        foreach (var (code, raw) in rawByCode)
        {
            if (existing.ContainsKey(code))
            {
                continue;
            }
            var link = GroupLink.Create(Guid.NewGuid(), code, raw.InviteUrl, raw.SourceChannel, keyword, clock.UtcNow);
            await links.AddAsync(link, ct);
            newLinks.Add(link);
        }
        await uow.SaveChangesAsync(ct);

        // 3) Validação pela página pública (sem WAHA), capada por rodada. PRIORIZA os recém-achados
        // desta busca (newLinks) — pra resolverem/aparecerem JÁ; no modo variado, completa o teto
        // com o backlog antigo de Found.
        var resolved = 0;
        var invalid = 0;
        var foreign = 0;

        var cap = Math.Max(1, o.ResolveMaxPerRun);
        var toResolve = new List<GroupLink>();
        var picked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nl in newLinks)
        {
            if (toResolve.Count >= cap) break;
            if (picked.Add(nl.InviteCode)) toResolve.Add(nl);
        }
        // Completa o teto com o backlog de Found: no modo variado, qualquer um (mais antigos);
        // numa busca por nicho, os Found DAQUELE nicho — pra re-validar os achados presos dele.
        if (toResolve.Count < cap)
        {
            var backlog = keyword is null
                ? await links.ListForEnrichmentAsync(cap, ct)
                : await links.ListForEnrichmentByKeywordAsync(keyword, cap, ct);
            foreach (var b in backlog)
            {
                if (toResolve.Count >= cap) break;
                if (picked.Add(b.InviteCode)) toResolve.Add(b);
            }
        }

        foreach (var link in toResolve)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(NextResolveDelay(o), ct); // pacing pra não tomar bloqueio do WhatsApp.
            switch (await EnrichAsync(link, o.OnlyBrazilian, ct))
            {
                case EnrichOutcome.Resolved: resolved++; break;
                case EnrichOutcome.Invalid: invalid++; break;
                case EnrichOutcome.Foreign: foreign++; break;
                default: break; // Transient: fica Found pra re-tentar depois.
            }
            // Persiste POR LINK: os recém-achados aparecem na tela em segundos (polling de 4s).
            await uow.SaveChangesAsync(ct);
        }
        var remainingPending = await links.CountAsync(null, GroupLinkStatus.Found, ct);

        return new CollectResult(
            TotalSeen: rawByCode.Count,
            NewLinks: newLinks.Count,
            Resolved: resolved,
            Invalid: invalid,
            Foreign: foreign,
            RemainingPending: remainingPending,
            ChannelsVisited: visited.Count,
            ChannelsFailed: channelsFailed);
    }

    private TimeSpan NextResolveDelay(CollectorOptions o)
    {
        var min = Math.Max(0, o.ResolveDelayMinSeconds);
        var max = Math.Max(min + 1, o.ResolveDelayMaxSeconds + 1);
        return TimeSpan.FromSeconds(rng.NextInt(min, max));
    }

    private enum EnrichOutcome { Resolved, Invalid, Foreign, Transient }

    // Valida UM link pela página pública e atualiza o status: vivo→Resolved, estrangeiro→Foreign,
    // morto→Invalid. Erro transitório (bloqueio/rede) → fica Found (re-tenta depois). O participante
    // não vem da página pública (só o nome) — fica null e é obtido depois, ao entrar.
    private async Task<EnrichOutcome> EnrichAsync(GroupLink link, bool onlyBrazilian, CancellationToken ct)
    {
        var check = await validator.CheckAsync(link.InviteCode, ct);
        if (check is null)
        {
            return EnrichOutcome.Transient; // bloqueio/erro transitório → continua Found.
        }
        if (!check.Alive)
        {
            link.MarkInvalid(clock.UtcNow);
            await links.UpdateAsync(link, ct);
            return EnrichOutcome.Invalid;
        }
        if (onlyBrazilian && LooksForeign(check.Name))
        {
            link.MarkForeign(check.Name, null, clock.UtcNow);
            await links.UpdateAsync(link, ct);
            return EnrichOutcome.Foreign;
        }
        link.Resolve(check.Name, null, clock.UtcNow);
        await links.UpdateAsync(link, ct);
        return EnrichOutcome.Resolved;
    }

    /// <summary>
    /// Entrada manual: recebe linhas com links/URLs de convite, deduplica contra o banco, cria os
    /// novos como Found e — se a sessão estiver conectada — valida na hora. Reusa a validação da coleta.
    /// </summary>
    public async Task<ManualLinksResult> AddManualAsync(IReadOnlyList<string> rawLinks, string? keyword, CancellationToken ct)
    {
        var o = collectorOpts.Value;
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();

        // Extrai os códigos das linhas coladas (aceita link completo, URL ou texto com o link).
        var codes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawLinks ?? [])
        {
            foreach (var code in WhatsAppInviteParser.ExtractCodes(raw))
            {
                if (seen.Add(code))
                {
                    codes.Add(code);
                }
            }
        }
        if (codes.Count == 0)
        {
            return new ManualLinksResult(0, 0, 0, 0, 0);
        }

        // Teto de segurança: valida no máximo 50 por chamada (a validação é síncrona — evita request
        // longa demais e martelar o WAHA). O excedente o usuário cola numa próxima leva.
        const int maxManual = 50;
        if (codes.Count > maxManual)
        {
            codes = codes.GetRange(0, maxManual);
        }

        // Dedup contra o banco; insere os novos como Found.
        var existing = await links.GetByCodesAsync(codes, ct);
        var added = new List<GroupLink>();
        var duplicates = 0;
        foreach (var code in codes)
        {
            if (existing.ContainsKey(code))
            {
                duplicates++;
                continue;
            }
            var link = GroupLink.Create(
                Guid.NewGuid(), code, $"https://chat.whatsapp.com/{code}", "manual", keyword, clock.UtcNow);
            await links.AddAsync(link, ct);
            added.Add(link);
        }
        await uow.SaveChangesAsync(ct);

        // Valida na hora pela página pública (não precisa do WhatsApp conectado).
        var live = 0;
        var dead = 0;
        var pending = 0;
        foreach (var link in added)
        {
            ct.ThrowIfCancellationRequested();
            switch (await EnrichAsync(link, o.OnlyBrazilian, ct))
            {
                case EnrichOutcome.Resolved: live++; break;
                case EnrichOutcome.Invalid:
                case EnrichOutcome.Foreign: dead++; break;
                default: pending++; break; // transitório (bloqueio) → fica Found pra re-tentar.
            }
        }
        await uow.SaveChangesAsync(ct);

        return new ManualLinksResult(added.Count, live, dead, duplicates, pending);
    }

    // Heurística barata de "não-brasileiro": o nome contém caracteres de alfabetos estrangeiros
    // (árabe, hebraico, grego, cirílico, indianos, tailandês, CJK, coreano). Acentos do português
    // (á, ç, ã…) são latinos e NÃO disparam. É parcial: nome estrangeiro em alfabeto latino
    // (ex.: inglês) passa — a garantia de verdade é filtrar o contato por +55 no import.
    private static readonly Regex ForeignScriptRx = new(
        "[Ͱ-ϿЀ-ӿ֐-׿؀-ۿݐ-ݿ"
        + "ऀ-ॿঀ-৿஀-௿ఀ-౿฀-๿"
        + "ᄀ-ᇿ぀-ヿ㐀-鿿가-힯]",
        RegexOptions.Compiled);

    private static bool LooksForeign(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ForeignScriptRx.IsMatch(name);
}

public sealed record CollectResult(
    int TotalSeen,
    int NewLinks,
    int Resolved,
    int Invalid,
    int Foreign,
    int RemainingPending,
    int ChannelsVisited,
    int ChannelsFailed);

/// <summary>Resultado da entrada manual de links: quantos novos, vivos, mortos, duplicados e
/// pendentes de validação (quando a sessão estava offline).</summary>
public sealed record ManualLinksResult(int Added, int Live, int Dead, int Duplicates, int PendingValidation);
