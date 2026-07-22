import { useEffect, useMemo, useState } from "react";

// Aba "Guia Google": passo a passo pra configurar o sync de contatos (anti-463) — do Console ao
// disparo. É só documentação/checklist (não faz chamadas). O progresso fica salvo no localStorage
// pra o operador poder fechar e voltar. Espelha docs/google-contacts-sync.md.

const STORAGE_KEY = "gsync.guide.checks.v1";

// Ids de TODOS os itens marcáveis (pra contar o progresso). Manter em sincronia com os <Check> abaixo.
const ALL_IDS = [
  "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8",
  "b1", "b2", "b3", "b4", "b5", "b6",
  "c1",
  "d1", "d2", "d3", "d4",
] as const;

export function GoogleSyncGuideScreen() {
  const [checks, setChecks] = useState<Record<string, boolean>>(() => {
    try {
      return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}") as Record<string, boolean>;
    } catch {
      return {};
    }
  });

  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(checks));
  }, [checks]);

  const toggle = (id: string) => setChecks((c) => ({ ...c, [id]: !c[id] }));
  const reset = () => setChecks({});
  const done = useMemo(() => ALL_IDS.filter((id) => checks[id]).length, [checks]);

  const check = (id: string, node: React.ReactNode) => (
    <li className="gsg-check">
      <input type="checkbox" id={`gsg-${id}`} checked={!!checks[id]} onChange={() => toggle(id)} />
      <label htmlFor={`gsg-${id}`}>{node}</label>
    </li>
  );

  return (
    <main className="gsg-wrap">
      <header className="gsg-hero">
        <span className="gsg-eyebrow">📇 Configuração · anti-463</span>
        <h1>Sincronizar agenda (Google) — passo a passo</h1>
        <p className="gsg-lede">
          Salvar o contato <b>frio</b> na conta Google do chip <b>antes</b> de disparar, pro aparelho
          primário sincronizar e o WhatsApp herdar o <b>tctoken</b> — matando o erro 463 que derruba a
          sessão. É opcional e vem <b>desligado</b>; siga isto só quando quiser ligar.
        </p>

        <div className="gsg-roles">
          <div className="gsg-role">
            <h3>🖥️ Conta do <span>Console</span></h3>
            <p>Dona do <b>app</b>. Registra a API e guarda o Client ID/Secret. <b>Qualquer conta sua</b> — não importa o e-mail.</p>
          </div>
          <div className="gsg-role">
            <h3>📱 Conta do <span>chip</span></h3>
            <p>Dona da <b>agenda</b> onde os contatos entram. Aparece <b>só no consentimento</b> (Parte 2), no próprio celular. Uma por stack.</p>
          </div>
        </div>

        <p className="gsg-progress">
          <b>{done}</b> de <b>{ALL_IDS.length}</b> passos concluídos
          <button type="button" className="gsg-reset" onClick={reset}>limpar</button>
        </p>
      </header>

      {/* PARTE 1 */}
      <section className="gsg-step">
        <div className="gsg-step-head">
          <span className="gsg-num">1</span>
          <div>
            <h2>No Console — registrar o app (uma vez, vale pros 10)</h2>
            <p className="gsg-sub">console.cloud.google.com · use a conta que já estiver logada</p>
          </div>
        </div>
        <ul className="gsg-list">
          {check("a1", <>Menu <code>☰ → APIs e serviços → Biblioteca</code>, buscar <b>People API</b> → <b>Ativar</b>.</>)}
          {check("a2", <><code>APIs e serviços → Tela de permissão OAuth</code> → <b>Começar</b> → User type <b>Externo</b>.</>)}
          {check("a3", <>Preencher nome do app + e-mail de suporte + e-mail do dev → <b>Criar</b>.</>)}
          {check("a4", <>Em <b>Acesso a dados</b> → Adicionar escopos → colar <code>.../auth/contacts</code> (o de escrita, sem <code>.readonly</code>) → <b>Salvar</b>.</>)}
          {check("a5", <>Em <b>Público-alvo</b> → Usuários de teste → <b>Add users</b> → digitar os <b>e-mails dos chips</b> (só digitar, sem login) → Salvar.</>)}
          {check("a6", <>Em <b>Clientes</b> → <b>+ Criar cliente</b> → tipo <b>Aplicativo da Web</b>.</>)}
          {check("a7", <>Em <b>URIs de redirecionamento autorizados</b>, adicionar <code>https://developers.google.com/oauthplayground</code> → Criar.</>)}
          {check("a8", <><b>Copiar e guardar</b> o <b>Client ID</b> e o <b>Client secret</b> (valem pros 10 stacks).</>)}
        </ul>
        <div className="gsg-callout gsg-info">
          <span>💳</span>
          <span>A <b>People API é gratuita</b> — ignore o banner dos US$ 300, não precisa cartão. E use o cliente <b>"Aplicativo da Web"</b> (não "App para computador"): o Playground só funciona com a URL de redirecionamento acima.</span>
        </div>
      </section>

      {/* PARTE 2 */}
      <section className="gsg-step">
        <div className="gsg-step-head">
          <span className="gsg-num">2</span>
          <div>
            <h2>Refresh token do chip — no OAuth Playground</h2>
            <p className="gsg-sub">uma vez por chip · é aqui (e só aqui) que a conta do chip entra</p>
          </div>
        </div>
        <ul className="gsg-list">
          {check("b1", <>Abrir uma <b>janela anônima</b> (Ctrl+Shift+N) → <code>developers.google.com/oauthplayground</code>.</>)}
          {check("b2", <><b>⚙ engrenagem</b> (canto sup. direito) → marcar <b>"Use your own OAuth credentials"</b> → colar o <b>Client ID + Secret do cliente Web</b>.</>)}
          {check("b3", <>No campo <b>"Input your own scopes"</b>, colar <code>https://www.googleapis.com/auth/contacts</code> → <b>Authorize APIs</b>.</>)}
          {check("b4", <>Login <b>com a conta Google do chip</b> (a anônima não tem sua conta do Chrome) — senha + 2FA no celular se pedir.</>)}
          {check("b5", <>Tela "app não verificado"/consentimento → <b>Continue</b> → <b>Allow</b> (é normal, você é o dev + a conta é usuária de teste).</>)}
          {check("b6", <>Step 2 → <b>Exchange authorization code for tokens</b> → copiar o <b>Refresh token</b> (começa com <code>1//…</code>).</>)}
        </ul>
        <div className="gsg-callout gsg-warn">
          <span>🔑</span>
          <span><b>Não pule a engrenagem (b2).</b> Se autorizar sem colar SUAS credenciais, o refresh token nasce amarrado ao cliente do Google e <b>não funciona</b> na config. O <code>client_id</code> na aba "Request/Response" tem que ser o SEU.</span>
        </div>
      </section>

      {/* PARTE 3 */}
      <section className="gsg-step">
        <div className="gsg-step-head">
          <span className="gsg-num">3</span>
          <div>
            <h2>Colar na config do stack</h2>
            <p className="gsg-sub">Client ID/Secret iguais pros 10 · o RefreshToken muda por stack</p>
          </div>
        </div>
        <pre className="gsg-code">{`# variáveis de ambiente do stack (cada stack usa o token do SEU chip)
AddressBookSync__Enabled=true
AddressBookSync__Provider=Google
AddressBookSync__GraceSeconds=180
AddressBookSync__Google__ClientId=<compartilhado>
AddressBookSync__Google__ClientSecret=<compartilhado>
AddressBookSync__Google__RefreshToken=<token DESTE chip>`}</pre>
        <ul className="gsg-list">
          {check("c1", <>Colar as variáveis no ambiente do stack (com o RefreshToken do chip dele) → reiniciar o <b>dispatcher</b> do stack.</>)}
        </ul>
        <div className="gsg-callout gsg-info">
          <span>🔌</span>
          <span>Com <code>Enabled=false</code> (o padrão) nada muda — o pipeline fica no-op. Ligar é só preencher isto e reiniciar.</span>
        </div>
      </section>

      {/* PARTE 4 */}
      <section className="gsg-step">
        <div className="gsg-step-head">
          <span className="gsg-num">4</span>
          <div>
            <h2>Validar no stack A antes de escalar</h2>
            <p className="gsg-sub">provar que mata o 463 com 1 chip antes de fazer os 10</p>
          </div>
        </div>
        <ul className="gsg-list">
          {check("d1", <>Ligar a config só no <b>A</b> com o token do chip do A.</>)}
          {check("d2", <>Disparar pra <b>1 contato frio</b> (não-respondedor) e aguardar o grace + envio.</>)}
          {check("d3", <>Conferir: a mensagem <b>entregou (ack ≥ 2)</b> e a sessão <b>NÃO caiu</b> (sem 463 no log do WAHA)?</>)}
          {check("d4", <>Deu certo → repetir a Parte 2 pros outros 9 chips. Não deu → o caminho é emulador-primary ou sair do grátis.</>)}
        </ul>
        <div className="gsg-callout gsg-warn">
          <span>⏳</span>
          <span>Token em modo <b>"Testing" expira em 7 dias</b> — ótimo pra validar. Pra produção contínua, publicar o consent em "In production" (escopo sensível em Gmail pessoal pode exigir verificação do Google).</span>
        </div>
      </section>

      <footer className="gsg-foot">
        <b>Automático depois de configurado:</b> no disparo, todo contato frio é salvo na agenda → o job
        espera o grace → envia no ciclo seguinte. <b>Manual (uma vez):</b> só a Parte 1 (Console) e o
        "sim" do consentimento de cada chip. Doc no repo: <code>docs/google-contacts-sync.md</code>.
      </footer>
    </main>
  );
}
