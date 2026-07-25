using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Core.UnitTests.Phone;

/// <summary>
/// Interpretação das shared_prefs do WhatsApp do emulador. Esta é a decisão que escolhe QUAL botão de
/// recuperação a aba Celular oferece, e errar tem custo real: em 2026-07-25 um chip saudável foi
/// registrado num aparelho cujo chip anterior tinha sido restringido, e morreu em 6h30 — porque
/// "Trocar chip" (pm clear) mantém `android_id`/GSF e o número novo herda a ficha do queimado. Quando o
/// estado é "revoked" a UI precisa empurrar pra "Limpar aparelho restringido" (imagem-ouro).
///
/// Os textos abaixo são recortes REAIS do dump colhido no emulador do A em 25/07.
/// </summary>
public sealed class WhatsAppAccountStateTests
{
    private const string Ok = WhatsAppAccountState.AdbSentinel;

    [Fact]
    public void Conta_viva_quando_ha_registration_jid()
    {
        var dump = $"{Ok}\n    <string name=\"registration_jid\">557191071879@s.whatsapp.net</string>";

        var st = WhatsAppAccountState.Parse(dump);

        st.State.Should().Be("registered");
        st.Phone.Should().Be("557191071879");
        st.RevokedByServer.Should().BeFalse();
    }

    [Fact]
    public void Revogada_pelo_servidor_quando_sobra_marca_de_logout_sem_jid()
    {
        // O caso do incidente: sem registration_jid, mas o app guarda de quem era a conta derrubada.
        var dump = $"{Ok}\n"
            + "    <string name=\"saved_user_before_logout\">557191071879</string>\n"
            + "    <boolean name=\"previously_logged_out_from_primary\" value=\"true\" />";

        var st = WhatsAppAccountState.Parse(dump);

        st.State.Should().Be("revoked");
        st.Phone.Should().Be("557191071879", "a UI mostra QUAL número caiu");
        st.RevokedByServer.Should().BeTrue();
    }

    [Fact]
    public void Revogada_mesmo_sem_o_numero_quando_a_flag_do_primario_esta_true()
    {
        // A flag sozinha basta pra saber que NÃO foi um pm clear local — só o número fica desconhecido.
        var dump = $"{Ok}\n    <boolean name=\"previously_logged_out_from_primary\" value=\"true\" />";

        var st = WhatsAppAccountState.Parse(dump);

        st.State.Should().Be("revoked");
        st.Phone.Should().BeNull();
    }

    [Fact]
    public void Flag_do_primario_em_false_nao_conta_como_revogacao()
    {
        // Regressão: um `Contains("previously_logged_out_from_primary")` ingênuo casaria aqui e acusaria
        // revogação num aparelho que só nunca registrou — empurrando o usuário a trocar o aparelho à toa.
        var dump = $"{Ok}\n    <boolean name=\"previously_logged_out_from_primary\" value=\"false\" />";

        WhatsAppAccountState.Parse(dump).State.Should().Be("none");
    }

    [Fact]
    public void Sem_conta_e_sem_marcas_e_aparelho_novo()
    {
        WhatsAppAccountState.Parse($"{Ok}\n").State.Should().Be("none");
    }

    [Fact]
    public void Jid_vence_as_marcas_de_logout_antigas()
    {
        // Registrar de novo NÃO apaga `saved_user_before_logout` (ela ficou lá do logout anterior). Se as
        // marcas vencessem, um chip recém-registrado apareceria como "derrubado" e a tela ofereceria
        // limpar o aparelho — jogando fora o número que acabou de entrar.
        var dump = $"{Ok}\n"
            + "    <string name=\"saved_user_before_logout\">557191071879</string>\n"
            + "    <boolean name=\"previously_logged_out_from_primary\" value=\"true\" />\n"
            + "    <string name=\"registration_jid\">557191176942@s.whatsapp.net</string>";

        var st = WhatsAppAccountState.Parse(dump);

        st.State.Should().Be("registered");
        st.Phone.Should().Be("557191176942");
    }

    [Theory]
    [InlineData("")]                                    // adb mudo / container fora
    [InlineData(null)]                                  // falha na execução
    [InlineData("error: device 'emulator-5554' not found")]
    [InlineData("<string name=\"registration_jid\">557191071879@s.whatsapp.net</string>")] // saída sem sentinela
    public void Sem_a_sentinela_o_estado_e_unknown(string? dump)
    {
        // FAIL-SAFE: "não consegui perguntar" nunca pode virar "não tem conta". Sem isto, um adb ocupado
        // durante o disparo faria a tela anunciar que o chip caiu — e, pior, o GetWhatsAppNumberAsync
        // (que delega pra cá) devolveria vazio, o que o dispatcher lê como TROCA DE CHIP e reiniciaria o
        // aquecimento sozinho. O último caso cobre justamente uma saída "plausível" mas não confirmada.
        WhatsAppAccountState.Parse(dump).State.Should().Be("unknown");
        WhatsAppAccountState.Parse(dump).RevokedByServer.Should().BeFalse();
    }
}
