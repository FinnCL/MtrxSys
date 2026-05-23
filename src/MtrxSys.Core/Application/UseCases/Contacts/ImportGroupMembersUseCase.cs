using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.UseCases.Contacts;

public sealed class ImportGroupMembersUseCase(
    IWahaClient waha,
    IContactRepository contacts,
    IUnitOfWork uow,
    IClock clock,
    IOptions<DispatchOptions> opts)
{
    public async Task<ImportResult> ExecuteAsync(string groupId, string? groupTag, CancellationToken ct)
    {
        var sessionId = opts.Value.SessionId;
        var members = await waha.ListGroupParticipantsAsync(sessionId, groupId, ct);

        var imported = 0;
        var duplicated = 0;
        var failures = new List<ImportFailure>();

        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var existing = await contacts.GetByPhoneAsync(member.PhoneE164, ct);
                if (existing is not null)
                {
                    duplicated++;
                    continue;
                }

                var phone = PhoneNumber.FromValidatedE164(member.PhoneE164);
                var contact = Contact.Create(
                    id: Guid.NewGuid(),
                    phone: phone,
                    name: member.Name,
                    groupTag: groupTag ?? groupId,
                    theme: null,
                    optInAt: clock.UtcNow);

                await contacts.AddAsync(contact, ct);
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
