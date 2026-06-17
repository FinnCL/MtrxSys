using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MtrxSys.Infrastructure.Collector;
using MtrxSys.Infrastructure.SharedLedger;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// Contador de busca COMPARTILHADO contra Postgres REAL (Testcontainers). Prova o que o medidor em
/// memória não dá: o total PERSISTE (a faixa grátis do Serper é vitalícia) e AGREGA os ambientes —
/// dois "ambientes" (data sources separados no mesmo banco) somam no mesmo contador.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _ds/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class SharedSearchUsageMeterTests : IAsyncLifetime
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

    private static SharedSearchUsageMeter Meter(SharedLedgerDataSource ds) =>
        new(ds, NullLogger<SharedSearchUsageMeter>.Instance);

    [Fact]
    public async Task Sem_registro_a_contagem_e_zero()
    {
        (await Meter(_ds).GetCountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Incrementa_e_le_o_total()
    {
        var meter = Meter(_ds);

        await meter.RecordAsync(CancellationToken.None);
        await meter.RecordAsync(CancellationToken.None);
        await meter.RecordAsync(CancellationToken.None);

        (await meter.GetCountAsync(CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public async Task Agrega_entre_ambientes_no_mesmo_banco()
    {
        // Dois "ambientes" = data sources separados apontando pro MESMO banco compartilhado.
        await using var ds2 = new SharedLedgerDataSource(_pg.GetConnectionString());
        var ambienteA = Meter(_ds);
        var ambienteB = Meter(ds2);

        await ambienteA.RecordAsync(CancellationToken.None);
        await ambienteB.RecordAsync(CancellationToken.None);
        await ambienteA.RecordAsync(CancellationToken.None);

        // Qualquer um lê o total AGREGADO (3) — é o número "rumo aos 2.500" somando os ambientes.
        (await ambienteB.GetCountAsync(CancellationToken.None)).Should().Be(3);
        (await ambienteA.GetCountAsync(CancellationToken.None)).Should().Be(3);
    }
}
