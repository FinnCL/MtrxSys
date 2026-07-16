namespace MtrxSys.Core.Safety;

/// <summary>Desde quando a sessão WAHA está CONTINUAMENTE em WORKING, no processo da API — o análogo
/// do <c>DispatchSettleTracker</c> (que vive no processo do disparo). Serve pro envio MANUAL respeitar
/// a MESMA janela de reassentamento: "reconectou, espere antes de mandar".
///
/// Por que existe: enviar logo após parear um companion RECÉM-LINKADO é o que faz o WhatsApp remover o
/// device (<c>conflict/device_removed</c>) e aplicar <c>reachout timelock</c> — visto em produção
/// (2026-07-16, chip A: "oi" 22s após parear → restrição de 7 dias). O disparo já esperava; o envio
/// manual pelo Chat NÃO — este tracker fecha esse buraco.
///
/// Atualizado pelo <c>SessionHealthWatchService</c> (que já poleia o status a cada tick); lido pelos
/// caminhos de envio manual. Acesso concorrente (background + requests), por isso o lock.
/// Como o DispatchSettleTracker: reiniciar a API zera a contagem (o 1º WORKING observado recomeça a
/// janela) — conservador de propósito, igual ao disparo.</summary>
public sealed class SessionReadinessTracker
{
    private readonly object gate = new();
    private DateTimeOffset? workingSince;

    /// <summary>Sessão observada em WORKING: marca o início da continuidade. NÃO sobrescreve — preserva
    /// o 1º WORKING, pra a janela contar desde a (re)conexão, não desde o último tick.</summary>
    public void MarkWorking(DateTimeOffset now)
    {
        lock (gate)
        {
            workingSince ??= now;
        }
    }

    /// <summary>Sessão observada FORA de WORKING: zera, pra o próximo WORKING recomeçar a contagem.</summary>
    public void MarkNotWorking()
    {
        lock (gate)
        {
            workingSince = null;
        }
    }

    /// <summary>Há quanto tempo a sessão está WORKING contínuo; <c>null</c> se não está (ou ainda não
    /// foi observada em WORKING — ex.: logo após um restart da API). O chamador trata null como
    /// "não assentou ainda" (fail-safe).</summary>
    public TimeSpan? WorkingFor(DateTimeOffset now)
    {
        lock (gate)
        {
            return workingSince is { } since ? now - since : null;
        }
    }
}
