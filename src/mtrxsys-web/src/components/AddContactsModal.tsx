import { useEffect, useMemo, useState } from "react";
import { api } from "../api/client";
import { type ManualImportResult, type ManualLineStatus } from "../api/types";

interface Props {
  onClose: () => void;
  // Chamado após salvar com sucesso, pra a tela recarregar os grupos/contatos.
  onSaved: (result: ManualImportResult) => void;
}

// Grupo padrão pros números avulsos quando o usuário não nomeia um lote.
const DEFAULT_GROUP = "Avulsos";
// Valor sentinela do seletor que revela o campo "nome do novo grupo".
const NEW_GROUP = "__new__";

// Quantas linhas a prévia renderiza (evita travar o DOM com colagens enormes).
const PREVIEW_CAP = 100;

// Separa o texto colado em candidatos: uma linha, vírgula ou ponto-e-vírgula por número.
function parseNumbers(text: string): string[] {
  return text
    .split(/[\n,;]+/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

// Validação CLIENTE preliminar (instantânea, sem libphonenumber): sinaliza só erros INEQUÍVOCOS
// — vazio, DDI de outro país, dígitos de menos (faltou DDD) ou de mais (dois números juntos). O
// que passa aqui ainda é validado DE VERDADE no backend ao Adicionar (Anatel, fixo×móvel, 9º
// dígito). Assim a prévia nunca marca como ok algo que o backend rejeitaria por engano.
function previewCheck(raw: string): { ok: boolean; reason?: string } {
  const trimmed = raw.trim();
  if (trimmed.startsWith("+") && !trimmed.replace(/[\s()-]/g, "").startsWith("+55")) {
    return { ok: false, reason: "DDI de outro país (só Brasil)" };
  }
  const digits = trimmed.replace(/\D/g, "");
  if (digits.length === 0) return { ok: false, reason: "sem dígitos" };
  // Tira o 55 do DDI pra raciocinar sobre DDD + número.
  const nat =
    (digits.length === 12 || digits.length === 13) && digits.startsWith("55")
      ? digits.slice(2)
      : digits;
  if (nat.length < 10) return { ok: false, reason: "faltam dígitos — precisa DDD + número" };
  if (nat.length > 11) return { ok: false, reason: "dígitos demais — dois números juntos?" };
  return { ok: true };
}

const STATUS_META: Record<ManualLineStatus, { dot: string; className: string; label: string }> = {
  Ok: { dot: "dot-ok", className: "manual-line-ok", label: "adicionado" },
  Corrected: { dot: "dot-corrected", className: "manual-line-corrected", label: "corrigido" },
  Duplicate: { dot: "dot-dup", className: "manual-line-duplicate", label: "já existia" },
  Invalid: { dot: "dot-invalid", className: "manual-line-invalid", label: "inválido" },
};

// Modal pra digitar/colar números manuais. Diferente do import de grupo, aqui o usuário escolhe
// (ou cria) o grupo-destino — assim os lotes ficam nomeados e disparáveis em vez de um amontoado.
// Mostra o resultado em resumo + inválidos separados (pra corrigir) + detalhes recolhíveis.
export function AddContactsModal({ onClose, onSaved }: Props) {
  const [text, setText] = useState("");
  const [groupChoice, setGroupChoice] = useState<string>(DEFAULT_GROUP);
  const [newGroup, setNewGroup] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<ManualImportResult | null>(null);
  const [showDetails, setShowDetails] = useState(false);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Só "Avulsos" (default) e "Criar nova lista…": as listas importadas NÃO entram aqui — a adição
  // manual é uma origem separada. A "lista" é apenas um rótulo (groupTag) pra organizar/segmentar o
  // disparo; NÃO é o grupo do WhatsApp da aba Grupos (esse tem conversa, membros e notifica todo
  // mundo). Chamavam-se os dois de "grupo" e a confusão era garantida — só o texto mudou, o campo
  // no banco/API segue groupTag.
  const groupOptions = [DEFAULT_GROUP];
  const finalGroup = groupChoice === NEW_GROUP ? newGroup.trim() : groupChoice;
  const numbers = useMemo(() => parseNumbers(text), [text]);
  const preview = useMemo(() => numbers.map((n) => ({ input: n, ...previewCheck(n) })), [numbers]);
  const previewProblems = preview.filter((p) => !p.ok).length;
  const invalidLines = useMemo(
    () => (result ? result.lines.filter((l) => l.status === "Invalid") : []),
    [result],
  );

  const canSubmit = !busy && numbers.length > 0 && finalGroup.length > 0;

  async function submit() {
    if (!canSubmit) return;
    setBusy(true);
    setError(null);
    try {
      const r = await api.addManualContacts(numbers, finalGroup);
      setResult(r);
      setShowDetails(false);
      onSaved(r);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
    }
  }

  // Limpa pra colar outro lote sem fechar (mantém o grupo escolhido).
  function addMore() {
    setText("");
    setResult(null);
    setError(null);
    setShowDetails(false);
  }

  async function copyInvalids() {
    try {
      await navigator.clipboard.writeText(invalidLines.map((l) => l.input).join("\n"));
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setError("Não foi possível copiar pra área de transferência.");
    }
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-dialog manual-modal" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
        <h3>Adicionar números</h3>
        <div className="modal-body">
          {/* Destino: escolher uma lista existente ou criar um lote nomeado. */}
          <div className="manual-group-row">
            <label htmlFor="manual-group">Salvar na lista</label>
            <select
              id="manual-group"
              value={groupChoice}
              onChange={(e) => setGroupChoice(e.target.value)}
              disabled={busy}
            >
              {groupOptions.map((g) => (
                <option key={g} value={g}>
                  {g}
                </option>
              ))}
              <option value={NEW_GROUP}>Criar nova lista…</option>
            </select>
            {groupChoice === NEW_GROUP && (
              <input
                type="text"
                className="manual-group-new"
                value={newGroup}
                onChange={(e) => setNewGroup(e.target.value)}
                placeholder="Nome da nova lista"
                disabled={busy}
                autoFocus
              />
            )}
          </div>
          <p className="muted tiny">
            Só um rótulo pra organizar e poder segmentar o disparo. Não cria grupo no WhatsApp e não
            avisa ninguém. Para isso existe a aba Grupos.
          </p>

          <p className="muted small">
            Cole ou digite os números, um por linha (ou separados por vírgula). O DDD é obrigatório;
            o 55 é adicionado automaticamente e o 9º dígito é inserido quando faltar.
          </p>
          <textarea
            className="manual-numbers-input"
            rows={8}
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder={"11987654321\n(21) 99888-7777\n+55 31 98888-1234"}
            disabled={busy}
          />
          {/* Prévia: lista os números um abaixo do outro e marca os com problema ANTES de enviar. */}
          {numbers.length > 0 && !result && (
            <div className="manual-preview">
              <p className="muted small">
                {numbers.length} detectado(s)
                {previewProblems > 0 && (
                  <>
                    {" · "}
                    <span className="manual-preview-warn">{previewProblems} com problema</span>
                  </>
                )}{" "}
                → grupo <strong>{finalGroup || "—"}</strong>
              </p>
              <ul className="manual-result-list">
                {preview.slice(0, PREVIEW_CAP).map((p, i) => (
                  <li key={i} className={p.ok ? undefined : "manual-line-invalid"}>
                    <span className={`manual-dot ${p.ok ? "dot-ok" : "dot-invalid"}`} />
                    <span className="mono">{p.input}</span>
                    {!p.ok && <span className="manual-line-detail">{p.reason}</span>}
                  </li>
                ))}
              </ul>
              {preview.length > PREVIEW_CAP && (
                <p className="muted tiny">
                  …e mais {preview.length - PREVIEW_CAP} (a validação completa roda ao Adicionar).
                </p>
              )}
            </div>
          )}

          {error && <p className="error">{error}</p>}

          {result && (
            <div className="manual-result">
              {/* Resumo em chips — escala pra 1000 sem virar uma lista gigante. */}
              <div className="manual-chips">
                <span className="manual-chip chip-added">{result.added} adicionados</span>
                <span className="manual-chip chip-corrected">{result.corrected} corrigidos</span>
                <span className="manual-chip chip-dup">{result.duplicated} já existiam</span>
                <span className="manual-chip chip-invalid">{result.invalid} inválidos</span>
              </div>

              {/* Inválidos em destaque, com copiar pra corrigir e colar de novo. */}
              {invalidLines.length > 0 && (
                <div className="manual-invalids">
                  <div className="manual-invalids-head">
                    <strong>Inválidos — corrija e cole de novo</strong>
                    <button type="button" className="btn-ghost small" onClick={() => void copyInvalids()}>
                      {copied ? "Copiado!" : "Copiar"}
                    </button>
                  </div>
                  <ul>
                    {invalidLines.map((l, i) => (
                      <li key={i}>
                        <span className="mono">{l.input}</span> — {l.reason}
                      </li>
                    ))}
                  </ul>
                </div>
              )}

              {/* Detalhe linha a linha fica recolhido por padrão. */}
              <button
                type="button"
                className="manual-details-toggle"
                onClick={() => setShowDetails((v) => !v)}
              >
                {showDetails ? "▾" : "▸"} Ver detalhes das {result.lines.length} linha(s)
              </button>
              {showDetails && (
                <ul className="manual-result-list">
                  {result.lines.map((line, i) => {
                    const meta = STATUS_META[line.status];
                    return (
                      <li key={i} className={meta.className}>
                        <span className={`manual-dot ${meta.dot}`} />
                        <span className="mono">{line.input}</span>
                        {line.phone && line.phone !== line.input && (
                          <span className="manual-line-detail mono">→ {line.phone}</span>
                        )}
                        <span className="manual-line-detail">
                          {line.status === "Corrected"
                            ? `${meta.label} (${line.correction})`
                            : line.status === "Invalid"
                              ? `${meta.label}: ${line.reason}`
                              : meta.label}
                        </span>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>
          )}
        </div>
        <div className="modal-actions">
          <button type="button" className="btn-ghost" onClick={onClose}>
            {result ? "Fechar" : "Cancelar"}
          </button>
          {result ? (
            <button type="button" onClick={addMore}>
              Adicionar mais
            </button>
          ) : (
            <button type="button" onClick={() => void submit()} disabled={!canSubmit}>
              {busy ? "Adicionando..." : "Adicionar"}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
