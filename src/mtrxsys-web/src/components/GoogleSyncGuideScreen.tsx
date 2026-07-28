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
    title: "1 · Registrar o app no Google",
    sub: "uma vez só · vale pros 10 stacks · console.cloud.google.com",
    items: [
      { id: "a1", node: <>Ativar a <b>People API</b> em <code>APIs e serviços → Biblioteca</code>.</> },
      { id: "a2", node: <>Criar a tela de permissão OAuth com User type <b>Externo</b>.</> },
      { id: "a3", node: <>Preencher nome do app, e-mail de suporte e e-mail do dev.</> },
      { id: "a4", node: <>Adicionar o escopo <code>.../auth/contacts</code> — o de escrita, <b>sem</b> <code>.readonly</code>.</> },
      { id: "a9", node: <>Em <b>Público</b>, clicar em <b>Publicar aplicativo</b>.</> },
      { id: "a6", node: <>Criar um cliente do tipo <b>Aplicativo da Web</b>.</> },
      { id: "a7", node: <>Nos redirecionamentos, adicionar <code>https://developers.google.com/oauthplayground</code>.</> },
      { id: "a8", node: <>Guardar o <b>Client ID</b> e o <b>Client secret</b>.</> },
    ],
    note: {
      kind: "warn",
      node: <>Publicar (a9) é o que evita o token morrer a cada 7 dias. É grátis e <b>não</b> exige mandar o app pra verificação.</>,
    },
  },
  {
    title: "2 · Gerar o refresh token do chip",
    sub: "uma vez por chip · developers.google.com/oauthplayground",
    items: [
      { id: "b1", node: <>Abrir o Playground numa <b>janela anônima</b>.</> },
      { id: "b2", node: <>Engrenagem → marcar <b>Use your own OAuth credentials</b> → colar Client ID e Secret.</> },
      { id: "b7", node: <>Ainda na engrenagem: <b>Access type = Offline</b> e <b>Force prompt = Consent screen</b>.</> },
      { id: "b3", node: <>Em <b>Input your own scopes</b>, colar <code>https://www.googleapis.com/auth/contacts</code> → <b>Authorize APIs</b>.</> },
      { id: "b4", node: <>Entrar com a <b>conta Google do chip</b>.</> },
      { id: "b5", node: <>Tela "app não verificado" → <b>Avançado</b> → acessar mesmo assim.</> },
      { id: "b6", node: <>Step 2 → <b>Exchange authorization code for tokens</b> → copiar o token que começa com <code>1//</code>.</> },
    ],
    note: {
      kind: "warn",
      node: <>Os dois passos que mais causam retrabalho: sem <b>b2</b> o token nasce amarrado ao cliente do Google e não funciona; sem <b>b7</b> o Google devolve a autorização <b>sem</b> refresh token.</>,
    },
  },
  {
    title: "3 · Colar na config do stack",
    sub: "Client ID e Secret iguais pros 10 · o token muda por chip",
    code: `AddressBookSync__Provider=Google
AddressBookSync__GraceSeconds=180
AddressBookSync__Google__ClientId=<compartilhado>
AddressBookSync__Google__ClientSecret=<compartilhado>
AddressBookSync__Google__RefreshToken=<token DESTE chip>`,
    items: [
      { id: "c1", node: <>Colar as variáveis e <b>reiniciar o dispatcher</b> — o token é lido só na inicialização.</> },
    ],
    note: {
      kind: "info",
      node: <>Não existe <code>Enabled</code>. Com <code>Provider=Google</code> e um token, a defesa liga sozinha. Pra desligar: <code>Provider=None</code>.</>,
    },
  },
  {
    title: "4 · Validar antes de escalar",
    sub: "provar com 1 chip antes de repetir nos 10",
    items: [
      { id: "d1", node: <>Ligar só no stack A, com o token do chip dele.</> },
      { id: "d2", node: <>Disparar pra <b>1 contato frio</b> e aguardar o grace e o envio.</> },
      { id: "d3", node: <>Conferir se entregou e se a sessão não caiu.</> },
      { id: "d4", node: <>Deu certo → repetir a parte 2 nos outros chips.</> },
    ],
  },
  {
    title: "5 · Dia a dia: adicionar contatos",
    sub: "o vínculo é com a CONTA GOOGLE, não com o aparelho",
    items: [
      { id: "e1", node: <><b>Em volume:</b> aba <b>Grupos → importar membros</b>. O disparo salva cada um na conta Google sozinho.</> },
      { id: "e2", node: <><b>Avulsos:</b> <code>contacts.google.com</code>, logado na conta do chip. Não precisa abrir o emulador.</> },
      { id: "e3", node: <>O Android baixa em <b>minutos</b>. O WhatsApp só reconhece no ciclo dele, <b>de hora em hora</b>.</> },
    ],
    note: {
      kind: "info",
      node: <>A cadeia é <code>conta Google → agenda do Android → WhatsApp</code>. O WhatsApp não tem lista própria, e o e-mail cadastrado dentro dele é só recuperação de conta. Por isso limpar o emulador não apaga contato: relogar a conta traz tudo de volta.</>,
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
