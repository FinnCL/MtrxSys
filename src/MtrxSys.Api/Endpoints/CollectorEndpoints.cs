using System.Net;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Groups;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Safety;

namespace MtrxSys.Api.Endpoints;

public static class CollectorEndpoints
{
    public static IEndpointRouteBuilder MapCollectorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/collector");

        // Info do motor de busca pro painel (SEM expor URL/credencial): qual motor está ativo e
        // quantas requisições já foram feitas (informativo — reinício zera). Sem busca configurada,
        // o Coletor usa o Telegram (modo variado).
        group.MapGet("/search-info", async (
            IGroupLinkSearchSource search, ISearchUsageMeter meter, ISearchStatus status, CancellationToken ct) =>
            Results.Ok(new
            {
                engine = search.IsConfigured ? search.Engine : "Telegram",
                configured = search.IsConfigured,
                requestCount = await meter.GetCountAsync(ct),
                lastError = status.LastError,
            }));

        // Estado da trava anti-ban de ENTRADA (pro painel deixar o limite explícito): entradas hoje,
        // teto diário, quantas restam e segundos até a próxima. Lê do BANCO (joined_at) — persistente,
        // sobrevive a restart; o teto zera à meia-noite de Brasília.
        group.MapGet("/join-status", async (JoinThrottle throttle, IGroupLinkRepository repo, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var joinsToday = await repo.CountJoinedSinceAsync(JoinThrottle.StartOfTodayBrazil(now), ct);
            var lastJoinedAt = await repo.MaxJoinedAtAsync(ct);
            var s = throttle.GetStatus(joinsToday, lastJoinedAt, now);
            return Results.Ok(new { joinsToday = s.JoinsToday, maxPerDay = s.MaxPerDay, remaining = s.Remaining, waitSeconds = s.WaitSeconds });
        });

        // Dispara uma coleta em background. keyword vazio = modo variado.
        group.MapPost("/collect", (CollectRequestDto? body, IGroupCollectorChannel queue, IGroupLinkSearchSource search) =>
        {
            var keyword = string.IsNullOrWhiteSpace(body?.Keyword) ? null : body!.Keyword!.Trim();
            var queued = queue.TryEnqueue(new CollectRequest(keyword));
            // searchConfigured avisa o front quando uma busca por nicho cai no Telegram (SearXNG desligado).
            return Results.Accepted("/api/collector/links", new { queued, keyword, searchConfigured = search.IsConfigured });
        });

        // Lista paginada dos links captados (pra tabela). keyword filtra por nicho; status opcional.
        group.MapGet("/links", async (
            string? keyword,
            string? status,
            int? limit,
            int? offset,
            IGroupLinkRepository repo,
            CancellationToken ct) =>
        {
            GroupLinkStatus? parsed = Enum.TryParse<GroupLinkStatus>(status, ignoreCase: true, out var s) ? s : null;
            var kw = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
            var pageSize = Math.Clamp(limit ?? 25, 1, 200);
            var skip = Math.Max(0, offset ?? 0);
            var total = await repo.CountAsync(kw, parsed, ct);
            var list = await repo.ListAsync(kw, parsed, pageSize, skip, ct);
            return Results.Ok(new
            {
                items = list.Select(GroupLinkDto.From),
                total,
                limit = pageSize,
                offset = skip,
            });
        });

        // Entrar no grupo (clique manual), com trava anti-ban (intervalo + teto diário).
        group.MapPost("/links/{code}/join", async (
            string code,
            IGroupLinkRepository repo,
            IWahaClient waha,
            JoinThrottle throttle,
            IClock clock,
            IUnitOfWork uow,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var link = await repo.GetByCodeAsync(code, ct);
            if (link is null)
            {
                return Results.NotFound();
            }

            // Trava anti-ban a partir do estado PERSISTIDO (entradas do dia + última entrada do banco):
            // sobrevive a restart e zera à meia-noite de Brasília.
            var now = clock.UtcNow;
            var joinsToday = await repo.CountJoinedSinceAsync(JoinThrottle.StartOfTodayBrazil(now), ct);
            var lastJoinedAt = await repo.MaxJoinedAtAsync(ct);
            var decision = throttle.Check(joinsToday, lastJoinedAt, now);
            if (!decision.Allowed)
            {
                return Results.Problem(detail: decision.Reason, statusCode: StatusCodes.Status429TooManyRequests);
            }

            WahaGroup joined;
            try
            {
                joined = await waha.JoinGroupByInviteAsync(dispatch.Value.SessionId, link.InviteCode, ct);
            }
            catch (HttpRequestException ex)
            {
                // 4xx do WAHA = convite inválido/expirado/cheio → marca o link e avisa com clareza
                // (não é erro de servidor). NÃO conta no anti-ban: só uma entrada de fato (joined_at).
                if (ex.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
                {
                    link.MarkInvalid(clock.UtcNow);
                    await repo.UpdateAsync(link, ct);
                    await uow.SaveChangesAsync(ct);
                    return Results.Problem(
                        detail: "Não foi possível entrar: o convite pode estar expirado, cheio ou inválido.",
                        statusCode: StatusCodes.Status422UnprocessableEntity);
                }
                return Results.Problem(detail: $"Falha ao entrar: {ex.Message}", statusCode: 502);
            }

            // Carimba joined_at → ESTA é a contabilização anti-ban (persistente). Sem contador em memória.
            link.MarkJoined(joined.Id, now);
            await repo.UpdateAsync(link, ct);
            await uow.SaveChangesAsync(ct);

            // Sem JID do grupo (o join não devolveu e não deu pra resolver pelo nome) o import não
            // tem alvo — avisamos o front pra NÃO oferecer "Importar" (que traria 0 em silêncio).
            var canImport = !string.IsNullOrWhiteSpace(joined.Id);
            return Results.Ok(new { joined = true, groupId = joined.Id, name = joined.Name, canImport });
        });

        // Entrada manual de links: cola 1 ou vários (chat.whatsapp.com/...), valida na hora —
        // só os VIVOS viram "Pronto"; mortos/estrangeiros não aparecem. Reusa a validação da coleta.
        group.MapPost("/links/manual", async (
            ManualLinksDto? body,
            CollectGroupLinksUseCase useCase,
            CancellationToken ct) =>
        {
            if (body?.Links is null || body.Links.Length == 0)
            {
                return Results.Problem("Informe ao menos um link.", statusCode: 400);
            }
            var r = await useCase.AddManualAsync(body.Links, body.Keyword, ct);
            return Results.Ok(new
            {
                added = r.Added,
                live = r.Live,
                dead = r.Dead,
                duplicates = r.Duplicates,
                pendingValidation = r.PendingValidation,
            });
        });

        return app;
    }

    public sealed record CollectRequestDto(string? Keyword);

    public sealed record ManualLinksDto(string[]? Links, string? Keyword);

    public sealed record GroupLinkDto(
        string InviteCode,
        string InviteUrl,
        string? SourceChannel,
        string? GroupName,
        int? ParticipantCount,
        string Status,
        string? MatchedKeyword,
        string? WhatsAppGroupId,
        DateTimeOffset CollectedAt,
        DateTimeOffset? ResolvedAt)
    {
        public static GroupLinkDto From(GroupLink l) => new(
            l.InviteCode, l.InviteUrl, l.SourceChannel, l.GroupName, l.ParticipantCount,
            l.Status.ToString(), l.MatchedKeyword, l.WhatsAppGroupId, l.CollectedAt, l.ResolvedAt);
    }
}
