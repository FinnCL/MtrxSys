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

    // ── O contrato de que o console do aparelho depende ───────────────────────────────────────────
    // O `ParseContato` (PhoneConsoleCommand) monta "+" + dígitos e pergunta AQUI. Antes ele conferia
    // só o COMPRIMENTO (12 ou 13), e comprimento certo não é número certo: "+5537368544314" tem 13
    // dígitos e DDD 37, que existe, mas o dígito depois do DDD não é 9. Um caso da mesma forma (um
    // número da Moldávia) passou por checagem de comprimento em 2026-07-27 e chegou à fila de disparo.
    // Estes casos existem pra que ninguém "simplifique" a validação de volta pra um Length.
    [Theory]
    [InlineData("+5571993836443", true)]   // 13 dígitos, celular moderno com o 9
    [InlineData("+557199383644", true)]    // 12 dígitos, legado sem o 9 — o caso NORMAL na base fria
    [InlineData("+5537368544314", false)]  // 13 dígitos, DDD real, mas sem o 9 no lugar certo
    [InlineData("+9999999999999", false)]  // 13 dígitos e nada mais
    [InlineData("+5500993836443", false)]  // DDD 00 não existe
    [InlineData("+1234567890123", false)]  // 13 dígitos sem o 55 na frente
    public void IsPlausibleBrazilian_e_o_corte_que_o_console_usa(string e164, bool esperado) =>
        _validator.IsPlausibleBrazilian(e164).Should().Be(
            esperado,
            $"'{e164}' é o formato que o console do aparelho cola e valida antes de gravar na agenda");

    // ── A outra forma do mesmo celular ────────────────────────────────────────────────────────────
    // O WhatsApp guarda a conta ora com o 9º dígito, ora sem, conforme a época do registro. Abrir a
    // conversa pela forma errada faz o app dizer "não tem WhatsApp", e um contato BOM parece morto.
    // Serve como SEGUNDA tentativa, nunca pra normalizar na entrada: em 2026-08-05, no mesmo DDD 84,
    // um número de 12 dígitos entregou e outro falhou — converter tudo quebraria o que funciona.
    [Theory]
    [InlineData("558498420730", "5584998420730")]   // 12 -> 13, ganha o 9
    [InlineData("5584998420730", "558498420730")]   // 13 -> 12, perde o 9
    [InlineData("+55 84 9842-0730", "5584998420730")] // formatado: só os dígitos importam
    public void AlternateBrazilianForm_troca_o_nono_digito(string entrada, string esperado) =>
        BrazilPhoneValidator.AlternateBrazilianForm(entrada).Should().Be(esperado);

    [Theory]
    [InlineData("558432104567")]   // fixo (assinante começa em 3): inserir 9 inventaria um número
    [InlineData("5500998420730")]  // DDD 00 não existe
    [InlineData("+351912345678")]  // estrangeiro
    [InlineData("5584")]           // curto demais
    [InlineData("")]
    public void AlternateBrazilianForm_devolve_null_quando_nao_ha_alternativa(string entrada) =>
        BrazilPhoneValidator.AlternateBrazilianForm(entrada).Should().BeNull();

    [Fact]
    public void AlternateBrazilianForm_e_reversivel() =>
        BrazilPhoneValidator.AlternateBrazilianForm(
            BrazilPhoneValidator.AlternateBrazilianForm("558498420730"))
            .Should().Be("558498420730", "ida e volta tem que devolver o original");

    // O bug que este teste guarda: colar os dígitos atrás de um "+" mandava número em formato
    // NACIONAL pro país errado, EM SILÊNCIO. "71991072835" (o jeito que sai de qualquer planilha)
    // virava "+71991072835" — e +7 é RÚSSIA, com 11 dígitos batendo o comprimento de lá. Esse número
    // ia direto pro POST /groups da WAHA como participante inexistente, que é o vetor do 463.
    [Theory]
    [InlineData("71991072835", "+5571991072835")]
    [InlineData("(71) 99107-2835", "+5571991072835")]
    [InlineData("71 99107 2835", "+5571991072835")]
    public void NormalizeTypedE164_assume_BR_quando_vem_em_formato_nacional(string typed, string expected) =>
        BrazilPhoneValidator.NormalizeTypedE164(typed).Should().Be(expected);

    [Theory]
    [InlineData("+5571991072835", "+5571991072835")]
    [InlineData("+55 71 99107-2835", "+5571991072835")]
    [InlineData("+351912345678", "+351912345678")]  // estrangeiro legítimo: preservado
    public void NormalizeTypedE164_respeita_o_pais_quando_vem_com_mais(string typed, string expected) =>
        BrazilPhoneValidator.NormalizeTypedE164(typed).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("+000000000")]
    [InlineData("1")]
    public void NormalizeTypedE164_recusa_lixo(string junk) =>
        BrazilPhoneValidator.NormalizeTypedE164(junk).Should().BeNull();
}
