using Npgsql;

namespace MtrxSys.Infrastructure.SharedLedger;

// Encapsula o NpgsqlDataSource do registro compartilhado (um banco SEPARADO dos 10, fora do
// MtrxDbContext) e garante as tabelas compartilhadas (`phone_ledger` e `search_usage`) uma única vez
// por processo, de forma idempotente (CREATE TABLE IF NOT EXISTS — seguro sob concorrência entre os
// ambientes). Registrado como singleton.
public sealed class SharedLedgerDataSource : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    public SharedLedgerDataSource(string connectionString)
    {
        // Blindagem: timeouts CURTOS. Este banco é auxiliar e fail-open (phone_ledger no disparo,
        // contador de busca na coleta). Com os defaults (connect 15s), um banco compartilhado
        // configurado mas FORA do ar travaria a coleta ~15s por chamada antes de cair no fail-open.
        // 5s pra conectar e 10s por comando bastam e mantêm o fluxo responsivo na má configuração.
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 10,
        };
        _dataSource = new NpgsqlDataSourceBuilder(csb.ConnectionString).Build();
    }

    // Abre uma conexão, garantindo antes que a tabela exista. Se o banco estiver fora, a exceção
    // sobe pro chamador (que é fail-open e a engole) e _ready continua false → tenta de novo depois.
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        return await _dataSource.OpenConnectionAsync(ct);
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_ready)
        {
            return;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_ready)
            {
                return;
            }
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                """
                CREATE TABLE IF NOT EXISTS phone_ledger (
                    phone_e164 varchar(20)  PRIMARY KEY,
                    status     smallint     NOT NULL,
                    chip       varchar(8),
                    updated_at timestamptz  NOT NULL DEFAULT now()
                );
                CREATE TABLE IF NOT EXISTS search_usage (
                    id         smallint     PRIMARY KEY,
                    count      bigint       NOT NULL DEFAULT 0,
                    updated_at timestamptz  NOT NULL DEFAULT now()
                );
                """,
                conn);
            await cmd.ExecuteNonQueryAsync(ct);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _dataSource.DisposeAsync();
    }
}
