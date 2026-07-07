namespace MtrxSys.Dispatcher;

/// <summary>Estado SINGLETON do "reassentar após reconectar". O DispatchEngine é Scoped (novo a cada
/// ciclo), então não guarda estado entre ciclos — este tracker guarda desde quando a sessão está
/// CONTINUAMENTE em WORKING. Quando o chip volta a WORKING (ou o dispatcher reinicia), o disparo espera
/// uma janela antes de voltar a enviar — evita "reconectar e já sair metralhando" (risco de ban).
/// Acesso é single-thread (o DispatchWorker roda um ciclo por vez), então não precisa de lock.</summary>
public sealed class DispatchSettleTracker
{
    private DateTimeOffset? _workingSince;

    // Sessão está WORKING agora: marca o início da continuidade (se ainda não marcado) e diz se ainda
    // está DENTRO da janela de reassentamento (true = não deve enviar ainda).
    public bool IsSettling(DateTimeOffset now, TimeSpan window)
    {
        _workingSince ??= now;
        return now - _workingSince.Value < window;
    }

    // Sessão NÃO está WORKING: zera, pra o próximo WORKING recomeçar a contagem do reassentamento.
    public void Reset() => _workingSince = null;
}
