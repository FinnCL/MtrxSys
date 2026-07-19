using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public static class SharedPhoneLedgerExtensions
{
    /// <summary>Tira do público quem o registro compartilhado suprime: opt-out SEMPRE; "enviado" só de
    /// OUTRO chip (ver <see cref="ISharedPhoneLedger.GetSuppressedAsync"/> — mesmo-chip é liberado pelo
    /// fix cross-chip, o que faz o reset diário do aquecimento voltar a alcançar os respondedores).
    /// No-op em Observe/Off (<see cref="ISharedPhoneLedger.IsEnforcing"/> false) e em lista vazia.
    ///
    /// FONTE ÚNICA desse dedup: disparo, prévia da contagem e reset do aquecimento chamam aqui — sem
    /// isso a mesma lógica ficava copiada em três lugares e uma mudança de regra derivava só em um.</summary>
    public static async Task<List<Contact>> FilterOutSuppressedAsync(
        this ISharedPhoneLedger ledger, IReadOnlyList<Contact> contacts, CancellationToken ct)
    {
        if (!ledger.IsEnforcing || contacts.Count == 0)
        {
            return contacts.ToList();
        }
        var suppressed = await ledger.GetSuppressedAsync(
            contacts.Select(c => c.Phone.E164).ToArray(), ct);
        return suppressed.Count == 0
            ? contacts.ToList()
            : contacts.Where(c => !suppressed.Contains(c.Phone.E164)).ToList();
    }
}
