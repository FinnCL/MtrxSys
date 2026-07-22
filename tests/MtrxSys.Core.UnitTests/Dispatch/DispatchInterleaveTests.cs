using FluentAssertions;
using MtrxSys.Core.Application.UseCases.Dispatch;

namespace MtrxSys.Core.UnitTests.Dispatch;

public sealed class DispatchInterleaveTests
{
    [Fact]
    public void Abre_com_o_seed_e_intercala_sem_bloco()
    {
        // P=3, N=6 → seed nos slots 0, 3, 6 (espalhado, não em bloco). 1º da fila SEMPRE é do seed.
        string[] seed = ["A", "B", "C"];
        string[] cold = ["1", "2", "3", "4", "5", "6"];

        var seq = DispatchInterleave.Interleave(seed, cold);

        seq.Should().Equal("A", "1", "2", "B", "3", "4", "C", "5", "6");
    }

    [Fact]
    public void So_frios_quando_seed_vazio()
    {
        string[] seed = [];
        string[] cold = ["1", "2"];

        DispatchInterleave.Interleave(seed, cold).Should().Equal("1", "2");
    }

    [Fact]
    public void So_seed_quando_frios_vazio()
    {
        string[] seed = ["A", "B"];
        string[] cold = [];

        DispatchInterleave.Interleave(seed, cold).Should().Equal("A", "B");
    }

    [Fact]
    public void Primeiro_da_fila_e_sempre_do_seed_quando_ha_seed()
    {
        string[] seed = ["X"];
        string[] cold = ["1", "2", "3", "4", "5"];

        DispatchInterleave.Interleave(seed, cold)[0].Should().Be("X");
    }
}
