import { useEffect, useMemo, useState } from "react";
import { api } from "../api/client";

// Aba "Guia Google": passo a passo pra configurar o sync de contatos (anti-463), do Console ao
// disparo. É só documentação/checklist (não faz chamadas). O progresso fica salvo no localStorage
// pra o operador poder fechar e voltar. Espelha docs/google-contacts-sync.md.

const STORAGE_KEY = "gsync.guide.checks.v1";

type NoteKind = "info" | "warn";

interface GuideStep {
  title: string;
  sub: string;
  code?: string; // bloco de config, quando o passo tem um (renderiza antes da lista)
  items: { id: string; node: React.ReactNode }[];
  note?: { kind: NoteKind; node: React.ReactNode };
}

// Conteúdo do guia como DADO, não como JSX repetido: a numeração e a lista de ids marcáveis saem
// daqui por derivação. Antes havia um ALL_IDS escrito à mão que precisava ser mantido em sincronia
// com os <Check> — esquecer um id não quebrava nada, só fazia a contagem de progresso mentir.
// Os ids são chave do localStorage: não renomeie (zera o progresso de quem já preencheu).
const STEPS: GuideStep[] = [
  {
    title: "No Console: registrar o app (uma vez, vale pros 10)",
    sub: "console.cloud.google.com · use a conta que já estiver logada",
    items: [
      { id: "a1", node: <>Menu <code>APIs e serviços → Biblioteca</code>, buscar <b>People API</b> → <b>Ativar</b>.</> },
      { id: "a2", node: <><code>APIs e serviços → Tela de permissão OAuth</code> → <b>Começar</b> → User type <b>Externo</b>.</> },
      { id: "a3", node: <>Preencher nome do app + e-mail de suporte + e-mail do dev → <b>Criar</b>.</> },
      { id: "a4", node: <>Em <b>Acesso a dados</b> → Adicionar escopos → colar <code>.../auth/contacts</code> (o de escrita, sem <code>.readonly</code>) → <b>Salvar</b>.</> },
      { id: "a5", node: <>Em <b>Público-alvo</b> → Usuários de teste → <b>Add users</b> → digitar os <b>e-mails dos chips</b> (só digitar, sem login) → Salvar.</> },
      { id: "a6", node: <>Em <b>Clientes</b> → <b>+ Criar cliente</b> → tipo <b>Aplicativo da Web</b>.</> },
      { id: "a7", node: <>Em <b>URIs de redirecionamento autorizados</b>, adicionar <code>https://developers.google.com/oauthplayground</code> → Criar.</> },
      { id: "a8", node: <><b>Copiar e guardar</b> o <b>Client ID</b> e o <b>Client secret</b> (valem pros 10 stacks).</> },
    ],
    note: {
      kind: "info",
      node: <>A <b>People API é gratuita</b>. Ignore o banner dos US$ 300, não precisa cartão. E use o cliente <b>"Aplicativo da Web"</b> (não "App para computador"): o Playground só funciona com a URL de redirecionamento acima.</>,
    },
  },
  {
    title: "Refresh token do chip (OAuth Playground)",
    sub: "uma vez por chip · é aqui (e só aqui) que a conta do chip entra",
    items: [
      { id: "b1", node: <>Abrir uma <b>janela anônima</b> (Ctrl+Shift+N) → <code>developers.google.com/oauthplayground</code>.</> },
      { id: "b2", node: <><b>Engrenagem</b> (canto sup. direito) → marcar <b>"Use your own OAuth credentials"</b> → colar o <b>Client ID + Secret do cliente Web</b>.</> },
      { id: "b3", node: <>No campo <b>"Input your own scopes"</b>, colar <code>https://www.googleapis.com/auth/contacts</code> → <b>Authorize APIs</b>.</> },
      { id: "b4", node: <>Login <b>com a conta Google do chip</b> (a anônima não tem sua conta do Chrome). Senha + 2FA no celular se pedir.</> },
      { id: "b5", node: <>Tela "app não verificado"/consentimento → <b>Continue</b> → <b>Allow</b> (é normal, você é o dev + a conta é usuária de teste).</> },
      { id: "b6", node: <>Step 2 → <b>Exchange authorization code for tokens</b> → copiar o <b>Refresh token</b> (começa com <code>1//…</code>).</> },
    ],
    note: {
      kind: "warn",
      node: <><b>Não pule a engrenagem (b2).</b> Se autorizar sem colar SUAS credenciais, o refresh token nasce amarrado ao cliente do Google e <b>não funciona</b> na config. O <code>client_id</code> na aba "Request/Response" tem que ser o SEU.</>,
    },
  },
  {
    title: "Colar na config do stack",
    sub: "Client ID/Secret iguais pros 10 · o RefreshToken muda por stack",
    code: `# variáveis de ambiente do stack (cada stack usa o token do SEU chip)
AddressBookSync__Provider=Google
AddressBookSync__GraceSeconds=180
AddressBookSync__Google__ClientId=<compartilhado>
AddressBookSync__Google__ClientSecret=<compartilhado>
AddressBookSync__Google__RefreshToken=<token DESTE chip>`,
    items: [
      { id: "c1", node: <>Colar as variáveis no ambiente do stack (com o RefreshToken do chip dele) → reiniciar o <b>dispatcher</b> do stack.</> },
    ],
    note: {
      kind: "info",
      node: <>Não precisa de <code>Enabled</code>: assim que houver <code>Provider=Google</code> + o RefreshToken, a defesa <b>liga sozinha</b>. Desligar de propósito = <code>Provider=None</code> ou remover o token — nunca mais fica apagada por esquecimento.</>,
    },
  },
  {
    title: "Validar no stack A antes de escalar",
    sub: "provar que mata o 463 com 1 chip antes de fazer os 10",
    items: [
      { id: "d1", node: <>Ligar a config só no <b>A</b> com o token do chip do A.</> },
      { id: "d2", node: <>Disparar pra <b>1 contato frio</b> (não-respondedor) e aguardar o grace + envio.</> },
      { id: "d3", node: <>Conferir: a mensagem <b>entregou (ack ≥ 2)</b> e a sessão <b>NÃO caiu</b> (sem 463 no log do WAHA)?</> },
      { id: "d4", node: <>Deu certo → repetir a Parte 2 pros outros 9 chips. Não deu → o caminho é emulador-primary ou sair do grátis.</> },
    ],
    note: {
      kind: "warn",
      node: <>Token em modo <b>"Testing" expira em 7 dias</b>, ótimo pra validar. Pra produção contínua, publicar o consent em "In production" (escopo sensível em Gmail pessoal pode exigir verificação do Google).</>,
    },
  },
];

const ALL_IDS = STEPS.flatMap((s) => s.items.map((i) => i.id));

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

  // Estado AO VIVO da defesa neste stack (o selo). Foi a invisibilidade disso que deixou o chip do A
  // rodar com o sync apagado. active = provider Google ligado (Provider=Google + token → auto-liga).
  const [sync, setSync] = useState<{ active: boolean; provider: string; tokenPresent: boolean } | null>(null);
  useEffect(() => {
    let alive = true;
    api.gsyncStatus().then((s) => { if (alive) setSync(s); }).catch(() => { if (alive) setSync(null); });
    return () => { alive = false; };
  }, []);
  // 3 estados: ATIVA (verde), sem token (cinza — falta configurar), Provider=None (cinza — desligado).
  const syncBadge = sync === null
    ? { cls: "gsg-badge", text: "Sincronização Google: verificando…" }
    : sync.active
      ? { cls: "gsg-badge is-on", text: "Sincronização Google: ATIVA" }
      : !sync.tokenPresent
        ? { cls: "gsg-badge is-off", text: "Sincronização Google: desligada (falta o token deste chip)" }
        : { cls: "gsg-badge is-off", text: "Sincronização Google: desligada (Provider ≠ Google)" };

  return (
    <main className="gsg-wrap">
      <header className="gsg-hero">
        <span className="gsg-eyebrow">Configuração anti-463</span>
        <h1>Sincronizar agenda (Google)</h1>
        <span className={syncBadge.cls}>{syncBadge.text}</span>
        <p className="gsg-lede">
          Salvar o contato <b>frio</b> na conta Google do chip <b>antes</b> de disparar, pro aparelho
          primário sincronizar e o WhatsApp herdar o <b>tctoken</b>. É o que mata o erro 463 que derruba
          a sessão. Opcional e vem <b>desligado</b>; siga isto só quando quiser ligar.
        </p>

        <div className="gsg-roles">
          <div className="gsg-role">
            <h3>Conta do <span>Console</span></h3>
            <p>Dona do <b>app</b>. Registra a API e guarda o Client ID/Secret. <b>Qualquer conta sua</b>, não importa o e-mail.</p>
          </div>
          <div className="gsg-role">
            <h3>Conta do <span>chip</span></h3>
            <p>Dona da <b>agenda</b> onde os contatos entram. Aparece <b>só no consentimento</b> (Parte 2), no próprio celular. Uma por stack.</p>
          </div>
        </div>

        <p className="gsg-progress">
          <span><b>{done}</b> de <b>{ALL_IDS.length}</b> passos concluídos</span>
          <button type="button" className="gsg-reset" onClick={reset}>limpar</button>
        </p>
      </header>

      {STEPS.map((step, i) => (
        <section className="gsg-step" key={step.title}>
          <div className="gsg-step-head">
            <span className="gsg-num">{i + 1}</span>
            <div>
              <h2>{step.title}</h2>
              <p className="gsg-sub">{step.sub}</p>
            </div>
          </div>
          {step.code && <pre className="gsg-code">{step.code}</pre>}
          <ul className="gsg-list">
            {step.items.map((item) => (
              <li className="gsg-check" key={item.id}>
                <input
                  type="checkbox"
                  id={`gsg-${item.id}`}
                  checked={!!checks[item.id]}
                  onChange={() => toggle(item.id)}
                />
                <label htmlFor={`gsg-${item.id}`}>{item.node}</label>
              </li>
            ))}
          </ul>
          {step.note && <div className={`gsg-callout gsg-${step.note.kind}`}>{step.note.node}</div>}
        </section>
      ))}

      <footer className="gsg-foot">
        <b>Automático depois de configurado:</b> no disparo, todo contato frio é salvo na agenda → o job
        espera o grace → envia no ciclo seguinte. <b>Manual (uma vez):</b> só a Parte 1 (Console) e o
        "sim" do consentimento de cada chip. Doc no repo: <code>docs/google-contacts-sync.md</code>.
      </footer>
    </main>
  );
}
