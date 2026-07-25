import { useState } from "react";
import { api } from "../api/client";
import type { Group } from "../api/types";
import { ConfirmDialog } from "./ConfirmDialog";
import { GroupImportPanel } from "./GroupImportPanel";

export function GroupsScreen() {
  // Grupo aguardando confirmação de saída (abre o modal); e o id em processo de saída (trava o botão).
  const [confirmLeave, setConfirmLeave] = useState<Group | null>(null);
  const [leavingId, setLeavingId] = useState<string | null>(null);
  const [leaveError, setLeaveError] = useState<string | null>(null);
  // Bump pra forçar o painel a re-buscar a lista após uma saída (o grupo some da lista da WAHA).
  const [reloadSignal, setReloadSignal] = useState(0);

  // Sai do grupo de verdade (número conectado deixa o grupo). Confirmado pelo modal. Em sucesso, força o
  // painel a re-buscar; o backend é tolerante a grupo-fantasma (sempre "saiu" do ponto do usuário).
  async function leaveGroup(group: Group) {
    // Fecha o modal JÁ na confirmação: o leave do WAHA pode demorar (até o timeout de 60s) e, se o modal
    // ficasse aberto, daria pra clicar "Sim, sair" de novo e disparar leaves duplicados.
    setConfirmLeave(null);
    setLeavingId(group.id);
    setLeaveError(null);
    try {
      await api.leaveGroup(group.id);
      setReloadSignal((k) => k + 1); // re-busca — o grupo já não aparece
    } catch (ex) {
      // Mostra o erro E re-busca: o estado real da WAHA é a fonte da verdade (o grupo pode ter saído
      // mesmo com erro; se não saiu, o refresh o mantém na lista e a mensagem explica o porquê).
      setLeaveError(
        `Não consegui sair de "${group.name || "(sem nome)"}": ${ex instanceof Error ? ex.message : String(ex)}`,
      );
      setReloadSignal((k) => k + 1);
    } finally {
      setLeavingId(null);
    }
  }

  return (
    <main className="groups-screen">
      <header className="groups-header">
        <div className="groups-header-row">
          <h2>Grupos</h2>
        </div>
        <p className="muted">
          Grupos que o WhatsApp logado participa. Importe os participantes pra cadastrá-los como contatos no CRM.
          Entrou gente nova depois? Clique <strong>Importar</strong> de novo: só os novos são adicionados
          (os já cadastrados são pulados como duplicados).
        </p>
      </header>

      {leaveError && <p className="error">{leaveError}</p>}

      <GroupImportPanel
        reloadSignal={reloadSignal}
        extraActions={(group) => (
          <button
            type="button"
            onClick={() => setConfirmLeave(group)}
            disabled={leavingId === group.id}
            className="leave-btn"
            title="Faz o número conectado sair deste grupo"
          >
            {leavingId === group.id ? "Saindo..." : "Sair"}
          </button>
        )}
      />

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
