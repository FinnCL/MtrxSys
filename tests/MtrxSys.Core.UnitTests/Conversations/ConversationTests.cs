using System.Text;
using FluentAssertions;
using MtrxSys.Core.Domain.Conversations;
using Xunit;

namespace MtrxSys.Core.UnitTests.Conversations;

public sealed class ConversationTests
{
    // Regressão: TouchLastMessage cortava a prévia em 280 chars; um emoji (par surrogate) na
    // borda virava high surrogate solto, e o EF/Npgsql estourava EncoderFallbackException ao
    // gravar em UTF-8 no Postgres. A truncagem agora preserva o par (ver StringExtensionsTests).
    [Fact]
    public void TouchLastMessage_truncating_on_an_emoji_boundary_keeps_valid_utf8()
    {
        // 🏆 ocupa exatamente os índices 278 (high) e 279 (low) — o ponto de corte.
        var preview = new string('a', 278) + "🏆" + new string('b', 50);
        var conv = Conversation.Create(
            id: Guid.NewGuid(),
            waChatId: "120363@g.us",
            contactId: null,
            title: "grupo",
            isGroup: true,
            createdAt: DateTimeOffset.UnixEpoch);

        conv.TouchLastMessage(DateTimeOffset.UnixEpoch.AddMinutes(1), preview);

        var stored = conv.LastMessagePreview!;
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var act = () => strictUtf8.GetBytes(stored);

        act.Should().NotThrow();
        stored.Length.Should().BeLessThanOrEqualTo(280);
    }
}
