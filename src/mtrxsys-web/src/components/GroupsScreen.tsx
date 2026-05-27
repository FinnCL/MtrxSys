import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Group, ImportResult } from "../api/types";
import { downloadContactsXlsx } from "../utils/exportContacts";

interface GroupRow {
  group: Group;
  importing: boolean;
  result: ImportResult | null;
  error: string | null;
}

export function GroupsScreen() {
  const [rows, setRows] = useState<GroupRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

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

  if (loading) return <div className="loading">Carregando grupos...</div>;

  return (
    <main className="groups-screen">
      <header className="groups-header">
        <h2>Grupos</h2>
        <p className="muted">
          Grupos que o WhatsApp logado participa. Importe os participantes pra cadastrá-los como contatos no CRM.
        </p>
      </header>
      {loadError && <p className="error">{loadError}</p>}
      {rows.length === 0 && !loadError && <p className="muted">Você não participa de nenhum grupo.</p>}
      <ul className="groups-list">
        {rows.map((row, i) => (
          <li key={row.group.id} className="group-row">
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
            </div>
            <button
              type="button"
              onClick={() => void importOne(i)}
              disabled={row.importing}
              className="import-btn"
            >
              {row.importing ? "Importando..." : "Importar contatos"}
            </button>
          </li>
        ))}
      </ul>
    </main>
  );
}
