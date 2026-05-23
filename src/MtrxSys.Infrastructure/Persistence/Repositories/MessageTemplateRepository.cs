using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Messages;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class MessageTemplateRepository(MtrxDbContext db) : IMessageTemplateRepository
{
    public async Task<IReadOnlyList<MessageTemplate>> ListActiveBySlotAsync(MessageSlot slot, CancellationToken ct) =>
        await db.MessageTemplates.Where(t => t.Slot == slot && t.Active).ToListAsync(ct);

    public async Task<IReadOnlyList<MessageTemplate>> ListAllAsync(CancellationToken ct) =>
        await db.MessageTemplates.OrderBy(t => t.Slot).ToListAsync(ct);

    public Task<MessageTemplate?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.MessageTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task AddAsync(MessageTemplate template, CancellationToken ct) =>
        await db.MessageTemplates.AddAsync(template, ct);
}
