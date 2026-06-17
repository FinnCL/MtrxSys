namespace MtrxSys.Core.Application.Abstractions;

/// <summary>
/// Valida um convite de grupo do WhatsApp pela PÁGINA PÚBLICA (chat.whatsapp.com/&lt;code&gt;), sem
/// precisar do WAHA nem da sessão conectada: convite vivo traz o nome do grupo (og:title); morto/
/// revogado/expirado vem vazio. Rápido e paralelizável — destrava o gargalo da validação via WAHA.
/// </summary>
public interface IWhatsAppInviteValidator
{
    /// <summary>Checa o convite: <see cref="InviteCheck.Alive"/> + nome quando vivo; Alive=false
    /// quando morto. Retorna null em erro transitório (rede/bloqueio) — deixa pra re-tentar depois.</summary>
    Task<InviteCheck?> CheckAsync(string inviteCode, CancellationToken ct);
}

/// <summary>Resultado da checagem de um convite. Name só vem preenchido quando Alive.</summary>
public sealed record InviteCheck(bool Alive, string? Name);
