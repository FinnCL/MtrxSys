using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class HumanPhaseProgressRepository(MtrxDbContext db) : IHumanPhaseProgressRepository
{
    public async Task<int> CountOutboundActiveDaysAsync(DateOnly since, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }
        await using var cmd = conn.CreateCommand();
        // SQL cru porque o "dia de Brasília" não traduz pra LINQ. A conversão é EXPLÍCITA
        // (AT TIME ZONE 'UTC' e depois -3h) de propósito: um `timestamp::date` direto usaria o
        // TimeZone da SESSÃO do Postgres, que ninguém controla aqui e mudaria o resultado conforme
        // o servidor. O -3 fixo é o mesmo critério do IClock.ToBrasiliaDate (o Brasil não tem
        // horário de verão desde 2019), então o card e o gate contam o dia igual.
        cmd.CommandText = @"
SELECT COUNT(DISTINCT (((timestamp AT TIME ZONE 'UTC') - interval '3 hours')::date))
FROM chat_messages
WHERE direction = @outbound AND timestamp >= @anchor;";
        var anchorParam = cmd.CreateParameter();
        anchorParam.ParameterName = "anchor";
        anchorParam.Value = ToBrasiliaDayStartUtc(since);
        cmd.Parameters.Add(anchorParam);
        var directionParam = cmd.CreateParameter();
        directionParam.ParameterName = "outbound";
        directionParam.Value = (int)MessageDirection.Outbound;
        cmd.Parameters.Add(directionParam);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<ConversationTally>> ListConversationTalliesAsync(
        DateOnly since, CancellationToken ct)
    {
        var anchorUtc = ToBrasiliaDayStartUtc(since);
        // AsNoTracking: leitura pura pra um placar; nada aqui é mutado (ver a ressalva do
        // OptOutReconciler, que ao contrário PRECISA rastrear).
        var rows = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Timestamp >= anchorUtc)
            // Grupo não conta: a fase é sobre conversa de pessoa pra pessoa. O join também exclui
            // mensagem órfã de conversa apagada.
            .Join(
                db.Conversations.AsNoTracking().Where(c => !c.IsGroup),
                m => m.ConversationId,
                c => c.Id,
                (m, c) => new { m.ConversationId, c.Title, c.WaChatId, m.Direction })
            .GroupBy(x => new { x.ConversationId, x.Title, x.WaChatId })
            .Select(g => new ConversationTally(
                g.Key.ConversationId,
                g.Key.Title,
                g.Key.WaChatId,
                g.Count(x => x.Direction == MessageDirection.Inbound),
                g.Count(x => x.Direction == MessageDirection.Outbound)))
            .ToListAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyList<MessageStamp>> ListStampsForConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds, DateOnly since, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
        {
            return [];
        }
        var anchorUtc = ToBrasiliaDayStartUtc(since);
        // Usa o índice (conversation_id, timestamp) — por isso filtra por conversa ANTES do instante.
        return await db.ChatMessages.AsNoTracking()
            .Where(m => conversationIds.Contains(m.ConversationId) && m.Timestamp >= anchorUtc)
            .Select(m => new MessageStamp(m.ConversationId, m.Direction, m.Timestamp))
            .ToListAsync(ct);
    }

    // Início do dia-Brasília `since`, em instante absoluto. -3 fixo, igual ao IClock.ToBrasiliaDate:
    // não usa TimeZoneInfo de propósito, porque InvariantGlobalization=true torna a busca por id de
    // fuso indisponível/instável conforme o SO.
    // O ToUniversalTime() no fim NÃO é enfeite: o Npgsql RECUSA gravar/parametrizar DateTimeOffset
    // com offset != 0 em `timestamp with time zone` ("only offset 0 (UTC) is supported"). Sem ele o
    // -03:00 estoura em runtime, mesmo o instante sendo o mesmo.
    private static DateTimeOffset ToBrasiliaDayStartUtc(DateOnly since) =>
        new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3)).ToUniversalTime();
}
