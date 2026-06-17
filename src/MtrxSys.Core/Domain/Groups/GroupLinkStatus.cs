namespace MtrxSys.Core.Domain.Groups;

/// <summary>Ciclo de vida de um link de grupo captado.
/// Found → (resolve via WAHA join-info) → Resolved | Invalid → (entrada manual) Joined → Imported.</summary>
public enum GroupLinkStatus
{
    /// <summary>Link cru recém-captado do Telegram; ainda não consultado no WhatsApp.</summary>
    Found = 0,
    /// <summary>Convite válido: nome/tamanho do grupo já lidos via join-info (sem entrar).</summary>
    Resolved = 1,
    /// <summary>Convite morto/expirado/cheio (join-info recusou).</summary>
    Invalid = 2,
    /// <summary>O número conectado entrou no grupo (clique manual no Coletor).</summary>
    Joined = 3,
    /// <summary>Participantes já importados como contatos.</summary>
    Imported = 4,
    /// <summary>Convite válido, mas o grupo aparenta NÃO ser brasileiro (nome em alfabeto
    /// estrangeiro). Escondido por padrão quando o filtro "só brasileiros" está ligado.</summary>
    Foreign = 5,
}
