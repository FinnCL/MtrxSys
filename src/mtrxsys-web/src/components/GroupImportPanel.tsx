import { type ReactNode, useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import type { Group, GroupMember, ImportResult } from "../api/types";
import { downloadContactsXlsx } from "../utils/exportContacts";

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
  extraActions,
}: {
  row: GroupRow;
  onImport: () => void;
  extraActions?: ReactNode;
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
          title="Importa os participantes como contatos. Entrou gente nova no grupo depois? Clique de novo — só os novos são adicionados; os já cadastrados são pulados (duplicados). Re-importar também retag os contatos pro chip conectado AGORA (habilita o disparo por este chip)."
        >
          {row.importing ? "Importando..." : row.result ? "Importar novos" : "Importar contatos"}
        </button>
        {extraActions}
      </div>
    </li>
  );
}

// Painel compartilhado de importação de grupos (usado nas abas Grupos E Contatos). Lista os grupos do
// WhatsApp conectado e importa os participantes como contatos, marcando-os com o chip conectado AGORA
// (ImportedByPhone) — é isto que habilita o disparo por este chip e "move" contatos de um chip antigo
// pro novo (re-importar depois de trocar o chip). Fonte única: os dois lugares usam este mesmo painel.
export function GroupImportPanel({
  onImported,
  extraActions,
  reloadSignal,
  autoDownload = true,
}: {
  // Chamado após um import bem-sucedido (ex.: a aba Contatos recarrega as listas).
  onImported?: (tag: string, result: ImportResult) => void;
  // Ações extras por linha (a aba Grupos injeta o botão "Sair"; a aba Contatos não passa nada).
  extraActions?: (group: Group) => ReactNode;
  // Bump externo pra forçar re-busca da lista (a aba Grupos usa após "Sair").
  reloadSignal?: number;
  // Baixar a planilha dos contatos salvos após importar (default true, como era nos Grupos).
  autoDownload?: boolean;
}) {
  const [rows, setRows] = useState<GroupRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Refresh interno (botão "Atualizar"); combinado com o reloadSignal externo no useEffect.
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
  }, [reloadKey, reloadSignal]);

  const refresh = useCallback(() => setReloadKey((k) => k + 1), []);

  // Chaveado pelo ID do grupo, nunca pelo índice: a lista muda embaixo e o import é lento — com índice,
  // um refresh no meio faria o resultado cair na linha errada.
  async function importOne(group: Group) {
    const groupId = group.id;
    setRows((prev) =>
      prev.map((r) => (r.group.id === groupId ? { ...r, importing: true, result: null, error: null } : r)),
    );
    try {
      const tagSuggestion = group.name?.trim() || groupId;
      const result = await api.importGroup(groupId, tagSuggestion);
      setRows((prev) =>
        prev.map((r) => (r.group.id === groupId ? { ...r, importing: false, result } : r)),
      );
      onImported?.(tagSuggestion, result);
      if (autoDownload) {
        // Baixa a planilha com os contatos salvos desse grupo. Extra — não atrapalha se falhar.
        try {
          const saved = await api.listContacts({ groupTag: tagSuggestion });
          if (saved.length > 0) {
            // AWAIT: o download virou assíncrono (xlsx por import dinâmico). Sem esperar, a
            // rejeição escaparia deste catch e viraria unhandled rejection.
            await downloadContactsXlsx(saved, tagSuggestion);
          }
        } catch {
          /* download é um extra */
        }
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

  if (loading && rows.length === 0) return <p className="muted">Carregando grupos...</p>;

  return (
    <div className="group-import-panel">
      <div className="groups-header-row">
        <button
          type="button"
          onClick={refresh}
          disabled={loading}
          className="import-btn"
          title="Re-busca a lista de grupos do WhatsApp conectado"
        >
          {loading ? "Atualizando..." : "Atualizar grupos"}
        </button>
      </div>
      {loadError && <p className="error">{loadError}</p>}
      {rows.length === 0 && !loadError && <p className="muted">Você não participa de nenhum grupo.</p>}
      <ul className="groups-list">
        {rows.map((row) => (
          <GroupRowView
            key={row.group.id}
            row={row}
            onImport={() => void importOne(row.group)}
            extraActions={extraActions?.(row.group)}
          />
        ))}
      </ul>
    </div>
  );
}
