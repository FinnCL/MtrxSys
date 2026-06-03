using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using Npgsql;

namespace MtrxSys.Infrastructure.SharedLedger;

// Acesso ao registro compartilhado via Npgsql puro (fora do MtrxDbContext, que é por-ambiente).
// FAIL-OPEN por contrato: qualquer exceção de infraestrutura é logada e engolida — leitura vira
// "não suprime", escrita vira no-op. NUNCA lança no caminho do disparo. Só cancelamento (shutdown)
// propaga, seguindo o padrão do resto do código. O try/catch fail-open vive num único helper
// (GuardedAsync) pra garantir o MESMO tratamento em todas as operações.
public sealed class NpgsqlSharedPhoneLedger(
    SharedLedgerDataSource dataSource,
    IOptions<SharedLedgerOptions> options,
    ILogger<NpgsqlSharedPhoneLedger> log) : ISharedPhoneLedger
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    private readonly SharedLedgerOptions _opts = options.Value;

    public bool IsEnabled => _opts.Mode != SharedLedgerMode.Off;
    public bool IsEnforcing => _opts.Mode == SharedLedgerMode.Enforce;

    public Task<bool> IsSuppressedAsync(string phoneE164, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(phoneE164))
        {
            return Task.FromResult(false);
        }
        return GuardedAsync(nameof(IsSuppressedAsync), fallback: false, async conn =>
        {
            await using var cmd = new NpgsqlCommand("SELECT 1 FROM phone_ledger WHERE phone_e164 = @p LIMIT 1", conn);
            cmd.Parameters.AddWithValue("p", phoneE164);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }, ct);
    }

    public Task<IReadOnlySet<string>> GetSuppressedAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct)
    {
        if (!IsEnabled || phonesE164 is null || phonesE164.Count == 0)
        {
            return Task.FromResult(Empty);
        }
        var arr = phonesE164.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray();
        if (arr.Length == 0)
        {
            return Task.FromResult(Empty);
        }
        return GuardedAsync(nameof(GetSuppressedAsync), Empty, async conn =>
        {
            await using var cmd = new NpgsqlCommand("SELECT phone_e164 FROM phone_ledger WHERE phone_e164 = ANY(@p)", conn);
            cmd.Parameters.AddWithValue("p", arr);
            var set = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                set.Add(reader.GetString(0));
            }
            return (IReadOnlySet<string>)set;
        }, ct);
    }

    // status 1 = enviado; ON CONFLICT DO NOTHING pra não rebaixar um opt-out (2) já gravado.
    public Task MarkSentAsync(string phoneE164, CancellationToken ct) => ExecAsync(
        "INSERT INTO phone_ledger (phone_e164, status, chip, updated_at) VALUES (@p, 1, @c, now()) "
        + "ON CONFLICT (phone_e164) DO NOTHING",
        phoneE164, nameof(MarkSentAsync), ct);

    // status 2 = opt-out; sempre vence (sobrescreve "enviado") — vale pra todos os ambientes.
    public Task MarkOptOutAsync(string phoneE164, CancellationToken ct) => ExecAsync(
        "INSERT INTO phone_ledger (phone_e164, status, chip, updated_at) VALUES (@p, 2, @c, now()) "
        + "ON CONFLICT (phone_e164) DO UPDATE SET status = 2, chip = @c, updated_at = now()",
        phoneE164, nameof(MarkOptOutAsync), ct);

    private Task ExecAsync(string sql, string phoneE164, string op, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(phoneE164))
        {
            return Task.CompletedTask;
        }
        return GuardedAsync(op, fallback: true, async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", phoneE164);
            cmd.Parameters.AddWithValue("c", _opts.Chip);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    // Abre conexão e roda `body`, sempre fail-open: cancelamento propaga; qualquer outra falha é
    // logada e devolve `fallback`. Centraliza o tratamento que antes se repetia em cada operação.
    private async Task<T> GuardedAsync<T>(string op, T fallback, Func<NpgsqlConnection, Task<T>> body, CancellationToken ct)
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
#pragma warning disable CA1031 // fail-open: qualquer falha do registro não pode travar o disparo
        catch (Exception ex)
        {
            log.LogWarning(ex, "SharedPhoneLedger.{Op} falhou (fail-open: o disparo segue normal).", op);
            return fallback;
        }
#pragma warning restore CA1031
    }
}
