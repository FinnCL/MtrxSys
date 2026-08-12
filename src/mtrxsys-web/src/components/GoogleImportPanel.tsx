import { useState } from "react";
import { api } from "../api/client";
import type { GooglePreview } from "../api/types";

// Painel "Importar da agenda Google": mostra a DIFERENÇA entre a conta Google do chip e o banco, e
// grava só o que o operador marcar.
//
// Por que não é automático: o disparo escreve nessa agenda antes de cada envio frio. Uma varredura
// periódica traria gente pra dentro sem ninguém decidir, e o sistema estaria lendo o próprio rastro.
// Importar é decisão; disparar é execução. Misturar as duas transforma um clique em consequência que
// ninguém revisou.
//
// O botão fica SEMPRE visível, mesmo sem novidade. Botão que some é pior que botão inativo: você não
// sabe se não há contato novo ou se algo quebrou.
export function GoogleImportPanel({ onImported }: { onImported: () => void }) {
  const [preview, setPreview] = useState<GooglePreview | null>(null);
  const [marcados, setMarcados] = useState<Set<string>>(new Set());
  const [carregando, setCarregando] = useState(false);
  const [importando, setImportando] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);

  async function conferir() {
    setCarregando(true);
    setMsg(null);
    try {
      const p = await api.googlePreview();
      setPreview(p);
      // Nada vem pré-marcado: importar é decisão explícita, e "marcar todos" por padrão faria o
      // clique seguinte trazer gente que ninguém olhou.
      setMarcados(new Set());
    } catch (ex) {
      setMsg(`Erro ao conferir: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setCarregando(false);
    }
  }

  async function importar() {
    if (marcados.size === 0) return;
    setImportando(true);
    setMsg(null);
    try {
      const r = await api.googleImport([...marcados]);
      // A conta tem que FECHAR contra o que foi marcado. Duas parcelas ficavam de fora e o operador
      // via "8 importados" de 10 sem explicação pros outros 2:
      //  - barrados: descartados ou com opt-out. A API reconfere isso no clique, porque entre abrir a
      //    prévia e importar alguém pode ter descartado o contato em outra aba.
      //  - corrigidos: entraram, mas com o número alterado (9º dígito). Já estão dentro de "added";
      //    aparecem à parte porque o operador precisa saber que o número gravado não é o que ele viu.
      const partes = [
        `${r.added} importado(s)`,
        `${r.duplicated} já existiam`,
        `${r.invalid} inválido(s)`,
      ];
      if (r.barrados) partes.push(`${r.barrados} barrado(s) por descarte ou opt-out`);
      if (r.corrected) partes.push(`${r.corrected} com 9º dígito inserido`);
      setMsg(`${partes.join(", ")}.`);
      setPreview(null);
      setMarcados(new Set());
      onImported();
    } catch (ex) {
      setMsg(`Erro ao importar: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setImportando(false);
    }
  }

  const alternar = (phone: string) =>
    setMarcados((s) => {
      const n = new Set(s);
      if (!n.delete(phone)) n.add(phone);
      return n;
    });

  return (
    <section className="google-import">
      <div className="google-import-head">
        <div>
          <h3>Importar da agenda Google</h3>
          <p className="muted small">
            Contatos criados em <code>contacts.google.com</code> não entram aqui sozinhos. Confira a
            diferença e escolha quais quer trazer.
          </p>
        </div>
        <button type="button" onClick={() => void conferir()} disabled={carregando || importando}>
          {carregando ? "Conferindo…" : "Conferir agenda"}
        </button>
      </div>

      {msg && <p className="muted small">{msg}</p>}

      {preview?.estado === "desligado" && (
        <p className="muted small">
          Sincronização Google desligada neste stack. Configure na aba <b>Guia Google</b>.
        </p>
      )}

      {/* Estado próprio, e não "0 novos": pane de leitura não pode produzir a mesma tela de "está
          tudo igual", senão o operador conclui que não há o que importar e nunca descobre que a
          pergunta não chegou ao Google. */}
      {preview?.estado === "ilegivel" && (
        <p className="error">
          Não consegui ler a agenda da conta Google. <b>Isto não quer dizer que não há contatos
          novos</b> — quer dizer que a pergunta não foi respondida. Verifique o token na aba{" "}
          <b>Guia Google</b>.
        </p>
      )}

      {preview?.estado === "ok" && (
        <div className="google-import-body">
          <p className="muted small">
            {preview.naConta} na conta · {preview.jaNoSistema} já no sistema ·{" "}
            <b>{preview.novos.length} novo(s)</b>
            {preview.bloqueados > 0 && ` · ${preview.bloqueados} descartado(s)/opt-out`}
            {preview.invalidos.length > 0 && ` · ${preview.invalidos.length} fora do padrão`}
          </p>

          {preview.bloqueados > 0 && (
            <p className="muted small">
              Os {preview.bloqueados} descartados ou com opt-out não são oferecidos. Quem pediu para
              sair não volta por importação.
            </p>
          )}

          {preview.novos.length === 0 && preview.invalidos.length === 0 && (
            <p className="muted small">Nenhum contato novo. A conta e o sistema estão iguais.</p>
          )}

          {preview.novos.length > 0 && (
            <>
              <ul className="google-import-list">
                {preview.novos.map((c) => (
                  <li key={c.phone}>
                    <label>
                      <input
                        type="checkbox"
                        checked={marcados.has(c.phone)}
                        onChange={() => alternar(c.phone)}
                      />
                      <span>{c.phone}</span>
                      {c.name && <span className="muted small"> · {c.name}</span>}
                    </label>
                  </li>
                ))}
              </ul>
              <div className="google-import-actions">
                <button
                  type="button"
                  onClick={() => setMarcados(new Set(preview.novos.map((c) => c.phone)))}
                  disabled={importando}
                >
                  Marcar todos
                </button>
                <button
                  type="button"
                  className="primary"
                  onClick={() => void importar()}
                  disabled={marcados.size === 0 || importando}
                >
                  {importando ? "Importando…" : `Importar ${marcados.size} selecionado(s)`}
                </button>
              </div>
              <p className="muted small">
                Os importados entram com o chip conectado como dono, então já podem ser disparados.
              </p>
            </>
          )}

          {preview.invalidos.length > 0 && (
            // Mostrados, não escondidos: sumir com eles faria a soma não fechar contra o total da
            // conta, e o operador ficaria procurando o que faltou.
            <details className="google-import-invalid">
              <summary>{preview.invalidos.length} fora do padrão (não importáveis)</summary>
              <ul>
                {preview.invalidos.map((c) => (
                  <li key={c.phone}>
                    <code>{c.phone}</code> <span className="muted small">— {c.motivo}</span>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </div>
      )}
    </section>
  );
}
