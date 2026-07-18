import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import type { ContactGroupTag, FunnelLink, FunnelRow } from "../api/types";

const STATUS_LABEL: Record<FunnelRow["status"], { text: string; cls: string }> = {
  pending: { text: "Pendente", cls: "f-pending" },
  engaged: { text: "Respondeu", cls: "f-engaged" },
  replied: { text: "Auto-respondido", cls: "f-replied" },
};

// Funil de inbound: o operador escolhe uma audiência, escreve o texto pré-preenchido do link e
// (opcional) a auto-resposta; gera os links wa.me pra distribuir por anúncio/e-mail/etc. Quando a
// pessoa clica e te escreve, ela aparece como "Respondeu" — e aí o Chat conversa livre (sem 463).
export function FunnelScreen() {
  const [tags, setTags] = useState<ContactGroupTag[]>([]);
  const [groupTag, setGroupTag] = useState("");
  const [prefill, setPrefill] = useState("Oi! Vi seu contato e queria te falar uma coisa.");
  const [autoReply, setAutoReply] = useState("");
  const [generating, setGenerating] = useState(false);
  const [links, setLinks] = useState<FunnelLink[]>([]);
  const [rows, setRows] = useState<FunnelRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);

  const loadRows = useCallback(async () => {
    try {
      setRows(await api.funnelList());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, []);

  useEffect(() => {
    api.listContactGroupTags().then(setTags).catch(() => {});
    void loadRows();
    const h = setInterval(loadRows, 8_000);
    return () => clearInterval(h);
  }, [loadRows]);

  async function generate() {
    setGenerating(true);
    setError(null);
    try {
      const res = await api.funnelGenerate({
        groupTag: groupTag || undefined,
        prefillText: prefill || undefined,
        autoReplyText: autoReply || undefined,
      });
      setLinks(res.links);
      await loadRows();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setGenerating(false);
    }
  }

  function copy(text: string, key: string) {
    void navigator.clipboard.writeText(text);
    setCopied(key);
    setTimeout(() => setCopied((c) => (c === key ? null : c)), 1500);
  }

  function exportCsv() {
    const header = "nome,telefone,link\n";
    const body = links
      .map((l) => `"${(l.name ?? "").replace(/"/g, '""')}","${l.phone}","${l.link}"`)
      .join("\n");
    const blob = new Blob([header + body], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "funil-links.csv";
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <main className="funnel-screen" style={{ padding: 24, overflowY: "auto", display: "flex", flexDirection: "column", gap: 24 }}>
      <section style={{ maxWidth: 720, display: "flex", flexDirection: "column", gap: 12 }}>
        <h2 style={{ margin: 0 }}>Funil de inbound</h2>
        <p className="muted" style={{ margin: 0 }}>
          Gere links <strong>wa.me</strong> pra distribuir (anúncio, link, e-mail/SMS). Quando a pessoa clica e
          te escreve, ela vira <strong>Respondeu</strong> — e aí você conversa livre pelo Chat, sem o 463.
        </p>

        <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <span>Audiência</span>
          <select value={groupTag} onChange={(e) => setGroupTag(e.target.value)}>
            <option value="">Todos os contatos</option>
            {tags.map((t) => (
              <option key={t.groupTag} value={t.groupTag}>
                {t.groupTag} ({t.count})
              </option>
            ))}
          </select>
        </label>

        <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <span>Texto do link (o que já vem escrito quando a pessoa abre)</span>
          <textarea value={prefill} onChange={(e) => setPrefill(e.target.value)} rows={2} />
        </label>

        <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <span>Auto-resposta no 1º inbound (opcional — precisa do <code>Funnel__AutoReplyEnabled</code> ligado)</span>
          <textarea
            value={autoReply}
            onChange={(e) => setAutoReply(e.target.value)}
            rows={2}
            placeholder="Deixe vazio pra não responder automaticamente."
          />
        </label>

        <div>
          <button type="button" onClick={() => void generate()} disabled={generating}>
            {generating ? "Gerando..." : "Gerar links"}
          </button>
        </div>
        {error && <p className="error">{error}</p>}
      </section>

      {links.length > 0 && (
        <section style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <div style={{ display: "flex", gap: 12, alignItems: "center" }}>
            <h3 style={{ margin: 0 }}>{links.length} links gerados</h3>
            <button type="button" onClick={exportCsv}>Exportar CSV</button>
          </div>
          <div style={{ overflowX: "auto" }}>
            <table>
              <thead>
                <tr><th>Contato</th><th>Telefone</th><th>Link</th><th /></tr>
              </thead>
              <tbody>
                {links.map((l) => (
                  <tr key={l.contactId}>
                    <td>{l.name ?? "—"}</td>
                    <td style={{ fontVariantNumeric: "tabular-nums" }}>{l.phone}</td>
                    <td><a href={l.link} target="_blank" rel="noreferrer">{l.link}</a></td>
                    <td>
                      <button type="button" onClick={() => copy(l.link, l.contactId)}>
                        {copied === l.contactId ? "Copiado!" : "Copiar"}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      <section style={{ display: "flex", flexDirection: "column", gap: 8 }}>
        <h3 style={{ margin: 0 }}>Convites recentes</h3>
        <div style={{ overflowX: "auto" }}>
          <table>
            <thead>
              <tr><th>Contato</th><th>Telefone</th><th>Status</th><th>Criado</th><th>Respondeu</th></tr>
            </thead>
            <tbody>
              {rows.length === 0 && (
                <tr><td colSpan={5} className="muted">Nenhum convite ainda. Gere links acima.</td></tr>
              )}
              {rows.map((r) => {
                const s = STATUS_LABEL[r.status];
                return (
                  <tr key={r.contactId + r.createdAt}>
                    <td>{r.name ?? "—"}</td>
                    <td style={{ fontVariantNumeric: "tabular-nums" }}>{r.phone ?? "—"}</td>
                    <td><span className={`funnel-badge ${s.cls}`}>{s.text}</span></td>
                    <td>{new Date(r.createdAt).toLocaleString()}</td>
                    <td>{r.engagedAt ? new Date(r.engagedAt).toLocaleString() : "—"}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>
    </main>
  );
}
