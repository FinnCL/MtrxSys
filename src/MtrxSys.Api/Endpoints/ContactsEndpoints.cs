using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;

namespace MtrxSys.Api.Endpoints;

public static class ContactsEndpoints
{
    public static IEndpointRouteBuilder MapContactsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts");

        // "Validar lista" (pré-voo anti-463): checa quais Leads têm conta no WhatsApp e descarta os
        // inexistentes ANTES do disparo. Dispara em background (paced) e reporta o progresso. Ver
        // NumberValidationRunner.
        group.MapPost("/validate/start", (BackgroundServices.NumberValidationRunner runner) =>
            Results.Ok(new { started = runner.Start(), status = runner.Status }));
        group.MapGet("/validate/status", (BackgroundServices.NumberValidationRunner runner) =>
            Results.Ok(runner.Status));

        group.MapGet("/", async (
            string? stage,
            string? groupTag,
            IContactRepository contacts,
            ISharedPhoneLedger ledger,
            ISystemStateRepository state,
            CancellationToken ct) =>
        {
            // Chip conectado agora — pra marcar quais contatos são "do chip atual" (podem disparar) e
            // quais são de outro chip (o front mostra em cinza/desabilitado). Null = desconhecido.
            var currentChip = (await state.GetAsync(ct)).WarmupPhone;
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
            return Results.Ok(list.Select(c => ToDto(c, suppressed.Contains(c.Phone.E164), currentChip)));
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
            // OBS: a reativação limpa só o opt-out LOCAL. NÃO mexe no registro compartilhado de
            // propósito — o opt-out do ledger é global e "sempre vence": um "SAIR" pode ter vindo de
            // OUTRO chip, e uma reativação LOCAL não pode dessuprimir globalmente (risco de LGPD).
            // Re-engajar cross-ambiente, se um dia for preciso, deve ser uma ação global explícita.
            return Results.Ok(ToDto(contact));
        });

        // Libera UM contato pra um novo disparo: zera o LastSentAt dele (equivalente per-contato do
        // "Renovar lista") E apaga o histórico de jobs dele (Enviada/Falhou/Pulada). Sem esse segundo
        // passo, o relatório de envios seguia mostrando o status VELHO ("Enviada") depois de liberar —
        // era a inconsistência: em Contatos o contato voltava a "Novo", mas no Disparo continuava
        // "Enviada". Fila ativa (Pending/Retrying) fica; Stage/OptOut não mudam.
        group.MapPost("/{id:guid}/resend", async (
            Guid id,
            IContactRepository contacts,
            IDispatchJobRepository jobs,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var contact = await contacts.GetByIdAsync(id, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }
            if (contact.ClearLastSent())
            {
                await contacts.UpdateAsync(contact, ct);
                await uow.SaveChangesAsync(ct);
            }
            // Limpa o histórico do relatório pra este contato (idempotente; bulk fora do change tracker).
            await jobs.DeleteHistoryByContactAsync(id, ct);
            return Results.Ok(ToDto(contact));
        });

        // Descarta (soft delete) UM contato: some das listas, do disparo, do Chat e do resultado dos
        // envios, mas a linha e o opt-out ficam no banco (reversível; anti-ban preservado). É o mesmo
        // efeito do "Descartar contatos deste grupo", só que pra um contato.
        group.MapPost("/{id:guid}/discard", async (
            Guid id,
            IContactRepository contacts,
            IClock clock,
            IUnitOfWork uow,
            CancellationToken ct) =>
        {
            var contact = await contacts.GetByIdAsync(id, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }
            var discarded = contact.Discard(clock.UtcNow);
            if (discarded)
            {
                await contacts.UpdateAsync(contact, ct);
                await uow.SaveChangesAsync(ct);
            }
            return Results.Ok(new { discarded });
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
            return await SaveOrConflict(() => useCase.ExecuteAsync(req.Numbers, req.GroupTag, ct));
        });

