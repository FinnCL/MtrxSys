namespace MtrxSys.Api.Options;

/// <summary>Config do endpoint anônimo /api/presence/chip. MaskChipPhone=true mascara o número na
/// resposta (mantém DDI+DDD e os 4 últimos dígitos) — endurecimento pra deploys onde a landing/API
/// fica exposta além da rede do operador. Default false: mostra o número cheio (comportamento atual,
/// pra a feature de "mesmo número em dois cards" da landing). Ver docs/architecture.md.</summary>
public sealed class PresenceOptions
{
    public const string SectionName = "Presence";

    public bool MaskChipPhone { get; init; }

    /// <summary>Mascara um E.164 (+557193804159 → +5571****4159): preserva DDI+DDD e os 4 últimos
    /// dígitos, esconde o miolo. Números curtos demais pra mascarar com segurança viram "+•••" (não
    /// vaza nada). null/vazio passa direto.</summary>
    public static string? Mask(string? phoneE164)
    {
        if (string.IsNullOrWhiteSpace(phoneE164))
        {
            return phoneE164;
        }
        var plus = phoneE164.StartsWith('+');
        var digits = new string(phoneE164.Where(char.IsDigit).ToArray());
        // Precisa de prefixo (DDI+DDD ≈ 4) + sufixo (4) sem se sobrepor: < 9 dígitos não dá pra
        // mascarar mantendo utilidade sem revelar quase tudo → esconde por completo.
        if (digits.Length < 9)
        {
            return "+•••";
        }
        var prefix = digits[..4];
        var suffix = digits[^4..];
        return (plus ? "+" : "") + prefix + "****" + suffix;
    }
}
