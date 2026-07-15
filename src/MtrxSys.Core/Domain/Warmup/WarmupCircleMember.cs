using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Warmup;

/// <summary>Uma pessoa do "círculo de aquecimento" — a lista de checagem da Fase Humana, montada a
/// partir da agenda do aparelho.
///
/// DE PROPÓSITO NÃO É UM <c>Contact</c>. Contact é do domínio de DISPARO e carrega dedup, ledger
/// compartilhado, Stage e LastSentAt; pôr essa gente lá exigiria um flag de isenção que furaria
/// justamente as travas que impedem reenvio acidental nos 10 ambientes. Aqui não há nada disso:
/// o círculo é só um marcador, e a conversa acontece pela aba Chat, que não toca em nenhuma trava.
///
/// SOBREVIVE À TROCA DE CHIP (RestartWarmup não mexe nisto): as pessoas são do OPERADOR, não do
/// chip — a mesma gente aquece o próximo. O que reseta é o PROGRESSO, porque é medido desde a
/// âncora do aquecimento.</summary>
public sealed class WarmupCircleMember : Entity<Guid>
{
    public string PhoneE164 { get; private set; } = string.Empty;
    public string? Name { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    private WarmupCircleMember() { }

    public static WarmupCircleMember Create(Guid id, string phoneE164, string? name, DateTimeOffset addedAt)
    {
        return new WarmupCircleMember
        {
            Id = id,
            PhoneE164 = phoneE164,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            AddedAt = addedAt,
        };
    }

    public void Rename(string? name) => Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