        // ── Importar da agenda Google ────────────────────────────────────────────────────────────
        // Compara a conta Google com o banco e mostra a diferença. NÃO grava, e NÃO é automático: o
        // disparo escreve nessa agenda antes de cada envio frio, então uma varredura periódica traria
        // gente pra dentro sem ninguém decidir — e o sistema estaria lendo o próprio rastro.
        //
        // O laço não acontece por dois motivos independentes: a prévia ignora quem já está no banco
        // (e tudo que o disparo criou está lá), e o índice único por DÍGITOS recusa duplicata mesmo
        // se a comparação de formato falhar — que foi exatamente o erro que gerou 50 contatos
        // repetidos em 2026-07-27.
        group.MapGet("/google/preview", async (
            IContactAddressBookSync addressBook,
            IContactRepository contacts,
            BrazilPhoneValidator phones,
            CancellationToken ct) =>
        {
            if (!addressBook.IsEnabled)
            {
                return Results.Ok(GooglePreviewResponse.Desligado());
            }
            var naConta = await addressBook.ListAsync(ct);
            if (naConta is null)
            {
                // Falha de leitura NUNCA vira "está tudo igual". Sem isto, um token morto produziria a
                // mesma tela de quando a conta está de fato sincronizada.
                return Results.Ok(GooglePreviewResponse.Ilegivel());
            }

            // Compara contra TODOS os contatos, inclusive descartados e opt-out. Usar o filtro padrão
            // aqui ofereceria de volta quem pediu pra SAIR, porque opt-out não aparece na resposta dele.
            // Dedup pelos DÍGITOS, não pelo texto: "+5588…" e "5588…" são a mesma pessoa.
            var noBanco = await contacts.ListAllPhoneStatusAsync(ct);
            var ativos = noBanco.Where(c => c.Ativo).Select(c => Digits(c.PhoneE164))
                .ToHashSet(StringComparer.Ordinal);
            var suprimidos = noBanco.Where(c => !c.Ativo).Select(c => Digits(c.PhoneE164))
                .ToHashSet(StringComparer.Ordinal);

            var novos = new List<GoogleContactDto>();
            var invalidos = new List<GoogleInvalidDto>();
            var jaTem = 0;
            var bloqueados = 0;
            foreach (var e in naConta)
            {
                var d = Digits(e.PhoneE164);
                if (ativos.Contains(d))
                {
                    jaTem++;
                }
                else if (suprimidos.Contains(d))
                {
                    // Conhecido, mas descartado ou com opt-out. Contado à parte pra a soma fechar
                    // contra o total da conta — e NUNCA oferecido: reimportar quem pediu pra sair é o
                    // erro que não tem desfazer.
                    bloqueados++;
                }
                else if (!phones.IsPlausibleBrazilian(e.PhoneE164))
                {
                    // Mostrado, não escondido: sumir com o número faria a soma não fechar e o operador
                    // ficaria procurando o que faltou.
                    invalidos.Add(new GoogleInvalidDto(e.PhoneE164, "fora do padrão brasileiro"));
                }
                else
                {
                    novos.Add(new GoogleContactDto(e.PhoneE164, e.Name));
                }
            }
            return Results.Ok(new GooglePreviewResponse(
                "ok", naConta.Count, jaTem, bloqueados, novos, invalidos));
        });

