# Arquitetura — "1 número : 1 emulador : 1 proxy : 1 ambiente"

A regra que guia o sistema multi-ambiente, pra **evitar ban correlacionado**.

## A regra
```
1 número  :  1 emulador (digital própria)  :  1 proxy (IP)  :  1 ambiente (WAHA + dashboard)
```

## Por que (o modelo de ban)
- O WhatsApp **bane a CONTA (número)**, não o IP.
- Ele **correlaciona contas** principalmente pela **digital do aparelho** (device fingerprint:
  IMEI/Android ID/detecção de emulador) — **não** pelo IP.
- **Proxy** só maquia o **IP** (sinal **menor**). Trocar o IP **não** muda o aparelho.
- ⇒ **2 contas no mesmo emulador = mesma digital = correlacionadas.** Um ban arrasta o outro.

Por isso **não se empilha** números num emulador (mesmo com 2 chips e 2 proxies por app — o proxy do
Android é *system-wide*, e o problema é o **aparelho**, não o IP).

## Onde cada peça roda
| Peça | Onde |
|---|---|
| **Dashboard + API + WAHA + Postgres** | localhost (teste) ou servidor (prod) |
| **Emulador** (Android principal de cada número) | **host com KVM** — docker-android + **noVNC embutido por ambiente** (servidor); GCP grátis (teste); LDPlayer em janela (teste local Windows) |

O **emulador não roda confiável no Windows local** (WSL2 trava as vCPUs; host-emulador dá ANR). O
embed é a parte fácil — o que falta é o **host com KVM**.

## Papéis
- **Emulador** = dispositivo **PRINCIPAL** do número (registra/segura a conta).
- **WAHA** = **companion** (vinculado por QR) que faz o **disparo**. Nunca vira principal.
- O **disparo** sai pelo **proxy do WAHA** (`WAHA_PROXY_N`) — já isolado por ambiente.

## Mapa dos 10 ambientes
```
Ambiente A (mtrxsys)      → número 1  → emulador 1 → proxy 1 (WAHA_PROXY_1)  → app: WhatsApp Business
Ambiente B (compose-2)    → número 2  → emulador 2 → proxy 2 (WAHA_PROXY_2)  → app: WhatsApp Messenger
…
Ambiente J (compose-10)   → número 10 → emulador 10 → proxy 10               → app: livre
```
- **Business vs Messenger** não é divisão de ambiente — é só **qual app** aquele número usa (os dois
  funcionam com o WAHA). Cada número (seja Business ou Messenger) é **um ambiente**.
- Cada ambiente embute **seu** emulador na aba "Celular" (`PHONE_VIEW_URL_N` = noVNC daquele Android).

## Disparar de Business E Messenger ao mesmo tempo?
Sim — são **2 ambientes** (A e B), cada um com seu número/app/emulador/proxy, rodando em paralelo.
**Não** 2 números num ambiente (1 WAHA = 1 número).

## Fluxo de conexão (sem o gate antigo)
1. Login no dashboard (email/senha) — **não** trava mais no QR.
2. Aba **"Celular"**: mostra o **QR** (quando desconectado) + a tela do emulador + a identidade.
3. No **emulador**, no WhatsApp → Aparelhos conectados → escaneia o QR → o WAHA vira companion.
4. A sessão **persiste** — próximos logins entram direto; o QR só volta se desconectar.

## Recuperação de ban
Ver **[recovery.md](recovery.md)**: ban → **chip novo + digital nova do emulador** (nova instância /
randomizar IMEI) + (recomendado) proxy novo. Com 1:1:1:1, um ban **não derruba os outros 9**.

## Docs relacionados
- **[phone-primary-server.md](phone-primary-server.md)** — emulador no servidor (docker-android + noVNC) + teste grátis na GCP.
- **[recovery.md](recovery.md)** — recuperação após ban.
- **[proxy.md](proxy.md)** — proxy por ambiente.
