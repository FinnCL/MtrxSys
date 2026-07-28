using FluentAssertions;
using MtrxSys.Core.Validation;
using Xunit;

namespace MtrxSys.Core.UnitTests.Validation;

/// <summary>
/// Trava o FURO que deixou um número da MOLDÁVIA entrar na base como se fosse brasileiro.
///
/// <para>🔴 MEDIDO em produção 2026-07-27: <c>37368544314</c> foi importado de um grupo, virou contato
/// ativo, ganhou contato na agenda Google e chegou a ser enfileirado pro disparo. Só não recebeu porque
/// alguém olhou a lista à mão. Prefixo 373 é Moldávia.</para>
///
/// <para>A causa é sutil e vale entender antes de mexer: <c>IsPlausibleBrazilian</c> parseia com a região
/// BR como padrão. Um número SEM "+" é tratado como número NACIONAL brasileiro, então a libphonenumber
/// devolve <c>CountryCode == 55</c> por construção — nunca por evidência. E 11 dígitos é comprimento
/// plausível de celular BR, então <c>IsPossibleNumber</c> também aprova. Os dois checks passam e o
/// número estrangeiro entra.</para>
/// </summary>
public sealed class PadraoNumeroTests
{
    private static readonly BrazilPhoneValidator Validator = new();

    [Theory]
    [InlineData("+5511972467559")] // celular SP com 9º dígito
    [InlineData("+557184609253")]  // legado BA sem o 9º dígito (o caso NORMAL aqui)
    [InlineData("5511972467559")]  // mesmo número, sem o "+" (como o aparelho devolve)
    public void Numero_brasileiro_e_aceito(string raw) =>
        Validator.IsPlausibleBrazilian(raw).Should().BeTrue();

    [Theory]
    [InlineData("37368544314")]   // Moldávia SEM "+" — o caso real que entrou na base
    [InlineData("+37368544314")]  // o MESMO número, com "+" — este a validação já rejeita
    public void Numero_estrangeiro_e_recusado(string raw) =>
        Validator.IsPlausibleBrazilian(raw).Should().BeFalse(
            "número de outro país não é público desta operação e disparar pra ele é envio inútil");
}
