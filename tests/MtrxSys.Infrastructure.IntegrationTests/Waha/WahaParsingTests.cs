using System.Text.Json;
using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>Testes unitários do parsing PURO extraído do WahaClient na refatoração por
/// responsabilidade. Cobre o miolo mais sutil (JID↔telefone, LID, mapeamento tolerante às engines
/// NOWEB/WEBJS, status e código de convite) que antes só tinha cobertura indireta.</summary>
public sealed class WahaParsingTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("5511921404487@c.us", "+5511921404487")]
    [InlineData("5511999999999@g.us", "+5511999999999")]
    [InlineData("5571993798077", "+5571993798077")] // sem @: usa a string toda, só dígitos
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("@c.us", null)]      // sem dígitos
    [InlineData("abc@c.us", null)]   // sem dígitos
    public void PhoneFromChatId_extrai_e164_ou_null(string? input, string? expected)
    {
        WahaParsing.PhoneFromChatId(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("+5511999999999", "+5511999999999")]
    [InlineData("+0", null)]      // pseudo-id sem dígito significativo
    [InlineData("+000", null)]
    [InlineData(null, null)]
    public void RealPhoneOrNull_descarta_pseudo_numeros(string? input, string? expected)
    {
        WahaParsing.RealPhoneOrNull(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("WORKING", WahaSessionStatus.Working)]
    [InlineData("working", WahaSessionStatus.Working)]
    [InlineData("SCAN_QR_CODE", WahaSessionStatus.ScanQrCode)]
    [InlineData("STARTING", WahaSessionStatus.Starting)]
    [InlineData("STOPPED", WahaSessionStatus.Stopped)]
    [InlineData("FAILED", WahaSessionStatus.Failed)]
    [InlineData("WHATEVER", WahaSessionStatus.Unknown)]
    [InlineData(null, WahaSessionStatus.Unknown)]
    public void ParseStatus_mapeia_todas_as_engines(string? raw, WahaSessionStatus expected)
    {
        WahaParsing.ParseStatus(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("120363041234567890", "120363041234567890@g.us")]
    [InlineData("120363041234567890@g.us", "120363041234567890@g.us")] // já tem JID, não duplica
    public void EnsureGroupJid_garante_sufixo_uma_vez(string input, string expected)
    {
        WahaParsing.EnsureGroupJid(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://chat.whatsapp.com/ABC123def456", "ABC123def456")]
    [InlineData("ABC123def456", "ABC123def456")]
    [InlineData("  ABC123  ", "ABC123")]
    public void ExtractInviteCode_tira_so_o_codigo(string input, string expected)
    {
        WahaParsing.ExtractInviteCode(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("120363041234567890@g.us", true)]
    [InlineData("120363041234567890", true)]   // só dígitos, >= 15
    [InlineData("ABC123def", false)]            // código de convite (tem letras)
    [InlineData("12345", false)]                // curto demais
    [InlineData(null, false)]
    public void LooksLikeGroupJid_distingue_jid_de_codigo(string? id, bool expected)
    {
        WahaParsing.LooksLikeGroupJid(id).Should().Be(expected);
    }

    [Fact]
    public void PhoneFromParticipant_noweb_usa_phoneNumber()
    {
        var p = El("""{"phoneNumber":"5511999999999@c.us","id":"99999@lid"}""");
        WahaParsing.PhoneFromParticipant(p).Should().Be("+5511999999999");
    }

    [Fact]
    public void PhoneFromParticipant_id_lid_string_vira_null()
    {
        var p = El("""{"id":"123456@lid"}""");
        WahaParsing.PhoneFromParticipant(p).Should().BeNull("participante só-LID não tem número real");
    }

    [Fact]
    public void PhoneFromParticipant_webjs_objeto_user_server()
    {
        var p = El("""{"id":{"user":"5511999999999","server":"c.us"}}""");
        WahaParsing.PhoneFromParticipant(p).Should().Be("+5511999999999");
    }

    [Fact]
    public void PhoneFromParticipant_webjs_server_lid_vira_null()
    {
        var p = El("""{"id":{"user":"123456","server":"lid"}}""");
        WahaParsing.PhoneFromParticipant(p).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"admin":"admin"}""", true)]
    [InlineData("""{"admin":"superadmin"}""", true)]
    [InlineData("""{"role":"ADMIN"}""", true)]
    [InlineData("""{"role":"SUPERADMIN"}""", true)]
    [InlineData("""{"admin":null,"role":"member"}""", false)]
    [InlineData("""{}""", false)]
    public void IsAdminRole_tolerante_as_engines(string json, bool expected)
    {
        WahaParsing.IsAdminRole(El(json)).Should().Be(expected);
    }

    [Fact]
    public void MapNowebGroup_extrai_numero_nome_e_contagem()
    {
        var g = El("""{"id":"120363041234567890@g.us","subject":"Meu Grupo","participants":[{"id":"a"},{"id":"b"}]}""");
        var result = WahaParsing.MapNowebGroup(g);
        result.Id.Should().Be("120363041234567890");
        result.Name.Should().Be("Meu Grupo");
        result.ParticipantsCount.Should().Be(2);
    }

    [Fact]
    public void MapNowebGroup_sem_participants_conta_null()
    {
        var g = El("""{"id":"120363041234567890@g.us","subject":"X"}""");
        WahaParsing.MapNowebGroup(g).ParticipantsCount.Should().BeNull();
    }
}
