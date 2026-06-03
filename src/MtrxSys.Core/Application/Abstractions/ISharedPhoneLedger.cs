namespace MtrxSys.Core.Application.Abstractions;

// Registro compartilhado de telefones já disparados / em opt-out, comum aos 10 ambientes.
// Contrato FAIL-OPEN: nenhuma implementação pode lançar exceção de infraestrutura — qualquer
// falha (registro fora do ar etc.) vira "não suprime" / no-op, pra NUNCA travar o disparo.
public interface ISharedPhoneLedger
{
    // Mode != Off — quando false, tudo é no-op e o disparo se comporta como hoje.
    bool IsEnabled { get; }

    // Mode == Enforce — quando false (Observe), apenas loga o que faria, sem suprimir.
    bool IsEnforcing { get; }

    // Este telefone já consta como enviado/opt-out em QUALQUER ambiente?
    Task<bool> IsSuppressedAsync(string phoneE164, CancellationToken ct);

    // Versão em lote (pra filtro de público e lista de contatos): retorna os que constam.
    Task<IReadOnlySet<string>> GetSuppressedAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct);

    // Marca "enviado" (não rebaixa um opt-out já existente).
    Task MarkSentAsync(string phoneE164, CancellationToken ct);

    // Marca "opt-out" (sempre vence: vale pra todos os ambientes).
    Task MarkOptOutAsync(string phoneE164, CancellationToken ct);
}
