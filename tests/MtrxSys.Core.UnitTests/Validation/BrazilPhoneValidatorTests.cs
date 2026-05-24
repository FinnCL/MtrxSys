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
}
