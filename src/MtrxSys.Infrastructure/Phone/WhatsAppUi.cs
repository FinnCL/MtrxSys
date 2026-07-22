namespace MtrxSys.Infrastructure.Phone;

/// <summary>Helpers do envio pela UI do WhatsApp no emulador (Caminho A anti-463). Compartilhado
/// pelos orquestradores docker-android e redroid.</summary>
internal static class WhatsAppUi
{
    /// <summary>Deep link click-to-chat que abre a conversa com o texto JÁ PREENCHIDO no campo de
    /// mensagem — pra número salvo OU não. O texto é URL-encoded (emoji/quebra-de-linha/link/'/&amp;
    /// viram %XX), então não sobra aspa simples: seguro pra passar entre aspas simples ao adb shell.
    /// Ex.: whatsapp://send?phone=5511999998888&amp;text=Ol%C3%A1%20%F0%9F%91%8B</summary>
    public static string DeepLink(string digits, string text)
        => $"whatsapp://send?phone={digits}&text={Uri.EscapeDataString(text ?? string.Empty)}";
}
