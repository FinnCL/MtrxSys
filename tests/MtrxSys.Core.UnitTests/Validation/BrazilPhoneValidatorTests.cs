using FluentAssertions;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.UnitTests.Validation;

public sealed class BrazilPhoneValidatorTests
{
    private readonly BrazilPhoneValidator _validator = new();

    [Theory]
    [InlineData("+5511987654321")]          // já em E.164
    [InlineData("11987654321")]             // nacional, DDD + 9 dígitos
    [InlineData("(11) 98765-4321")]         // com formatação humana
    [InlineData(" +55 11 98765 4321 ")]     // com espaços
    public void Validate_accepts_and_normalizes_valid_mobile(string raw)
    {
        var result = _validator.Validate(raw);

        result.IsSuccess.Should().BeTrue($"'{raw}' deveria ser válido");
        result.Value!.E164.Should().Be("+5511987654321");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("+")]
    [InlineData("11")]                       // curto demais
    [InlineData("+5500987654321")]           // DDD 00 inexistente
    public void Validate_rejects_invalid_input(string raw)
    {
        var result = _validator.Validate(raw);

        result.IsFailure.Should().BeTrue($"'{raw}' deveria ser inválido");
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NormalizeTrusted_returns_normalized_when_valid()
    {
        var phone = _validator.NormalizeTrusted("+55 11 98765-4321");

        phone.E164.Should().Be("+5511987654321");
    }

    [Fact]
    public void NormalizeTrusted_keeps_original_when_lib_rejects()
    {
        // Número legado/atípico que a lib não valida: não descartamos, mantemos como veio.
        const string legacy = "+5511887654321";

        var phone = _validator.NormalizeTrusted(legacy);

        phone.E164.Should().Be(legacy);
    }

    // Números REAIS de um grupo de produção (DDD 71), no formato ANTIGO: 8 dígitos depois do DDD,
    // sem o 9º. É assim que o WhatsApp os devolve — inclusive o número do nosso próprio chip.
    [Theory]
    [InlineData("+557182368724")]
    [InlineData("+557191072835")]
    [InlineData("+557193836443")]
    public void IsPlausibleBrazilian_aceita_numero_legado_que_o_Validate_rejeita(string legacy)
    {
        // A premissa do teste: a lib REJEITA esses números (não os corrige inserindo o 9º dígito).
        // Se um dia isso mudar, este assert avisa em vez de o teste passar mentindo.
        _validator.Validate(legacy).IsSuccess.Should().BeFalse();

        _validator.IsPlausibleBrazilian(legacy).Should().BeTrue(
            "é brasileiro sim — só é antigo. Decidir isso pelo Validate rotulava um grupo inteiro de "
            + "conhecidos como 'não brasileiro' e importava ZERO contatos");
    }

    [Theory]
    [InlineData("+13475551234")]   // EUA
    [InlineData("+351912345678")]  // Portugal
    public void IsPlausibleBrazilian_recusa_estrangeiro(string foreign) =>
        _validator.IsPlausibleBrazilian(foreign).Should().BeFalse();

    [Fact]
    public void IsPlausibleBrazilian_aceita_numero_br_valido() =>
        _validator.IsPlausibleBrazilian("+5511987654321").Should().BeTrue();

    // O contrapeso do teste acima: aceitar o legado NÃO pode abrir a porta pra lixo. Contato-lixo
    // vira disparo pra número inexistente = 463 = gatilho de ban. "+551100" tem código 55 e passaria
    // se o corte fosse só o país — foi a regressão que os testes do Coletor pegaram.
    [Theory]
    [InlineData("+551100")]
    [InlineData("+5571")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void IsPlausibleBrazilian_recusa_lixo(string junk) =>
        _validator.IsPlausibleBrazilian(junk).Should().BeFalse();
}
