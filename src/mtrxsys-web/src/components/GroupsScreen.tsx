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

// Uma linha de grupo. Ver os membros é sob demanda: a lista pode ter dezenas de grupos, e buscar
// participantes de todos seria uma chamada ao WAHA por linha, em toda abertura da aba.
function GroupRowView({
  row,
  onImport,
  onLeave,
  leaving,
}: {
  row: GroupRow;
  onImport: () => void;
  onLeave: () => void;
  leaving: boolean;
}) {
  const [members, setMembers] = useState<GroupMember[] | null>(null);
  const [membersError, setMembersError] = useState<string | null>(null);
  const [showMembers, setShowMembers] = useState(false);

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
    <li className="group-row">
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

export function GroupsScreen() {
  const [rows, setRows] = useState<GroupRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Grupo aguardando confirmação de saída (abre o modal); e o id em processo de saída (trava o botão).
  const [confirmLeave, setConfirmLeave] = useState<Group | null>(null);
  const [leavingId, setLeavingId] = useState<string | null>(null);
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

  // Chaveado pelo ID do grupo, nunca pelo índice: a lista muda embaixo (sair de um grupo remove a
  // linha, o refresh reordena) e o import é lento. Com índice, sair de um grupo enquanto outro
  // importa faria o resultado cair na LINHA ERRADA — ou sumir, se a lista tivesse encolhido.
  async function importOne(group: Group) {
    const groupId = group.id;
    setRows((prev) =>
      prev.map((r) =>
        r.group.id === groupId ? { ...r, importing: true, result: null, error: null } : r,
      ),
    );
    try {
      const tagSuggestion = group.name?.trim() || groupId;
      const result = await api.importGroup(groupId, tagSuggestion);
      setRows((prev) =>
        prev.map((r) => (r.group.id === groupId ? { ...r, importing: false, result } : r)),
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
        prev.map((r) =>
          r.group.id === groupId
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

  // No primeiro load (rows vazia) mostra "Carregando..." cheio. Em refresh, mantém a lista
  // antiga visível com o botão em "Atualizando..." pra não dar flash de tela em branco.
  if (loading && rows.length === 0) return <div className="loading">Carregando grupos...</div>;

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
          Entrou gente nova depois? Clique <strong>Importar</strong> de novo: só os novos são adicionados
          (os já cadastrados são pulados como duplicados).
        </p>
      </header>
      {loadError && <p className="error">{loadError}</p>}

      {rows.length === 0 && !loadError && <p className="muted">Você não participa de nenhum grupo.</p>}
      <ul className="groups-list">
        {rows.map((row) => (
          <GroupRowView
            key={row.group.id}
            row={row}
            onImport={() => void importOne(row.group)}
            onLeave={() => setConfirmLeave(row.group)}
            leaving={leavingId === row.group.id}
          />
        ))}
      </ul>

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
