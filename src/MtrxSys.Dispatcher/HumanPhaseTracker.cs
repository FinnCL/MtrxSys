namespace MtrxSys.Dispatcher;

/// <summary>Latch SINGLETON da Fase Humana. O DispatchEngine é Scoped (novo a cada ciclo), então não
/// guarda nada entre ciclos — este tracker lembra que a fase JÁ FECHOU pra um dado chip.
///
/// Existe por CUSTO, não por correção: a fase é função pura dos dados e monotônica (dias ativos e
/// conversas qualificadas só crescem), então recomputar sempre daria a mesma resposta — só que
/// custaria um group-by sobre chat_messages a cada job, pra sempre, muito depois da fase ter
/// acabado. Com o latch, o custo some assim que ela fecha.
///
/// Deliberadamente EM MEMÓRIA e não no banco: gravar "fase fechada" seria mais uma escrita em
/// system_state vinda do Dispatcher — a armadilha que o DispatchEngine documenta (a linha singleton
/// tem token xmin; um conflito com a Api/webhook revertia junto o MarkSent e a MESMA mensagem saía
/// duas vezes). Perder o latch num restart é inócuo: recomputa e dá o mesmo resultado.
///
/// Chaveado pela ÂNCORA do aquecimento: chip novo re-ancora (RestartWarmup) e o latch cai sozinho,
/// que é o desejado — chip novo refaz a fase.
///
/// Acesso é single-thread (o DispatchWorker roda um ciclo por vez), então não precisa de lock.</summary>
public sealed class HumanPhaseTracker
{
    private DateOnly? _closedForAnchor;

    /// <summary>A fase já fechou pra este chip? Só confia no latch se a âncora for a mesma.</summary>
    public bool IsClosedFor(DateOnly anchor) => _closedForAnchor == anchor;

    public void MarkClosedFor(DateOnly anchor) => _closedForAnchor = anchor;
}
