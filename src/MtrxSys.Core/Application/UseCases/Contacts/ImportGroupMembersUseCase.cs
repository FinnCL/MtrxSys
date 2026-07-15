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
        // Número conectado ("me"): serve pra NÃO importar o próprio número como destinatário e pra
        // marcar quem importou cada contato.
        var ownNumber = await waha.GetOwnPhoneE164Async(sessionId, ct);
        // Sem o número do chip, PARA. Não é preciosismo: `ImportedByPhone` nulo é lido pelo motor
        // como "contato legado, de chip desconhecido", e o gate anti-463 PULA esses. Importar assim
        // criaria contatos que o disparo nunca alcança — a tela diria "5 importados" e o envio
        // pularia os 5, com o operador sem pista do porquê. Falhar aqui é recuperável (é só clicar
        // de novo); o silêncio não é. Além disso, sem "me" o próprio chip entraria como contato e
        // viraria auto-envio.
        if (string.IsNullOrWhiteSpace(ownNumber))
        {
            throw new InvalidOperationException(
                "Não deu pra ler o número do chip conectado agora, então a importação foi cancelada "
                + "(sem ele os contatos nasceriam sem dono e o disparo os pularia em silêncio). "
                + "Confira se o chip está conectado e tente de novo.");
        }

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
                // Estrangeiro é ignorado (não vira contato). A pergunta é "é brasileiro?" — pelo
                // CÓDIGO DO PAÍS, não pela validade. Antes isto usava Validate, que REJEITA celular
                // BR no formato antigo (sem o 9º dígito): um grupo de conhecidos com números legados
                // importava ZERO contatos, todos rotulados "não brasileiro" — mentira, e apontando
                // pra causa errada. Ver BrazilPhoneValidator.IsPlausibleBrazilian.
                if (onlyBrazilian && !phones.IsPlausibleBrazilian(member.PhoneE164))
                {
                    failures.Add(new ImportFailure(member.Id, member.PhoneE164, "Número não brasileiro (ignorado)."));
                    continue;
                }
                // NormalizeTrusted (e não Validate): o número veio do WhatsApp, então é um id de
                // roteamento REAL. Preserva a forma que ele nos deu — inserir o 9º dígito "corrigiria"
                // pra um número que o WhatsApp não conhece, e isso é 463, que é gatilho de ban.
                var phone = phones.NormalizeTrusted(member.PhoneE164);
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
