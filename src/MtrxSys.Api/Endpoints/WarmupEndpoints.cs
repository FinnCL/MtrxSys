using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.Warmup;

namespace MtrxSys.Api.Endpoints;

/// <summary>API do motor de aquecimento de conversa: status (config + toggle + contagem por membro) e
/// o toggle Iniciar/Parar (persistido no system_state). Auth herdada da FallbackPolicy.</summary>
public static class WarmupEndpoints
{
    public static IEndpointRouteBuilder MapWarmupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/warmup");

        group.MapGet("/status", async (
            IOptions<WarmupEngineOptions> opts, WarmupState state, ISystemStateRepository stateRepo,
            IClock clock, CancellationToken ct) =>
        {
            var o = opts.Value;
            var st = await stateRepo.GetAsync(ct);
            var today = IClock.ToBrasiliaDate(clock.UtcNow);
            var sent = state.SnapshotSentToday(today);
            var members = o.Members
                .Select(m => new WarmupMemberStatus(m.Name, m.PhoneE164, sent.GetValueOrDefault(m.Name)))
                .ToList();
            return Results.Ok(new WarmupStatusDto(
                FeatureEnabled: o.Enabled,
                Running: st.WarmupEngineEnabled,
                MemberCount: o.Members.Count,
                GroupCount: o.GroupInviteLinks.Count,
                StartedOn: state.StartedOn?.ToString("yyyy-MM-dd"),
                Members: members));
        });

        group.MapPost("/start", async (
            IOptions<WarmupEngineOptions> opts, ISystemStateRepository stateRepo, IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var o = opts.Value;
            if (!o.Enabled)
            {
                return Results.BadRequest(new { error = "Aquecimento desabilitado por config (WarmupEngine:Enabled=false)." });
            }
            if (o.Members.Count < 2)
            {
                return Results.BadRequest(new { error = "O pool precisa de pelo menos 2 membros pra mão dupla." });
            }
            var st = await stateRepo.GetAsync(ct);
            st.SetWarmupEngineEnabled(true);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { running = true });
        });

        group.MapPost("/stop", async (ISystemStateRepository stateRepo, IUnitOfWork uow, CancellationToken ct) =>
        {
            var st = await stateRepo.GetAsync(ct);
            st.SetWarmupEngineEnabled(false);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { running = false });
        });

        return app;
    }

    private sealed record WarmupStatusDto(
        bool FeatureEnabled, bool Running, int MemberCount, int GroupCount, string? StartedOn,
        IReadOnlyList<WarmupMemberStatus> Members);

    private sealed record WarmupMemberStatus(string Name, string Phone, int SentToday);
}
