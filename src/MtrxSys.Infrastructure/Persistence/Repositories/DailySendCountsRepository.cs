using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class DailySendCountsRepository(MtrxDbContext db) : IDailySendCountsRepository
{
    // AsNoTracking de propósito: IncrementAsync escreve via SQL cru (bypassa o EF), então uma
    // leitura RASTREADA devolveria o contador antigo do identity-map (EF não sobrescreve entidade
    // já rastreada com o valor novo do banco). Sem isso, dentro de um mesmo ciclo de disparo o teto
    // do aquecimento lia sempre o valor velho e era furado. Sem tracking, cada leitura é fresca.
    public Task<DailySendCount?> GetAsync(DateOnly dateUtc, CancellationToken ct) =>
        db.DailySendCounts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == dateUtc, ct);

    public async Task<int> IncrementAsync(DateOnly dateUtc, int warmupDayIndex, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO daily_send_counts (date_utc, sent_count, warmup_day_index)
VALUES (@date, 1, @warmup)
ON CONFLICT (date_utc) DO UPDATE SET sent_count = daily_send_counts.sent_count + 1
RETURNING sent_count;";
        var dateParam = cmd.CreateParameter();
        dateParam.ParameterName = "date";
        dateParam.Value = dateUtc;
        cmd.Parameters.Add(dateParam);
        var warmupParam = cmd.CreateParameter();
        warmupParam.ParameterName = "warmup";
        warmupParam.Value = warmupDayIndex;
        cmd.Parameters.Add(warmupParam);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i ? i : Convert.ToInt32(result);
    }
}
