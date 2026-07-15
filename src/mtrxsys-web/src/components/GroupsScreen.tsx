import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import type { Group, GroupMember, ImportResult } from "../api/types";
import { downloadContactsXlsx } from "../utils/exportContacts";
import { ConfirmDialog } from "./ConfirmDialog";

interface GroupRow {
  group: Group;
  importing: boolean;
  result: ImportResult | null;
  error: string | null;
}

// Uma linha de grupo. Igual nas duas seções — o que muda é só o destaque (o CSS da seção "mine").
// Ver os membros é sob demanda: a lista pode ter dezenas de grupos, e buscar participantes de todos
// seria uma chamada ao WAHA por linha, em toda abertura da aba.
function GroupRowView({
  row,
  onImport,
  onLeave,
  onExemptionChanged,
  onClaim,
  leaving,
}: {
  row: GroupRow;
  onImport: () => void;
  onLeave: () => void;
  onExemptionChanged: (enabled: boolean) => void;
  onClaim: () => void;
  leaving: boolean;
}) {
  const [members, setMembers] = useState<GroupMember[] | null>(null);
  const [membersError, setMembersError] = useState<string | null>(null);
  const [showMembers, setShowMembers] = useState(false);
  const [exemptionBusy, setExemptionBusy] = useState(false);
  const [exemptionError, setExemptionError] = useState<string | null>(null);

  // A chave só reflete o servidor DEPOIS que ele confirma. Nada de pintar o estado otimista aqui:
  // ligar pode falhar (WhatsApp fora → o backend recusa em vez de isentar com lista velha), e uma
  // chave que parece ligada sem estar diria que o disparo vai repetir quando não vai.
  async function toggleExemption() {
    const next = !row.group.exemptFromDispatchLimits;
    setExemptionBusy(true);
    setExemptionError(null);
    try {
      const res = await api.setGroupExemption(row.group.id, next);
      onExemptionChanged(res.enabled);
    } catch (ex) {
      setExemptionError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setExemptionBusy(false);
    }
  }

  async function toggleMembers() {
    if (showMembers) {
      setShowMembers(false);
      return;
    }
    setShowMembers(true);
    if (members) return; // já buscou; não re-busca a cada abrir/fechar
    setMembersError(null);
    try {
      setMembers(await api.listGroupMembers(row.group.id));
    } catch (ex) {
      setMembersError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  return (
    <li className={`group-row${row.group.isMine ? " group-row-mine" : ""}`}>
      <div className="group-info">
        <span className="group-name">{row.group.name || "(sem nome)"}</span>
        {row.group.participantsCount !== null && (
          <span className="muted small">{row.group.participantsCount} participantes</span>
        )}
        {row.result && (
          <span className="import-summary">
            {row.result.imported} importados · {row.result.duplicated} duplicados
            {row.result.failed > 0 && ` · ${row.result.failed} falharam`}
          </span>
        )}
        {row.error && <span className="error small">{row.error}</span>}
        {/* O input só existe em grupo SEU: em grupo de terceiro não há isenção pra ligar. */}
        {row.group.isMine && (
          <label className="group-exemption">
            <input
              type="checkbox"
              checked={row.group.exemptFromDispatchLimits}
              disabled={exemptionBusy}
              onChange={() => void toggleExemption()}
            />
            <span>
              Posso enviar mais de uma vez pra quem está neste grupo
              {exemptionBusy && " — salvando..."}
            </span>
          </label>
        )}
        {row.group.isMine && row.group.exemptFromDispatchLimits && (
          <span className="muted small">
            Vale pra quem estava no grupo quando você ligou a chave. Entrou gente depois? Desligue e
            ligue de novo. Quem responder <strong>SAIR</strong> continua fora — isso a chave não muda.
          </span>
        )}
        {exemptionError && <span className="error small">{exemptionError}</span>}
        {showMembers && (
          <div className="group-members">
            {membersError && <span className="error small">{membersError}</span>}
            {!members && !membersError && <span className="muted small">Carregando membros…</span>}
            {members?.length === 0 && <span className="muted small">Nenhum membro com número visível.</span>}
            {members?.map((m) => (
              <span key={m.phone} className="muted small">
                {m.name ? `${m.name} — ` : ""}{m.phone}{m.isAdmin ? " (admin)" : ""}
              </span>
            ))}
          </div>
        )}
      </div>
      <div className="group-actions">
        {/* Declarar posse: o grupo de aquecimento é criado por VOCÊ no aparelho físico, então o
            sistema não tem como saber que é seu — o WAHA não expõe quem criou. Marcar é o que
            habilita a seção verde e, depois, a isenção. */}
        <button
          type="button"
          onClick={onClaim}
          className="import-btn"
          title={row.group.isMine
            ? "Tira a marca de que este grupo é seu (o grupo continua no WhatsApp)"
            : "Marca este grupo como seu — habilita a isenção de disparo pros membros dele"}
        >
          {row.group.isMine ? "Não é meu" : "Este grupo é meu"}
        </button>
        <button type="button" onClick={() => void toggleMembers()} className="import-btn"
          title="Mostra o telefone de quem está dentro do grupo">
          {showMembers ? "Ocultar membros" : "Ver membros"}
        </button>
        <button
          type="button"
          onClick={onImport}
          disabled={row.importing}
          className="import-btn"
          title="Importa os participantes como contatos. Entrou gente nova no grupo depois? Clique de novo — só os novos são adicionados; os já cadastrados são pulados (duplicados)."
        >
          {row.importing ? "Importando..." : row.result ? "Importar novos" : "Importar contatos"}
        </button>
        <button
          type="button"
          onClick={onLeave}
          disabled={leaving}
          className="leave-btn"
          title="Faz o número conectado sair deste grupo"
        >
          {leaving ? "Saindo..." : "Sair"}
        </button>
      </div>
    </li>
  );
}

// Criar pelo sistema é a via SECUNDÁRIA, e de propósito: num chip novo e frio, "criar grupo com 5
// participantes" por API como primeira atividade da conta é assinatura de bot. O caminho normal é
// criar no aparelho, na mão, e marcar aqui com "Este grupo é meu". Isto serve pra chip já quente,
// ou pra quem quer montar o grupo sem pegar no celular.
function CreateGroupForm({ onCreated }: { onCreated: () => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [phones, setPhones] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    const list = phones
      .split(/[\n,;]+/)
      .map((p) => p.trim())
      .filter((p) => p.length > 0);
    if (!name.trim() || list.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      await api.createGroup({ name: name.trim(), phones: list });
      setName("");
      setPhones("");
      setOpen(false);
      onCreated();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
    }
  }

  if (!open) {
    return (
      <div className="groups-create">
        <button type="button" className="import-btn" onClick={() => setOpen(true)}>
          + Criar grupo
        </button>
      </div>
    );
  }

  return (
    <div className="groups-create groups-create-open">
      <label htmlFor="new-group-name">Nome do grupo</label>
      <input
        id="new-group-name"
        value={name}
        onChange={(e) => setName(e.target.value)}
        placeholder="Amigos — aquecimento"
      />
      <label htmlFor="new-group-phones">Participantes (um por linha, ou separados por vírgula)</label>
      <textarea
        id="new-group-phones"
        value={phones}
        onChange={(e) => setPhones(e.target.value)}
        rows={5}
        placeholder={"+55 71 99999-8888\n+55 71 98888-7777"}
      />
      <p className="muted small">
        O grupo é criado no WhatsApp de verdade, pelo número conectado, e já fica marcado como
        <strong> seu</strong>.
      </p>
      <p className="muted small">
        <strong>Chip novo?</strong> Prefira criar o grupo <strong>no seu aparelho</strong> e marcar
        aqui com "Este grupo é meu". Criar por aqui é o sistema agindo pela conta, e numa conta
        recém-criada isso parece robô — que é justamente o que o aquecimento evita.
      </p>
      {error && <p className="error small">{error}</p>}
      <div className="group-actions">
        <button type="button" className="import-btn" onClick={() => void submit()} disabled={busy || !name.trim() || !phones.trim()}>
          {busy ? "Criando..." : "Criar grupo"}
        </button>
        <button type="button" className="leave-btn" onClick={() => setOpen(false)} disabled={busy}>
          Cancelar
        </button>
      </div>
    </div>
  );
}

export function GroupsScreen() {
  const [rows, setRows] = useState<GroupRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Grupo aguardando confirmação de saída (abre o modal); e o id em processo de saída (trava o botão).
  const [confirmLeave, setConfirmLeave] = useState<Group | null>(null);
  const [leavingId, setLeavingId] = useState<string | null>(null);
  // Grupo aguardando confirmação de "é meu" / "não é meu". Marcar habilita a isenção, então passa
  // por confirmação — é o clique que decide quem pode receber disparo repetido.
  const [confirmClaim, setConfirmClaim] = useState<Group | null>(null);
  // Bumpar a chave dispara o useEffect (re-busca da WAHA) preservando a proteção
  // 'cancelled' contra setState pós-unmount.
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      try {
        const groups = await api.listGroups();
        if (!cancelled) {
          setRows(groups.map((g) => ({ group: g, importing: false, result: null, error: null })));
          setLoadError(null);
        }
      } catch (ex) {
        if (!cancelled) setLoadError(ex instanceof Error ? ex.message : String(ex));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [reloadKey]);

  const refresh = useCallback(() => {
    setReloadKey((k) => k + 1);
  }, []);

  // Atualiza só a linha tocada, com o valor que o SERVIDOR confirmou. Um refresh inteiro aqui
  // fecharia os "Ver membros" abertos e re-bateria no WhatsApp à toa.
  const setExemption = useCallback((groupId: string, enabled: boolean) => {
    setRows((prev) =>
      prev.map((r) =>
        r.group.id === groupId
          ? { ...r, group: { ...r.group, exemptFromDispatchLimits: enabled } }
          : r,
      ),
    );
  }, []);

  async function importOne(idx: number) {
    setRows((prev) =>
      prev.map((r, i) => (i === idx ? { ...r, importing: true, result: null, error: null } : r)),
    );
    try {
      const groupId = rows[idx].group.id;
      const tagSuggestion = rows[idx].group.name?.trim() || groupId;
      const result = await api.importGroup(groupId, tagSuggestion);
      setRows((prev) =>
        prev.map((r, i) => (i === idx ? { ...r, importing: false, result } : r)),
      );
      // Baixa automaticamente a planilha com os contatos salvos desse grupo.
      try {
        const saved = await api.listContacts({ groupTag: tagSuggestion });
        if (saved.length > 0) {
          downloadContactsXlsx(saved, tagSuggestion);
        }
      } catch {
        // download é um extra — não atrapalha o fluxo de importação se falhar
      }
    } catch (ex) {
      setRows((prev) =>
        prev.map((r, i) =>
          i === idx
            ? { ...r, importing: false, error: ex instanceof Error ? ex.message : String(ex) }
            : r,
        ),
      );
    }
  }

  // Sai do grupo de verdade (número conectado deixa o grupo). Confirmado pelo modal. Em sucesso,
  // remove a linha na hora; o backend é tolerante a grupo-fantasma (sempre "saiu" do ponto do usuário).
  async function leaveGroup(group: Group) {
    // Fecha o modal JÁ na confirmação: o leave do WAHA pode demorar (até o timeout de 60s) e,
    // se o modal ficasse aberto, daria pra clicar "Sim, sair" de novo e disparar leaves duplicados.
    // O feedback durante a espera fica no botão da própria linha ("Saindo...").
    setConfirmLeave(null);
    setLeavingId(group.id);
    try {
      await api.leaveGroup(group.id);
      setRows((prev) => prev.filter((r) => r.group.id !== group.id));
    } catch (ex) {
      setRows((prev) =>
        prev.map((r) =>
          r.group.id === group.id
            ? { ...r, error: ex instanceof Error ? ex.message : String(ex) }
            : r,
        ),
      );
    } finally {
      setLeavingId(null);
    }
  }

  // Marca/desmarca a posse. Em sucesso atualiza só a linha tocada; desmarcar derruba a isenção
  // JUNTO (o backend apaga a fotografia em cascata), e a tela tem que refletir isso, senão a caixa
  // continuaria marcada mentindo que a dispensa ainda vale.
  async function toggleClaim(group: Group) {
    setConfirmClaim(null);
    try {
      // Marcar JÁ liga a dispensa (o servidor fotografa os membros no mesmo ato) — dizer "é meu" e
      // depois "posso falar de novo" era a mesma afirmação duas vezes. Desmarcar derruba as duas.
      // O estado vem do que o SERVIDOR confirmou, não de palpite: se a leitura dos membros falhar no
      // criar-pelo-sistema, ele devolve exempt=false, e a caixa tem que aparecer desligada mesmo.
      const exempt = group.isMine ? false : (await api.claimGroup(group.id)).exempt;
      if (group.isMine) {
        await api.unclaimGroup(group.id);
      }
      setRows((prev) =>
        prev.map((r) =>
          r.group.id === group.id
            ? {
                ...r,
                error: null,
                group: {
                  ...r.group,
                  isMine: !group.isMine,
                  exemptFromDispatchLimits: exempt,
                },
              }
            : r,
        ),
      );
    } catch (ex) {
      setRows((prev) =>
        prev.map((r) =>
          r.group.id === group.id
            ? { ...r, error: ex instanceof Error ? ex.message : String(ex) }
            : r,
        ),
      );
    }
  }

  // No primeiro load (rows vazia) mostra "Carregando..." cheio. Em refresh, mantém a lista
  // antiga visível com o botão em "Atualizando..." pra não dar flash de tela em branco.
  if (loading && rows.length === 0) return <div className="loading">Carregando grupos...</div>;

  // A separação vem do backend (isMine), que a lê de owned_groups — não de heurística sobre o
  // nome ou sobre ser admin.
  const mine = rows.filter((r) => r.group.isMine);
  const others = rows.filter((r) => !r.group.isMine);

  return (
    <main className="groups-screen">
      <header className="groups-header">
        <div className="groups-header-row">
          <h2>Grupos</h2>
          <button
            type="button"
            onClick={refresh}
            disabled={loading}
            className="import-btn"
            title="Re-busca a lista de grupos do WhatsApp conectado"
          >
            {loading ? "Atualizando..." : "Atualizar"}
          </button>
        </div>
        <p className="muted">
          Grupos que o WhatsApp logado participa. Importe os participantes pra cadastrá-los como contatos no CRM.
          Entrou gente nova depois? Clique <strong>Importar</strong> de novo — só os novos são adicionados
          (os já cadastrados são pulados como duplicados).
        </p>
      </header>
      {loadError && <p className="error">{loadError}</p>}

      <CreateGroupForm onCreated={refresh} />

      {/* MEUS GRUPOS em seção própria, no topo e em verde. O que sustenta a separação não é
          aparência: é o registro de que ESTE sistema criou o grupo (owned_groups). O WAHA não expõe
          quem criou — sem o registro, isto seria adivinhação. */}
      {mine.length > 0 && (
        <section className="groups-section groups-section-mine">
          <h3 className="groups-section-title">Meus grupos</h3>
          <p className="muted small">
            Grupos que você marcou como seus. São os únicos que podem dispensar a trava de envio
            repetido. Marque só os de gente conhecida.
          </p>
          <ul className="groups-list">
            {mine.map((row) => (
              <GroupRowView
                key={row.group.id}
                row={row}
                onImport={() => void importOne(rows.indexOf(row))}
                onLeave={() => setConfirmLeave(row.group)}
                onExemptionChanged={(enabled) => setExemption(row.group.id, enabled)}
                onClaim={() => setConfirmClaim(row.group)}
                leaving={leavingId === row.group.id}
              />
            ))}
          </ul>
        </section>
      )}

      {/* "Outros" só faz sentido em contraste com a seção acima. Sem grupo meu (o caso de todo mundo
          hoje), a lista fica como sempre foi — sem um título solto por cima. */}
      {mine.length > 0 && others.length > 0 && <h3 className="groups-section-title">Outros grupos</h3>}
      {rows.length === 0 && !loadError && <p className="muted">Você não participa de nenhum grupo.</p>}
      <ul className="groups-list">
        {others.map((row) => (
          <GroupRowView
            key={row.group.id}
            row={row}
            onImport={() => void importOne(rows.indexOf(row))}
            onLeave={() => setConfirmLeave(row.group)}
            onExemptionChanged={(enabled) => setExemption(row.group.id, enabled)}
            onClaim={() => setConfirmClaim(row.group)}
            leaving={leavingId === row.group.id}
          />
        ))}
      </ul>

      {confirmClaim && (
        <ConfirmDialog
          title={confirmClaim.isMine ? "Tirar a marca deste grupo?" : "Marcar este grupo como seu?"}
          message={
            confirmClaim.isMine ? (
              <>
                <strong>"{confirmClaim.name || "(sem nome)"}"</strong> sai da sua lista de grupos.
                <br />
                <br />
                O grupo <strong>continua no WhatsApp</strong> — some só a marca. Se a dispensa de
                envio repetido estava ligada, ela é <strong>desligada junto</strong>.
              </>
            ) : (
              <>
                Marca <strong>"{confirmClaim.name || "(sem nome)"}"</strong> como seu. Use isto no
                grupo que <strong>você criou no seu aparelho</strong>.
                <br />
                <br />
                Quem está dentro dele passa a <strong>poder receber disparo mais de uma vez</strong> —
                a trava de "já enviei pra esse" deixa de valer pra eles. Só marque um grupo de{" "}
                <strong>gente conhecida</strong>: marcar um grupo de contatos frios abriria envio
                repetido para desconhecidos.
                <br />
                <br />
                O que <strong>continua valendo</strong>: quem responder <strong>SAIR</strong> fica
                fora, números inexistentes seguem barrados e o teto diário do aquecimento não muda.
                Você pode desligar a repetição na caixa da linha, sem tirar a marca.
              </>
            )
          }
          confirmLabel={confirmClaim.isMine ? "Tirar a marca" : "Sim, o grupo é meu"}
          cancelLabel="Cancelar"
          onConfirm={() => void toggleClaim(confirmClaim)}
          onCancel={() => setConfirmClaim(null)}
        />
      )}

      {confirmLeave && (
        <ConfirmDialog
          title="Sair deste grupo?"
          message={
            <>
              O número conectado vai <strong>sair de "{confirmLeave.name || "(sem nome)"}"</strong>.
              <br />
              <br />
              Essa ação é <strong>irreversível</strong> e afeta o WhatsApp de verdade. Para voltar ao
              grupo, só com um novo convite. Serve para sair de grupos que não aparecem no seu celular.
            </>
          }
          confirmLabel="Sim, sair do grupo"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void leaveGroup(confirmLeave)}
          onCancel={() => setConfirmLeave(null)}
        />
      )}
    </main>
  );
}