        // Grava SÓ o que o operador marcou. Reusa o cadastro manual (normalização, dedup e relatório
        // por linha já resolvidos ali) em vez de duplicar essa lógica.
        group.MapPost("/google/import", async (
            GoogleImportRequest req,
            AddManualContactsUseCase useCase,
            IContactRepository contacts,
            ISystemStateRepository stateRepo,
            CancellationToken ct) =>
        {
            if (req.Phones is null || req.Phones.Count == 0)
            {
                return Results.Problem("Selecione ao menos um contato.", statusCode: 400);
            }
            if (req.Phones.Count > 2000)
            {
                return Results.Problem("Máximo de 2000 por vez.", statusCode: 400);
            }
            // Dono = chip conectado. Sem isto o contato nasce e NUNCA recebe: o gate anti-463 pula quem
            // não tem dono. RECUSA quando não há chip, em vez de importar mudo: o operador clicaria,
            // veria "10 importados" e descobriria semanas depois que nenhum saiu.
            var chip = (await stateRepo.GetAsync(ct)).WarmupPhone;
            if (string.IsNullOrWhiteSpace(chip))
            {
                return Results.Problem(
                    "Nenhum chip conectado. Os contatos nasceriam sem dono e o disparo os pularia em "
                    + "silêncio. Registre o chip antes de importar.",
                    statusCode: 409);
            }
            // RECONFERE os suprimidos AQUI, não só na prévia. Entre abrir a prévia e clicar em
            // importar, alguém pode ter descartado um contato em outra aba — e aí a lista da tela está
            // velha. Sem esta checagem, o clique ressuscita quem acabou de ser descartado.
            //
            // O banco não protege sozinho: o índice único cobre só linhas ATIVAS, e o dedup do use case
            // casa por TEXTO exato, então um descartado gravado em outro formato não é encontrado.
            var suprimidos = (await contacts.ListAllPhoneStatusAsync(ct))
                .Where(c => !c.Ativo)
                .Select(c => PhoneDigits.Of(c.PhoneE164))
                .ToHashSet(StringComparer.Ordinal);
            var permitidos = req.Phones.Where(p => !suprimidos.Contains(PhoneDigits.Of(p))).ToList();
            var barrados = req.Phones.Count - permitidos.Count;
            if (permitidos.Count == 0)
            {
                return Results.Problem(
                    $"Os {barrados} contato(s) selecionados estão descartados ou com opt-out no sistema. "
                    + "Quem pediu pra sair não volta por importação.",
                    statusCode: 409);
            }
            return await SaveOrConflict(
                () => useCase.ExecuteAsync(permitidos, "Google", ct, chip),
                r => r with { Barrados = barrados });
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

        // "Migrar contatos para este chip": regrava o ImportedByPhone dos contatos que estão marcados com
        // OUTRO chip (ou sem marca), liberando o disparo por eles.
        //
        // ⚠️ ISTO AFROUXA UMA TRAVA ANTI-BAN, DE PROPÓSITO E SOB DECISÃO DO OPERADOR. O ImportedByPhone
        // existe porque disparar de um chip pra contato que veio de grupo de OUTRO chip dá 463, que é
        // gatilho de ban. Re-importar o grupo é o caminho honesto: ele PROVA que o chip novo está no
        // grupo. Migrar na mão AFIRMA um vínculo que pode não existir — por isso a tela avisa e exige
        // confirmação, e por isso esta rota não é chamada por nada automático.
        //
        // Existe caso legítimo que a re-importação NUNCA resolve: contato adicionado à mão nunca veio de
        // grupo nenhum, então fica preso ao chip antigo pra sempre. Sem esta ação, a única saída seria
        // recriar os contatos.
        // Sem IUnitOfWork: o ExecuteUpdate grava direto no banco, não passa pelo change tracker. Manter o
        // parâmetro sugeriria um SaveChanges que nunca acontece.
        group.MapPost("/reassign-to-current-chip", async (
            IContactRepository contacts,
            IDispatchJobRepository jobs,
            ISystemStateRepository state,
            CancellationToken ct) =>
        {
            var currentChip = (await state.GetAsync(ct)).WarmupPhone;
            if (string.IsNullOrWhiteSpace(currentChip))
            {
                // Sem saber qual é o chip conectado, regravar marcaria os contatos com nada — que o motor
                // lê como "legado, de chip desconhecido" e PULA. Ficaria pior do que estava, em silêncio.
                return Results.Problem(
                    "Não deu pra ler o número do chip conectado agora, então nada foi alterado. "
                    + "Confira se o chip está registrado no emulador e tente de novo.",
                    statusCode: 409);
            }

            // Uma instrução SQL, sem materializar a base. Inclui quem está em opt-out de propósito: essa
            // é uma trava INDEPENDENTE e continua valendo; deixá-los de fora criaria contatos
            // meio-migrados que voltariam a travar se o opt-out fosse revertido.
            var moved = await contacts.ReassignToChipAsync(currentChip, ct);
            // Migrar o contato não basta: quem JÁ tinha sido pulado pelo gate ficou em "Pulado", que é
            // estado final. Sem devolver esses jobs à fila, o operador migraria, veria a fila igual e
            // concluiria que a migração não funcionou.
            var requeued = await jobs.RequeueSkippedByChipGateAsync(currentChip, ct);
            var total = await contacts.CountByFilterAsync(new ContactFilter(ExcludeOptedOut: false), ct);
            return Results.Ok(new { moved, total, requeued, chip = currentChip });
        });

        return app;
    }

    private static ContactDto ToDto(Contact c, bool sentElsewhere = false, string? currentChip = null) => new(
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
        sentElsewhere,
        c.ImportedByPhone,
        // ESTRITO (regra do usuário): "do chip atual" (habilitado, não cinza) SÓ quando a marca bate
        // com o chip conectado agora. Contato sem marca (legado) OU de outro chip → CINZA/desabilitado,
        // sem envio. Só sai do cinza re-importando o grupo COM o chip conectado (aí ganha a marca dele) —
        // dinâmico: trocou de chip, muda quem está habilitado. (currentChip null = desconhecido → não marca.)
        FromCurrentChip: currentChip is null
            || string.Equals(c.ImportedByPhone, currentChip, StringComparison.Ordinal));

    private static ContactNoteDto ToDto(ContactNote n) => new(n.Id, n.ContactId, n.Body, n.CreatedAt, n.CreatedByUserId);

    // Mapeia o resultado do use case pra resposta da API. Status vira string (ToString) — o projeto
    // não tem conversor global de enum, e o front consome os nomes ("Ok"/"Corrected"/...).
    // Toda gravação de contato pode esbarrar no índice único por dígitos (IX_contacts_phone_digits)
    // por uma corrida que o dedup não cobre: dois imports ao mesmo tempo, ou o Google sync inserindo o
    // mesmo número em paralelo, entre a leitura do dedup e o SaveChanges. Isso é concorrência, não erro
    // do operador — vira 409 "tente de novo", nunca um 500 com stack trace vazando. Centralizado pra os
    // três caminhos de escrita (manual, import Google, e o de grupo tem o seu) responderem igual.
    private static async Task<IResult> SaveOrConflict(
        Func<Task<ManualImportResult>> save, Func<ManualImportResponse, ManualImportResponse>? adjust = null)
    {
        try
        {
            var resp = ToResponse(await save());
            return Results.Ok(adjust is null ? resp : adjust(resp));
        }
        catch (DbUpdateException)
        {
            return Results.Problem(
                "A gravação esbarrou num contato criado em paralelo. Tente novamente.", statusCode: 409);
        }
    }

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

    public sealed record GoogleContactDto(string Phone, string? Name);

    public sealed record GoogleInvalidDto(string Phone, string Motivo);

    /// <param name="Estado">
    /// "ok", "desligado" (sem provider) ou "ilegivel" (não deu pra ler a conta).
    /// <para>Três estados, e não um booleano, porque "não consegui ler" e "não há nada novo" produzem
    /// telas idênticas se forem o mesmo valor — e a primeira é uma pane que o operador precisa ver.</para>
    /// </param>
    /// <param name="Bloqueados">Conhecidos mas descartados/opt-out. Contados pra a soma fechar, nunca oferecidos.</param>
    public sealed record GooglePreviewResponse(
        string Estado,
        int NaConta,
        int JaNoSistema,
        int Bloqueados,
        IReadOnlyList<GoogleContactDto> Novos,
        IReadOnlyList<GoogleInvalidDto> Invalidos)
    {
        public static GooglePreviewResponse Desligado() => new("desligado", 0, 0, 0, [], []);

        public static GooglePreviewResponse Ilegivel() => new("ilegivel", 0, 0, 0, [], []);
    }

    public sealed record GoogleImportRequest(IReadOnlyList<string> Phones);

    private static string Digits(string raw) => PhoneDigits.Of(raw);

    /// <param name="Barrados">
    /// Selecionados que NÃO foram importados por estarem descartados ou com opt-out. Sempre 0 no
    /// cadastro manual; só a importação do Google preenche. Reportado em vez de silenciado: o operador
    /// escolheu N e precisa saber por que entraram menos.
    /// </param>
    public sealed record ManualImportResponse(
        int Total, int Added, int Duplicated, int Corrected, int Invalid, IReadOnlyList<ManualLineResponse> Lines)
    {
        public int Barrados { get; init; }
    }

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
        bool SentElsewhere = false,
        // Chip (número) que importou o contato. Null = legado/sem marca.
        string? ImportedByPhone = null,
        // true = importado pelo chip CONECTADO agora (co-membro dele) → pode disparar. false = de outro
        // chip ou legado → FRIO pra este chip, o disparo PULA (anti-463). O front mostra os false em
        // CINZA/desabilitado com selo "outro chip". Quando o chip conectado é desconhecido, vem true
        // (não desabilita à toa).
        bool FromCurrentChip = true);

    public sealed record ContactNoteDto(Guid Id, Guid ContactId, string Body, DateTimeOffset CreatedAt, Guid CreatedByUserId);

    public sealed record StageChangeDto(Guid Id, string? FromStage, string ToStage, DateTimeOffset ChangedAt, Guid ChangedByUserId);
}
