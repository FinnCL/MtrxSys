namespace MtrxSys.Core.Application.Abstractions;

// Estado de um telefone no registro compartilhado, lido em UMA consulta.
public enum SharedLedgerStatus
{
    None,        // não consta em nenhum ambiente → pode enviar.
    Sent,        // já enviado por outro chip (dedup — status 1).
    OptedOut,    // opt-out registrado (status 2) — suprimir sempre, todos os ambientes.
    Unavailable, // registro inacessível: não dá pra garantir — em Enforce, pausar e re-tentar.
}

// Registro compartilhado de telefones já disparados / em opt-out, comum aos 10 ambientes.
// Contrato: nenhuma implementação lança exceção de infraestrutura. GetStatusAsync devolve
// Unavailable em falha e o chamador decide — em Enforce PAUSA (fail-closed: nunca arrisca mandar
// pra quem deu SAIR); o dedup só é postergado, não furado. GetSuppressed/Mark* são fail-open/no-op.
public interface ISharedPhoneLedger
{
    // Mode != Off — quando false, tudo é no-op e o disparo se comporta como hoje.
    bool IsEnabled { get; }

    // Mode == Enforce — quando false (Observe), apenas loga o que faria, sem suprimir.
    bool IsEnforcing { get; }

    // Estado deste telefone (dedup + opt-out) em UMA query. Erro de infra → Unavailable.
    Task<SharedLedgerStatus> GetStatusAsync(string phoneE164, CancellationToken ct);

    // Versão em lote (pra filtro de público e lista de contatos): retorna os que constam.
    Task<IReadOnlySet<string>> GetSuppressedAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct);

    // Marca "enviado" (não rebaixa um opt-out já existente).
    Task MarkSentAsync(string phoneE164, CancellationToken ct);

    // Marca "opt-out" (sempre vence: vale pra todos os ambientes).
    Task MarkOptOutAsync(string phoneE164, CancellationToken ct);

    // Marca opt-out em LOTE (rede de segurança/backfill), numa única query e SEM churn: só escreve as
    // linhas que ainda não são opt-out (não reescreve as já em opt-out) e PRESERVA o chip de origem.
    Task MarkOptOutBatchAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct);
}
