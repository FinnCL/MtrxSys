using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Funnel;

namespace MtrxSys.Api.Endpoints;

/// <summary>Funil de inbound: gera os links wa.me (click-to-chat) pra um lote de contatos e mostra
/// quem já engajou (mandou o 1º inbound). NÃO envia nada a frio — o link só faz a PESSOA te procurar
/// (consentimento); o envio livre acontece depois, sem 463.</summary>
public static class FunnelEndpoints
{
    public static IEndpointRouteBuilder MapFunnelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/funnel");

        // Gera os links wa.me pra um lote (por grupo OU lista de ids) + cria os convites de funil.
        group.MapPost("/links", async (
            FunnelGenerateRequest req,
            IContactRepository contacts,
            IFunnelInviteRepository invites,
            IUnitOfWork uow,
            IClock clock,
            CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            IReadOnlyList<Contact> targets;
            if (req.ContactIds is { Count: > 0 } ids)
            {
                var loaded = new List<Contact>();
                foreach (var id in ids.Distinct())
                {
                    var c = await contacts.GetByIdAsync(id, ct);
                    if (c is not null)
                    {
                        loaded.Add(c);
                    }
                }
                targets = loaded;
            }
            else
            {
                // Público por grupo (mesma seleção do disparo, mas NÃO cria job/ledger — link não é envio).
                var filter = new ContactFilter(
                    GroupTag: string.IsNullOrWhiteSpace(req.GroupTag) ? null : req.GroupTag,
                    ExcludeOptedOut: true);
                targets = await contacts.ListByFilterAsync(filter, ct);
            }

            var links = new List<object>();
            foreach (var c in targets)
            {
                var link = WaMeLink.Build(c.Phone.E164, req.PrefillText);
                if (link is null)
                {
                    continue;
                }
                var invite = FunnelInvite.Create(Guid.NewGuid(), c.Id, req.PrefillText, req.AutoReplyText, now);
                await invites.AddAsync(invite, ct);
                links.Add(new { contactId = c.Id, name = c.Name, phone = c.Phone.E164, link });
            }
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { count = links.Count, links });
        });

        // Painel: convites recentes com status (pending / engaged / replied) + o link pra copiar.
        group.MapGet("/", async (
            IFunnelInviteRepository invites,
            IContactRepository contacts,
            CancellationToken ct) =>
        {
            var recent = await invites.ListRecentAsync(200, ct);
            var byId = new Dictionary<Guid, Contact>();
            foreach (var id in recent.Select(i => i.ContactId).Distinct())
            {
                var c = await contacts.GetByIdAsync(id, ct);
                if (c is not null)
                {
                    byId[id] = c;
                }
            }
            return Results.Ok(recent.Select(i =>
            {
                byId.TryGetValue(i.ContactId, out var c);
                var status = i.AutoRepliedAt is not null ? "replied"
                    : i.EngagedAt is not null ? "engaged"
                    : "pending";
                return new
                {
                    contactId = i.ContactId,
                    name = c?.Name,
                    phone = c?.Phone.E164,
                    prefillText = i.PrefillText,
                    autoReplyText = i.AutoReplyText,
                    createdAt = i.CreatedAt,
                    engagedAt = i.EngagedAt,
                    autoRepliedAt = i.AutoRepliedAt,
                    status,
                    link = WaMeLink.Build(c?.Phone.E164, i.PrefillText),
                };
            }));
        });

        return app;
    }

    public sealed record FunnelGenerateRequest(
        string? GroupTag,
        IReadOnlyList<Guid>? ContactIds,
        string? PrefillText,
        string? AutoReplyText);
}
