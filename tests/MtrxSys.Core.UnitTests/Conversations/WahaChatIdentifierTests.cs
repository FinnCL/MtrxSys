using FluentAssertions;
using MtrxSys.Core.Domain.Conversations;
using Xunit;

namespace MtrxSys.Core.UnitTests.Conversations;

public sealed class WahaChatIdentifierTests
{
    [Theory]
    // Eco de saída traz sufixo "_out" — o core é o token logo após o chatId, não o último.
    [InlineData("true_157239574847645@lid_3EB0EA0B1062A059AE3C9E_out", "3EB0EA0B1062A059AE3C9E")]
    // Inbound: core é o token após o segmento com '@'.
    [InlineData("false_91672436301905@lid_A5C75EF631CE7", "A5C75EF631CE7")]
    [InlineData("true_557186576422@c.us_3EB0FDFBD5E4B4D74489FE", "3EB0FDFBD5E4B4D74489FE")]
    [InlineData("false_120363427847675650@g.us_501122571779", "501122571779")]
    // Id já "core" (retornado pelo envio do WAHA) volta inalterado.
    [InlineData("3EB0EA0B1062A059AE3C9E", "3EB0EA0B1062A059AE3C9E")]
    [InlineData("", "")]
    public void ExtractMessageCore_returns_stable_core(string input, string expected) =>
        WahaChatIdentifier.ExtractMessageCore(input).Should().Be(expected);

    [Fact]
    public void ExtractMessageCore_send_and_echo_share_the_same_core()
    {
        // O disparo guarda o id retornado pelo envio (@c.us); o eco chega serializado por @lid.
        // O de-dupe só funciona se ambos reduzirem ao mesmo core.
        const string fromSend = "3EB0EA0B1062A059AE3C9E";
        const string fromEcho = "true_157239574847645@lid_3EB0EA0B1062A059AE3C9E_out";

        WahaChatIdentifier.ExtractMessageCore(fromSend)
            .Should().Be(WahaChatIdentifier.ExtractMessageCore(fromEcho));
    }
}
