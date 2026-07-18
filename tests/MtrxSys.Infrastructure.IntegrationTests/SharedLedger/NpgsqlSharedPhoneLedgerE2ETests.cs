using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.SharedLedger;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.SharedLedger;

/// <summary>
/// E2E do registro compartilhado contra Postgres REAL (Testcontainers), cobrindo o UPSERT em LOTE do
/// backfill — a query nova, compliance-crítica (reafirma opt-outs). Valida: traduz/executa, faz upsert
/// de novos, faz upgrade de "enviado" (1) para "opt-out" (2), e PRESERVA o chip de origem no conflito
/// (o backfill de outro ambiente não pode destruir a proveniência).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _ds/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class NpgsqlSharedPhoneLedgerE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private SharedLedgerDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _ds = new SharedLedgerDataSource(_pg.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private NpgsqlSharedPhoneLedger Ledger(string chip) =>
        new(_ds, Options.Create(new SharedLedgerOptions { Mode = SharedLedgerMode.Enforce, Chip = chip }),
            NullLogger<NpgsqlSharedPhoneLedger>.Instance);

    [Fact]
    public async Task Batch_inserts_new_and_upgrades_sent_to_optout()
    {
        var ledger = Ledger("A");
        // +...02 já consta como ENVIADO (status 1); +...01 é novo. O lote deve deixar os dois em opt-out.
        await ledger.MarkSentAsync("+5511999990002", CancellationToken.None);

        var phones = new[] { "+5511999990001", "+5511999990002" };
        await ledger.MarkOptOutBatchAsync(phones, CancellationToken.None);

        (await ledger.GetStatusAsync("+5511999990001", CancellationToken.None))
            .Should().Be(SharedLedgerStatus.OptedOut);
        (await ledger.GetStatusAsync("+5511999990002", CancellationToken.None))
            .Should().Be(SharedLedgerStatus.OptedOut);
    }

    [Fact]
    public async Task Batch_preserves_origin_chip_on_conflict()
    {
        // Opt-out veio do chip B; o backfill do chip A NÃO pode sobrescrever o chip de origem.
        await Ledger("B").MarkOptOutAsync("+5511999990010", CancellationToken.None);
        var phones = new[] { "+5511999990010" };
        await Ledger("A").MarkOptOutBatchAsync(phones, CancellationToken.None);

        await using var conn = await _ds.OpenAsync(CancellationToken.None);
        await using var cmd = new NpgsqlCommand(
            "SELECT chip FROM phone_ledger WHERE phone_e164 = @p", conn);
        cmd.Parameters.AddWithValue("p", "+5511999990010");
        var chip = (string?)await cmd.ExecuteScalarAsync();

        chip.Should().Be("B"); // proveniência preservada (não vira "A")
    }

    // ── Dedup CROSS-CHIP (habilita o re-envio do aquecimento sem furar o anti-spam) ──────────────

    [Fact]
    public async Task Sent_pelo_MESMO_chip_NAO_suprime()
    {
        // Chip A enviou pra X. Consultado pelo PRÓPRIO chip A, X não consta como suprimido: re-enviar do
        // mesmo chip é continuidade (o LastSentAt LOCAL governa), não o cenário do dedup. Sem isto, o
        // reset diário do aquecimento nunca voltaria a alcançar os respondedores.
        var a = Ledger("A");
        await a.MarkSentAsync("+5511999990020", CancellationToken.None);

        (await a.GetStatusAsync("+5511999990020", CancellationToken.None))
            .Should().Be(SharedLedgerStatus.None);
        (await a.GetSuppressedAsync(["+5511999990020"], CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Sent_por_OUTRO_chip_suprime()
    {
        // Chip B enviou pra X. Consultado pelo chip A, X é suprimido — dois chips não podem cair na
        // mesma pessoa (denúncia de spam). O dedup cross-chip continua valendo.
        await Ledger("B").MarkSentAsync("+5511999990021", CancellationToken.None);
        var a = Ledger("A");

        (await a.GetStatusAsync("+5511999990021", CancellationToken.None))
            .Should().Be(SharedLedgerStatus.Sent);
        (await a.GetSuppressedAsync(["+5511999990021"], CancellationToken.None))
            .Should().Contain("+5511999990021");
    }

    [Fact]
    public async Task OptOut_suprime_ate_no_MESMO_chip()
    {
        // Opt-out SEMPRE vence — inclusive pra quem consulta pelo mesmo chip que registrou (LGPD).
        var a = Ledger("A");
        await a.MarkOptOutAsync("+5511999990022", CancellationToken.None);

        (await a.GetStatusAsync("+5511999990022", CancellationToken.None))
            .Should().Be(SharedLedgerStatus.OptedOut);
        (await a.GetSuppressedAsync(["+5511999990022"], CancellationToken.None))
            .Should().Contain("+5511999990022");
    }
}
