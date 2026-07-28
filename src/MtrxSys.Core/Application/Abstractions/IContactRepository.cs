using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public interface IContactRepository
{
    Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Contact?> GetByPhoneAsync(string e164, CancellationToken ct);
    /// <summary>Carrega num único SELECT os contatos cujos telefones estão na lista, indexados por
    /// E.164. Usado pela importação de grupo pra evitar o N+1 (uma consulta por participante).</summary>
    Task<IReadOnlyDictionary<string, Contact>> GetByPhonesAsync(IReadOnlyCollection<string> e164s, CancellationToken ct);
    Task AddAsync(Contact contact, CancellationToken ct);
    Task UpdateAsync(Contact contact, CancellationToken ct);
    Task<IReadOnlyList<Contact>> ListByFilterAsync(ContactFilter filter, CancellationToken ct);

    /// <summary>Telefone e situação de TODOS os contatos — INCLUSIVE descartados e opt-out.</summary>
    /// <remarks>
    /// Existe porque <see cref="ListByFilterAsync"/> esconde os dois: o ApplyFilter começa com
    /// <c>DeletedAt == null</c> e o <c>ExcludeOptedOut</c> é true por padrão. Isso é o certo pra montar
    /// fila de disparo, e é EXATAMENTE o errado pra responder "esse número já é conhecido?".
    ///
    /// <para>🔴 Quem usar o filtro pra essa pergunta oferece de volta quem pediu pra SAIR, porque um
    /// opt-out simplesmente não aparece na resposta. Nesta base, em 2026-07-28, isso valeria pra 335
    /// contatos descartados além dos opt-out.</para>
    ///
    /// Projeção, não entidade: é leitura pura pra comparação, não passa pelo change tracker.
    /// </remarks>
    Task<IReadOnlyList<ContactPhoneStatus>> ListAllPhoneStatusAsync(CancellationToken ct);
    Task<int> CountByFilterAsync(ContactFilter filter, CancellationToken ct);
    /// <summary>Telefones (E.164) de TODOS os contatos em opt-out (incluindo descartados — opt-out
    /// continua valendo). Projeção leve para o backfill periódico do registro compartilhado, sem
    /// carregar entidades inteiras.</summary>
    Task<IReadOnlyList<string>> ListOptedOutPhonesAsync(CancellationToken ct);
    Task<IReadOnlyList<ContactGroupTag>> ListGroupTagsAsync(CancellationToken ct);
    /// <summary>Descarta (soft delete) os contatos de um grupo: marca deleted_at, somem das
    /// listas/disparo, mas a linha e o opt-out ficam no banco. Retorna quantos foram descartados.</summary>
    Task<int> DiscardByGroupTagAsync(string groupTag, DateTimeOffset now, CancellationToken ct);

    /// <summary>Zera o marcador de envio (LastSentAt) de todos os contatos. Usado no "Renovar
    /// lista": quem só tinha recebido volta a "Novo", consistente com voltar a ser re-disparável.</summary>
    Task<int> ClearLastSentAsync(CancellationToken ct);

    /// <summary>Passa o "dono" (ImportedByPhone) de TODOS os contatos vivos para o chip informado, numa
    /// única instrução. Retorna quantos mudaram de fato.
    /// <para>⚠️ Afrouxa a trava anti-463 por decisão explícita do operador — ver o endpoint
    /// <c>/api/contacts/reassign-to-current-chip</c>. Não chamar de nada automático.</para></summary>
    Task<int> ReassignToChipAsync(string chipPhoneE164, CancellationToken ct);

    /// <summary>Zera o LastSentAt só de quem ENGAJOU (respondeu/avançou — Stage != Novo/Descartado), pra
    /// re-disparar pros MESMOS no dia seguinte durante o aquecimento. NÃO toca em quem saiu (opt-out),
    /// descartados, nem nos "Novo" (frios). Retorna quantos foram liberados.</summary>
    Task<int> ClearLastSentForEngagedAsync(CancellationToken ct);

    /// <summary>Zera o LastSentAt SÓ dos telefones dados (o Círculo de Aquecimento escolhido pelo
    /// operador), pra re-disparar pros MESMOS na fase híbrida — SEM reabrir frios que responderam
    /// (esses ficam com LastSentAt e o dedup os mantém fora). Retorna quantos foram liberados.</summary>
    Task<int> ClearLastSentForPhonesAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct);
}

/// <summary>Situação de um telefone no sistema. <paramref name="Ativo"/> false = descartado ou opt-out:
/// o número É conhecido, mas não pode ser reoferecido pra importação.</summary>
public sealed record ContactPhoneStatus(string PhoneE164, bool Ativo);

public sealed record ContactFilter(
    ContactStage? Stage = null,
    string? TagName = null,
    string? GroupTag = null,
    bool ExcludeOptedOut = true,
    bool EngagedOnly = false,
    // Telefone E.164 a excluir — usado pra nunca disparar pro próprio número conectado.
    string? ExcludePhoneE164 = null,
    // Exclui quem já tem job Pending ou Sent — evita re-enviar pra quem já recebeu e
    // duplicar quem já está na fila. Usado no disparo e na prévia de público.
    bool ExcludeAlreadyDispatched = false,
    // Restringe a UM contato (por Id). Usado pelo enfileiramento por-respondedor (webhook) pra
    // reusar EXATAMENTE o mesmo dedup/segurança do disparo em lote, só que num contato só.
    Guid? ContactId = null,
    // Só contatos importados por ESTE chip (gate anti-463, o mesmo do DispatchEngine). Sem isto o
    // público era montado sem saber que o motor ia pular quem é de outro chip: em 2026-07-26 a fila
    // nasceu com 173 pendentes dos quais só 119 podiam sair, e os outros 54 iam ser processados um a
    // um só pra virar "pulado". O gate protegia (nada errado saía), mas o desperdício era real e o
    // relatório enchia de linhas que nunca sairiam — a tela chegava a ESCONDÊ-LAS e a explicar num
    // banner, que é remendo na saída pra um problema que estava na entrada.
    //
    // ⚠️ NULL = NÃO FILTRA, e isso é proposital. Se o número do chip não puder ser lido, o público
    // tem que degradar pro comportamento antigo (enfileira todos, o motor decide), NUNCA pra "público
    // vazio". É a mesma doutrina do motor (`connectedPhone null → não bloqueia`): leitura indisponível
    // é desconhecido, não ausência, e um blip de infra não pode zerar o disparo em silêncio.
    string? ImportedByPhone = null);

public sealed record ContactGroupTag(string GroupTag, int Count);
