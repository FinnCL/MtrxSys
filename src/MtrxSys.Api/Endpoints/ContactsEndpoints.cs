using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Api.Endpoints;

public static class ContactsEndpoints
{
    public static IEndpointRouteBuilder MapContactsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts");

        group.MapGet("/", async (
            string? stage,
            string? groupTag,
            IContactRepository contacts,
            ISharedPhoneLedger ledger,
            CancellationToken ct) =>
        {
            ContactStage? parsedStage = null;
            if (!string.IsNullOrWhiteSpace(stage))
            {
                if (!Enum.TryParse<ContactStage>(stage, ignoreCase: true, out var s))
                {
                    return Results.Problem($"unknown stage '{stage}'", statusCode: 400);
                }
                parsedStage = s;
            }
            // ExcludeOptedOut: false — na listagem o usuário quer ver tudo, inclusive "Descartado".
            var filter = new ContactFilter(
                Stage: parsedStage,
                TagName: null,
                GroupTag: string.IsNullOrWhiteSpace(groupTag) ? null : groupTag,
                ExcludeOptedOut: false);
            var list = await contacts.ListByFilterAsync(filter, ct);
            // Marca quem consta no registro compartilhado (tratado por outro chip). GetSuppressedAsync
            // já retorna vazio quando o recurso está desligado — então isto é no-op nesse caso.
            var suppressed = await ledger.GetSuppressedAsync(list.Select(c => c.Phone.E164).ToArray(), ct);
            return Results.Ok(list.Select(c => ToDto(c, suppressed.Contains(c.Phone.E164))));
        });

        group.MapGet("/group-tags", async (
            IContactRepository contacts,
            CancellationToken ct) =>
        {
            var tags = await contacts.ListGroupTagsAsync(ct);
            return Results.Ok(tags.Select(t => new { groupTag = t.GroupTag, count = t.Count }));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            IContactRepository contacts,
            IContactNoteRepository notes,
            IContactTagRepository tags,
            IContactStageChangeRepository changes,
            CancellationToken ct) =>
        {
            var contact = await contacts.GetByIdAsync(id, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }
            var noteList = await notes.ListByContactAsync(id, ct);
            var tagNames = await tags.ListTagsForContactAsync(id, ct);
            var history = await changes.ListByContactAsync(id, ct);
            return Results.Ok(new
            {
                contact = ToDto(contact),
                notes = noteList.Select(ToDto),
                tags = tagNames,
                stageHistory = history.Select(ToDto),
            });
        });

        group.MapPatch("/{id:guid}", async (
            Guid id,
            PatchContactRequest req,
            IContactRepository contacts,
            IContactTagRepository tagsRepo,
            IContactStageChangeRepository changes,
            ICurrentUserAccessor user,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var contact = await contacts.GetByIdAsync(id, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }

            var userId = user.UserId ?? Guid.Empty;
            var now = clock.UtcNow;

            if (req.Stage is not null)
            {
                if (!Enum.TryParse<ContactStage>(req.Stage, ignoreCase: true, out var parsedStage))
                {
                    return Results.Problem($"unknown stage '{req.Stage}'", statusCode: 400);
                }
                var previous = contact.ChangeStage(parsedStage, now);
                if (previous is not null)
                {
                    await contacts.UpdateAsync(contact, ct);
                    var change = ContactStageChange.Create(
                        id: Guid.NewGuid(),
                        contactId: contact.Id,
                        fromStage: previous,
                        toStage: parsedStage,
                        changedAt: now,
                        changedByUserId: userId);
                    await changes.AddAsync(change, ct);
                }
            }

            if (req.AddTags is { Count: > 0 })
            {
                // Carrega as tags atuais do contato UMA vez (antes era uma consulta por tag — N+1).
                // O set também faz o de-dupe dentro do próprio request (as atribuições novas ainda
                // não estão no banco, então re-consultar não as enxergaria).
                var assigned = new HashSet<string>(
                    await tagsRepo.ListTagsForContactAsync(id, ct), StringComparer.OrdinalIgnoreCase);
                foreach (var name in req.AddTags)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    var key = name.Trim().ToLowerInvariant();
                    var tag = await tagsRepo.GetByNameAsync(key, ct);
                    if (tag is null)
                    {
                        tag = ContactTag.Create(key, null, now);
                        await tagsRepo.AddAsync(tag, ct);
                    }
                    if (assigned.Add(key))
                    {
                        await tagsRepo.AssignAsync(ContactTagAssignment.Create(id, key, now), ct);
                    }
                }
            }

            if (req.RemoveTags is { Count: > 0 })
            {
                foreach (var name in req.RemoveTags)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    await tagsRepo.UnassignAsync(id, name, ct);
                }
            }

