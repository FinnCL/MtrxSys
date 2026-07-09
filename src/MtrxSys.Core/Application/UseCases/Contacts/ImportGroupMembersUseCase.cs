using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Contacts;

public sealed class ImportGroupMembersUseCase(
    IWahaClient waha,
    IContactRepository contacts,
    IUnitOfWork uow,
    IClock clock,
    BrazilPhoneValidator phones,
    IOptions<DispatchOptions> opts)
{
    public async Task<ImportResult> ExecuteAsync(string groupId, string? groupTag, CancellationToken ct)
    {
        var sessionId = opts.Value.SessionId;
        var members = await waha.ListGroupParticipantsAsync(sessionId, groupId, ct);
        // Número conectado ("me") — não importamos o próprio número como contato/destinatário.
        var ownNumber = await waha.GetOwnPhoneE164Async(sessionId, ct);

        var imported = 0;
        var duplicated = 0;
        var failures = new List<ImportFailure>();
        var tag = groupTag ?? groupId;

        // 1ª passada: normaliza os telefones (capturando falhas individuais) e descarta o próprio
        // número. Faz isso ANTES do banco pra poder carregar os já-existentes num lote só.
        var onlyBrazilian = opts.Value.OnlyBrazilianContacts;
        var pending = new List<(WahaParticipant Member, PhoneNumber Phone)>();
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                PhoneNumber phone;
                if (onlyBrazilian)
                {
                    // Só números válidos para o BR; estrangeiros são ignorados (não viram contato).
                    var validated = phones.Validate(member.PhoneE164);
                    if (!validated.IsSuccess || validated.Value is null)
                    {
                        failures.Add(new ImportFailure(member.Id, member.PhoneE164, "Número não brasileiro (ignorado)."));
                        continue;
                    }
                    phone = validated.Value;
                }
                else
                {
                    phone = phones.NormalizeTrusted(member.PhoneE164);
                }
                if (ownNumber is not null && phone.E164 == ownNumber)
                {
                    continue; // pula o próprio número do remetente
                }
                pending.Add((member, phone));
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                failures.Add(new ImportFailure(member.Id, member.PhoneE164, ex.Message));
            }
#pragma warning restore CA1031
        }

        // Carrega num único SELECT os contatos já existentes pros telefones desta importação —
        // antes era uma consulta por participante (N+1), pesado em grupos grandes.
        var existingByPhone = await contacts.GetByPhonesAsync(
            pending.Select(x => x.Phone.E164).Distinct().ToList(), ct);
        var known = new Dictionary<string, Contact>(existingByPhone, StringComparer.Ordinal);

        foreach (var (member, phone) in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (known.TryGetValue(phone.E164, out var existing))
                {
                    // Já existe: re-importar = "quero estes contatos". Traz de volta quem foi
                    // descartado e garante o grupo (ex.: contato criado pelo sync sem grupo).
                    // Sem isso, um contato descartado ficava num beco sem saída (some da lista,
                    // sem como reativar pela interface).
                    if (existing.ReimportInto(tag, ownNumber))
                    {
                        await contacts.UpdateAsync(existing, ct);
                        imported++;
                    }
                    else
                    {
                        duplicated++;
                    }
                    continue;
                }

                var contact = Contact.Create(
                    id: Guid.NewGuid(),
                    phone: phone,
                    name: member.Name,
                    groupTag: tag,
                    theme: null,
                    optInAt: clock.UtcNow,
                    // Marca o chip que importou (= o chip conectado, co-membro do grupo). O disparo só
                    // manda pros contatos do chip conectado; assim, trocar de chip não dispara frio.
                    importedByPhone: ownNumber);

                await contacts.AddAsync(contact, ct);
                known[phone.E164] = contact; // telefone repetido no mesmo grupo reaproveita o contato
                imported++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                failures.Add(new ImportFailure(member.Id, member.PhoneE164, ex.Message));
            }
#pragma warning restore CA1031
        }

        await uow.SaveChangesAsync(ct);
        return new ImportResult(Total: members.Count, Imported: imported, Duplicated: duplicated, Failures: failures);
    }
}

public sealed record ImportFailure(string ParticipantId, string Phone, string Reason);

public sealed record ImportResult(int Total, int Imported, int Duplicated, IReadOnlyList<ImportFailure> Failures)
{
    public int Failed => Failures.Count;
}
