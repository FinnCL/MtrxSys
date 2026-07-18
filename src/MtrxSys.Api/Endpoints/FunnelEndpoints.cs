using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
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

        // Gera o link wa.me do CHIP (click-to-chat) e registra o convite de funil por contato. O link
        // aponta pro NÚMERO CONECTADO: quem clica te ESCREVE (inbound = consentimento, sem 463). É um
        // link só, pra distribuir (anúncio, link, e-mail/SMS, ou postar num grupo) — o casamento de quem
        // respondeu é pelo telefone de quem escreve, não pelo link, então não há link por contato.
        group.MapPost("/links", async (
            FunnelGenerateRequest req,
            IContactRepository contacts,
            IFunnelInviteRepository invites,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatchOpts,
            IUnitOfWork uow,
            IClock clock,
            CancellationToken ct) =>
        {
            // Número do chip conectado — o alvo do link (a mesma fonte do /api/presence/chip). Sem chip
            // no ar não há pra quem a pessoa escrever: falha explícita em vez de link quebrado.
            var chip = await waha.GetSessionSnapshotAsync(dispatchOpts.Value.SessionId, ct);
            var chatLink = WaMeLink.Build(chip.Identity?.PhoneE164, req.PrefillText);
            if (chatLink is null)
            {
                return Results.BadRequest(new { error = "Conecte o chip antes de gerar o link do funil — sem número conectado não há pra quem a pessoa escrever." });
            }

            var now = clock.UtcNow;
            IReadOnlyList<Contact> targets;
            if (req.ContactIds is { Count: > 0 } ids)
            {
                // Um único SELECT pelos ids (sem N+1 de um GetById por contato).
                var byId = await contacts.GetByIdsAsync(ids.Distinct().ToList(), ct);
                targets = byId.Values.ToList();
            }
            else
            {
                // Público por grupo (mesma seleção do disparo, mas NÃO cria job/ledger — link não é envio).
                var filter = new ContactFilter(
                    GroupTag: string.IsNullOrWhiteSpace(req.GroupTag) ? null : req.GroupTag,
                    ExcludeOptedOut: true);
                targets = await contacts.ListByFilterAsync(filter, ct);
            }

            // Convites já abertos destes contatos — reusa em vez de duplicar (idempotente por contato):
            // re-gerar com texto novo atualiza o convite aberto, não cria um segundo. Um SELECT só.
            var openByContact = (await invites.ListOpenByContactIdsAsync(targets.Select(c => c.Id).ToList(), ct))
                .GroupBy(i => i.ContactId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First());

            foreach (var c in targets)
            {
                if (openByContact.TryGetValue(c.Id, out var existing))
                {
                    existing.UpdateContent(req.PrefillText, req.AutoReplyText);
                    await invites.UpdateAsync(existing, ct);
                }
                else
                {
                    var invite = FunnelInvite.Create(Guid.NewGuid(), c.Id, req.PrefillText, req.AutoReplyText, now);
                    await invites.AddAsync(invite, ct);
                }
            }
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { count = targets.Count, chatLink });
        });

        // Painel: convites recentes com status (pending / engaged / replied) + o link pra copiar.
        group.MapGet("/", async (
            IFunnelInviteRepository invites,
            IContactRepository contacts,
            CancellationToken ct) =>
        {
            var recent = await invites.ListRecentAsync(200, ct);
            // Um único SELECT pelos contatos dos convites (sem N+1 — este endpoint é polido de 5-8s).
            var byId = await contacts.GetByIdsAsync(recent.Select(i => i.ContactId).Distinct().ToList(), ct);
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
