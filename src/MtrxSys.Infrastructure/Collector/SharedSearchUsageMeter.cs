using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Infrastructure.SharedLedger;
using Npgsql;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Medidor de uso COMPARTILHADO entre os 10 ambientes: grava num contador de uma linha (tabela
/// search_usage) no banco compartilhado (mesmo Postgres do phone_ledger). Assim o total soma todos
/// os ambientes e PERSISTE (a faixa grátis do Serper é vitalícia). FAIL-OPEN: qualquer falha de
/// infra é logada e engolida (nunca trava a busca) — só cancelamento (shutdown) propaga.
/// </summary>
internal sealed class SharedSearchUsageMeter(
    SharedLedgerDataSource dataSource,
    ILogger<SharedSearchUsageMeter> log) : ISearchUsageMeter
{
    public async Task RecordAsync(CancellationToken ct) =>
        await GuardedAsync(nameof(RecordAsync), async conn =>
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO search_usage (id, count, updated_at) VALUES (1, 1, now()) "
                + "ON CONFLICT (id) DO UPDATE SET count = search_usage.count + 1, updated_at = now()",
                conn);
            await cmd.ExecuteNonQueryAsync(ct);
            return 0L;
        }, ct);

    public Task<long> GetCountAsync(CancellationToken ct) =>
        GuardedAsync(nameof(GetCountAsync), async conn =>
        {
            await using var cmd = new NpgsqlCommand("SELECT count FROM search_usage WHERE id = 1", conn);
            return await cmd.ExecuteScalarAsync(ct) is long c ? c : 0L;
        }, ct);

    private async Task<long> GuardedAsync(string op, Func<NpgsqlConnection, Task<long>> body, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenAsync(ct);
            return await body(conn);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // fail-open: o contador é informativo e não pode travar a busca
        catch (Exception ex)
        {
            log.LogWarning(ex, "SharedSearchUsageMeter.{Op} falhou (fail-open).", op);
            return 0L;
        }
#pragma warning restore CA1031
    }
}
