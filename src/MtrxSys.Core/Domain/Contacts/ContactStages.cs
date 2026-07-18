namespace MtrxSys.Core.Domain.Contacts;

/// <summary>Fonte ÚNICA da regra "engajado": quem RESPONDEU/AVANÇOU. "Novo" (Lead) e "Descartado" (Lost)
/// NÃO engajaram; todo o resto (Respondeu/Negociando/Cliente) sim. Usada no filtro de disparo
/// (EngagedOnly), no reset diário do aquecimento e no selo "Respondeu" do relatório — pra a definição
/// não divergir entre os três lugares.</summary>
public static class ContactStages
{
    /// <summary>Os estágios NÃO engajados. Em array pra o EF traduzir <c>!NonEngaged.Contains(c.Stage)</c>
    /// em SQL (<c>NOT IN</c>) — verificado por teste de integração contra Postgres real.</summary>
    public static readonly ContactStage[] NonEngaged = [ContactStage.Lead, ContactStage.Lost];

    /// <summary>Engajou? (em memória — mesma regra do <see cref="NonEngaged"/>).</summary>
    public static bool IsEngaged(ContactStage stage) =>
        stage is not (ContactStage.Lead or ContactStage.Lost);
}
