using FluentAssertions;
using MtrxSys.Core.Domain.Funnel;
using Xunit;

namespace MtrxSys.Core.UnitTests.Funnel;

public sealed class WaMeLinkTests
{
    [Fact]
    public void Monta_link_com_digitos_e_texto_encodado()
    {
        var link = WaMeLink.Build("+55 71 99118-3209", "oi, tudo bem?");
        link.Should().Be("https://wa.me/5571991183209?text=oi%2C%20tudo%20bem%3F");
    }

    [Fact]
    public void Sem_texto_gera_link_simples()
    {
        WaMeLink.Build("5571991183209", null).Should().Be("https://wa.me/5571991183209");
    }

    [Fact]
    public void Aceita_c_us_e_lid_extraindo_so_digitos()
    {
        WaMeLink.Build("557185211291@c.us", null).Should().Be("https://wa.me/557185211291");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("@c.us")]
    public void Sem_digitos_devolve_null(string? phone)
    {
        WaMeLink.Build(phone, "oi").Should().BeNull();
    }
}

public sealed class FunnelInviteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MarkEngaged_e_idempotente_so_o_1o_dispara()
    {
        var invite = FunnelInvite.Create(Guid.NewGuid(), Guid.NewGuid(), "oi", "obrigado!", T0);
        invite.MarkEngaged(T0.AddMinutes(1)).Should().BeTrue("1º inbound");
        invite.MarkEngaged(T0.AddMinutes(2)).Should().BeFalse("já engajou — não redispara");
        invite.EngagedAt.Should().Be(T0.AddMinutes(1), "preserva a data original");
    }

    [Fact]
    public void HasPendingAutoReply_so_com_texto_e_ainda_nao_enviada()
    {
        var comTexto = FunnelInvite.Create(Guid.NewGuid(), Guid.NewGuid(), "oi", "obrigado!", T0);
        comTexto.HasPendingAutoReply.Should().BeTrue();
        comTexto.MarkAutoReplied(T0.AddMinutes(1));
        comTexto.HasPendingAutoReply.Should().BeFalse("já respondeu");

        var semTexto = FunnelInvite.Create(Guid.NewGuid(), Guid.NewGuid(), "oi", null, T0);
        semTexto.HasPendingAutoReply.Should().BeFalse("sem auto-resposta configurada");
    }

    [Fact]
    public void MarkAutoReplied_preserva_a_1a_data()
    {
        var invite = FunnelInvite.Create(Guid.NewGuid(), Guid.NewGuid(), "oi", "obrigado!", T0);
        invite.MarkAutoReplied(T0.AddMinutes(1));
        invite.MarkAutoReplied(T0.AddMinutes(5));
        invite.AutoRepliedAt.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public void UpdateContent_atualiza_convite_aberto()
    {
        var invite = FunnelInvite.Create(Guid.NewGuid(), Guid.NewGuid(), "oi", "obrigado", T0);
        invite.UpdateContent("texto novo", "resposta nova");
        invite.PrefillText.Should().Be("texto novo");
        invite.AutoReplyText.Should().Be("resposta nova");
    }

    [Fact]
    public void UpdateContent_e_noop_depois_de_engajado()
    {
        var invite = FunnelInvite.Create(Guid.NewGuid(), Guid.NewGuid(), "oi", "obrigado", T0);
        invite.MarkEngaged(T0.AddMinutes(1));
        invite.UpdateContent("mudou", "mudou");
        invite.PrefillText.Should().Be("oi", "convite já engajado não muda mais o texto");
        invite.AutoReplyText.Should().Be("obrigado");
    }
}