            await uow.SaveChangesAsync(ct);
            return Results.Ok(ToDto(contact));
        });

        group.MapPost("/{id:guid}/reactivate", async (
            Guid id,
            IContactRepository contacts,
            IContactStageChangeRepository changes,
            ICurrentUserAccessor user,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var contact = await contacts.GetByIdAsync(id, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }
            var now = clock.UtcNow;
            var previous = contact.Reactivate(now);
            await contacts.UpdateAsync(contact, ct);
            if (previous is not null)
            {
                await changes.AddAsync(
                    ContactStageChange.Create(
                        id: Guid.NewGuid(),
                        contactId: contact.Id,
                        fromStage: previous,
                        toStage: ContactStage.Lead,
                        changedAt: now,
                        changedByUserId: user.UserId ?? Guid.Empty),
                    ct);
            }
            await uow.SaveChangesAsync(ct);
            return Results.Ok(ToDto(contact));
        });

        // Descarta (soft delete) os contatos de UM grupo: somem das listas/disparo e do Chat,
        // mas a linha e o opt-out ficam no banco (reversível). O WhatsApp do celular não é tocado.
        group.MapPost("/delete-by-group", async (
            DeleteByGroupRequest req,
            IContactRepository contacts,
            IClock clock,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.GroupTag))
            {
                return Results.Problem("groupTag é obrigatório", statusCode: 400);
            }
            // ExecuteUpdate persiste sozinho (não passa pelo UnitOfWork), igual ao delete anterior.
            var deleted = await contacts.DiscardByGroupTagAsync(req.GroupTag.Trim(), clock.UtcNow, ct);
            return Results.Ok(new { deleted });
        });

        // Cadastro manual: digita/cola uma lista de números (origem NÃO confiável, ao contrário do
        // import de grupo). Normaliza pra E.164, auto-corrige o 9º dígito quando dá, dedup pelo
        // E.164 e devolve o status de cada linha. Números avulsos caem no grupo "Avulsos" por padrão.
        group.MapPost("/manual", async (
            AddManualContactsRequest req,
            AddManualContactsUseCase useCase,
            CancellationToken ct) =>
        {
            if (req.Numbers is null || req.Numbers.Count == 0)
            {
                return Results.Problem("Cole ao menos um número.", statusCode: 400);
            }
            // Teto de sanidade contra um paste acidental gigante (tudo numa transação só).
            if (req.Numbers.Count > 2000)
            {
                return Results.Problem("Máximo de 2000 números por vez.", statusCode: 400);
            }
            var result = await useCase.ExecuteAsync(req.Numbers, req.GroupTag, ct);
            return Results.Ok(ToResponse(result));
        });

        group.MapPost("/{id:guid}/notes", async (
            Guid id,
            CreateNoteRequest req,
            IContactRepository contacts,
            IContactNoteRepository notes,
            ICurrentUserAccessor user,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
            {
                return Results.Problem("body is required", statusCode: 400);
            }
            var contact = await contacts.GetByIdAsync(id, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }
            var note = ContactNote.Create(
                id: Guid.NewGuid(),
                contactId: id,
                body: req.Body,
                createdAt: clock.UtcNow,
                createdByUserId: user.UserId ?? Guid.Empty);
            await notes.AddAsync(note, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Created($"/api/contacts/{id}/notes/{note.Id}", ToDto(note));
        });

        return app;
    }

    private static ContactDto ToDto(Contact c, bool sentElsewhere = false) => new(
        c.Id,
        c.Phone.E164,
        c.Name,
        c.GroupTag,
        c.Theme,
        c.Stage.ToString(),
        c.StageChangedAt,
        c.OptInAt,
        c.OptOutAt,
        c.LastSentAt,
        sentElsewhere);

    private static ContactNoteDto ToDto(ContactNote n) => new(n.Id, n.ContactId, n.Body, n.CreatedAt, n.CreatedByUserId);

    // Mapeia o resultado do use case pra resposta da API. Status vira string (ToString) — o projeto
    // não tem conversor global de enum, e o front consome os nomes ("Ok"/"Corrected"/...).
    private static ManualImportResponse ToResponse(ManualImportResult r) => new(
        r.Total, r.Added, r.Duplicated, r.Corrected, r.Invalid,
        r.Lines.Select(l => new ManualLineResponse(
            l.Input, l.Status.ToString(), l.Phone, l.Correction, l.Reason)).ToList());

    private static StageChangeDto ToDto(ContactStageChange c) => new(
        c.Id,
        c.FromStage?.ToString(),
        c.ToStage.ToString(),
        c.ChangedAt,
        c.ChangedByUserId);

    public sealed record PatchContactRequest(string? Stage, IReadOnlyList<string>? AddTags, IReadOnlyList<string>? RemoveTags);

    public sealed record CreateNoteRequest(string Body);

    public sealed record DeleteByGroupRequest(string GroupTag);

    public sealed record AddManualContactsRequest(IReadOnlyList<string> Numbers, string? GroupTag);

    public sealed record ManualImportResponse(
        int Total, int Added, int Duplicated, int Corrected, int Invalid, IReadOnlyList<ManualLineResponse> Lines);

    public sealed record ManualLineResponse(
        string Input, string Status, string? Phone, string? Correction, string? Reason);

    public sealed record ContactDto(
        Guid Id,
        string PhoneE164,
        string? Name,
        string? GroupTag,
        string? Theme,
        string Stage,
        DateTimeOffset? StageChangedAt,
        DateTimeOffset? OptInAt,
        DateTimeOffset? OptOutAt,
        DateTimeOffset? LastSentAt,
        // Consta no registro compartilhado (enviado/opt-out em algum ambiente). Só vira selo na UI
        // quando o LastSentAt local é nulo — i.e., foi tratado por OUTRO chip. False quando o
        // recurso está desligado.
        bool SentElsewhere = false);

    public sealed record ContactNoteDto(Guid Id, Guid ContactId, string Body, DateTimeOffset CreatedAt, Guid CreatedByUserId);

    public sealed record StageChangeDto(Guid Id, string? FromStage, string ToStage, DateTimeOffset ChangedAt, Guid ChangedByUserId);
}
