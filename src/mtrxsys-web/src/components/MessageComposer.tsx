import { useState, type FormEvent, type KeyboardEvent } from "react";

interface Props {
  onSend: (text: string) => Promise<void>;
}

export function MessageComposer({ onSend }: Props) {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    const trimmed = text.trim();
    if (!trimmed) return;
    setBusy(true);
    setError(null);
    try {
      await onSend(trimmed);
      setText("");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
    }
  }

  function onSubmit(e: FormEvent) {
    e.preventDefault();
    void submit();
  }

  function onKey(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  }

  return (
    <form className="composer" onSubmit={onSubmit}>
      {error && <p className="error">{error}</p>}
      <textarea
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKey}
        placeholder="Digite uma mensagem (Enter envia, Shift+Enter quebra linha)"
        disabled={busy}
        rows={2}
      />
      <button type="submit" disabled={busy || !text.trim()}>
        {busy ? "..." : "Enviar"}
      </button>
    </form>
  );
}
