import { useCallback, useEffect, useState } from "react";
import { api } from "../api/client";
import type { ContactGroupTag, FunnelRow } from "../api/types";

const STATUS_LABEL: Record<FunnelRow["status"], { text: string; cls: string }> = {
  pending: { text: "Pendente", cls: "f-pending" },
  engaged: { text: "Respondeu", cls: "f-engaged" },
  replied: { text: "Auto-respondido", cls: "f-replied" },
};

// Funil de inbound: o operador escolhe uma audiência e o texto que já vem escrito pra pessoa te
// enviar; o sistema gera UM link wa.me do CHIP conectado pra distribuir (anúncio, e-mail/SMS, ou
// postar num grupo). Quem clica te ESCREVE (inbound = consentimento) e vira "Respondeu" — e aí o
// Chat conversa livre, sem o 463. O casamento de quem respondeu é pelo telefone, não pelo link.
export function FunnelScreen() {
  const [tags, setTags] = useState<ContactGroupTag[]>([]);
  const [groupTag, setGroupTag] = useState("");
  const [prefill, setPrefill] = useState("Oi! Tenho interesse, pode me passar mais informações?");
  const [autoReply, setAutoReply] = useState("");
  const [generating, setGenerating] = useState(false);
  const [chatLink, setChatLink] = useState<string | null>(null);
  const [invitedCount, setInvitedCount] = useState(0);
  const [rows, setRows] = useState<FunnelRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const loadRows = useCallback(async () => {
    try {
      setRows(await api.funnelList());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, []);

  useEffect(() => {
    api.listContactGroupTags().then(setTags).catch(() => {});
    // Sincroniza com estado externo (convites no servidor): o setState é pós-await, não cascateia
    // render — mesmo padrão do HumanPhaseCard/PlaybookScreen.
    // eslint-disable-next-line react-hooks/set-state-in-effect
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
      setChatLink(res.chatLink);
      setInvitedCount(res.count);
      await loadRows();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setGenerating(false);
    }
  }

  function copyLink() {
    if (!chatLink) return;
    void navigator.clipboard.writeText(chatLink);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  return (
    <main className="funnel-screen" style={{ padding: 24, overflowY: "auto", display: "flex", flexDirection: "column", gap: 24 }}>
      <section style={{ maxWidth: 720, display: "flex", flexDirection: "column", gap: 12 }}>
        <h2 style={{ margin: 0 }}>Funil de inbound</h2>
        <p className="muted" style={{ margin: 0 }}>
          Gere <strong>um link wa.me do seu chip</strong> pra distribuir (anúncio, link, e-mail/SMS, ou
          poste num grupo que você participa). Quem clica <strong>te escreve</strong> — vira{" "}
          <strong>Respondeu</strong> e você conversa livre pelo Chat, sem o 463.
        </p>

        <label style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <span>Audiência (quem você vai convidar)</span>
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
          <span>Texto que já vem escrito pra pessoa te enviar (ela pode editar)</span>
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
            {generating ? "Gerando..." : "Gerar link"}
          </button>
        </div>
        {error && <p className="error">{error}</p>}
      </section>

      {chatLink && (
        <section style={{ maxWidth: 720, display: "flex", flexDirection: "column", gap: 8 }}>
          <h3 style={{ margin: 0 }}>Link do chip</h3>
          <p className="muted" style={{ margin: 0 }}>
            {invitedCount} {invitedCount === 1 ? "contato convidado" : "contatos convidados"} — distribua o
            link abaixo. Quem clicar e te escrever aparece como <strong>Respondeu</strong> na tabela.
          </p>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <a href={chatLink} target="_blank" rel="noreferrer" style={{ wordBreak: "break-all" }}>{chatLink}</a>
            <button type="button" onClick={copyLink}>{copied ? "Copiado!" : "Copiar"}</button>
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
                <tr><td colSpan={5} className="muted">Nenhum convite ainda. Gere o link acima.</td></tr>
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
