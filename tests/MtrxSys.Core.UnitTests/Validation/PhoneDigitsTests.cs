using FluentAssertions;
using MtrxSys.Core.Validation;
using Xunit;

namespace MtrxSys.Core.UnitTests.Validation;

/// <summary>
/// Trava a IDENTIDADE de um telefone: só os dígitos.
///
/// <para>🔴 Comparar telefone por TEXTO gerou 50 contatos duplicados na produção em 2026-07-27 — o
/// mesmo grupo importado por dois caminhos, um gravando "+5588…" e outro "5588…". O índice único do
/// banco (IX_contacts_phone_digits) usa esta mesma forma; se o código divergir dele, a comparação em
/// memória diz uma coisa e o banco outra.</para>
/// </summary>
public sealed class PhoneDigitsTests
{
    [Theory]
    [InlineData("+5511972467559", "5511972467559")]
    [InlineData("5511972467559", "5511972467559")]
    [InlineData("+55 (11) 97246-7559", "5511972467559")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Extrai_so_os_digitos(string? raw, string esperado) =>
        PhoneDigits.Of(raw).Should().Be(esperado);

    [Theory]
    [InlineData("+5588945283 54", "558894528354")]   // com espaço no meio
    [InlineData("+558894528354", "558894528354")]    // o par real que duplicou em 27/07
    public void Mesmo_numero_em_formatos_diferentes_e_o_mesmo(string a, string b) =>
        PhoneDigits.Same(a, b).Should().BeTrue();

    [Fact]
    public void Numeros_diferentes_nao_colidem() =>
        PhoneDigits.Same("+5511972467559", "+5511972467558").Should().BeFalse();

    [Theory]
    [InlineData(null, "+5511972467559")]
    [InlineData("", "+5511972467559")]
    [InlineData("sem digitos", "+5511972467559")]
    public void Vazio_nunca_e_igual_a_ninguem(string? a, string b) =>
        PhoneDigits.Same(a, b).Should().BeFalse(
            "senão um telefone em branco casaria com o primeiro contato da lista");
}
