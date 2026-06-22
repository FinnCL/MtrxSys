# Setup do "modeminho sempre-ligado" — aposentar o celular sem perder o disparo

Como manter o número **vivo e estável** com o disparo rodando pelo WAHA, **sem depender do seu celular
pessoal**. Esse é o caminho **prático e leve** (companion), que roda hoje — diferente do
emulador-principal (servidor/KVM, ver [`phone.md`](phone.md)).

## Quem é quem

```
┌──────────────────────── mesma conta WhatsApp ────────────────────────┐
│  Aparelho PRINCIPAL  = celular barato sempre-ligado (dono do número)  │
│  WAHA (container)    = dispositivo VINCULADO (faz o disparo)          │
└──────────────────────────────────────────────────────────────────────┘
```

- O **principal** é quem "registra" o número e quem o WhatsApp re-verifica.
- O **WAHA** é só um **vinculado** (igual WhatsApp Web). Ele dispara sozinho, **mesmo com o principal
  offline** — mas com regras (abaixo).

## ⚠️ A pegadinha: "modeminho" precisa ser um CELULAR, não um modem GSM puro

O dispositivo **principal** do WhatsApp **roda o app WhatsApp**. Um **modem/dongle 4G** sozinho **não
serve de principal** — ele não roda o app, só recebe SMS. Então:

- ✅ **Recomendado:** um **celular Android barato** (pode ser usado/antigo), com o **chip dentro**, o
  **WhatsApp instalado e registrado**, ligado **24/7 no WiFi**. Esse é o principal durável.
- ⚠️ **Modem GSM/gateway de SMS:** só vale como **caixa de entrada do SMS de re-verificação** (avançado,
  pra centralizar muitos chips) — mas o principal ainda tem que ser um celular com o app. Comece pelo
  Android barato; só vá pro gateway se for escalar muitos números.

## As 3 regras que mantêm vivo (e o que quebra)

| Regra | Por quê | Se furar |
|---|---|---|
| **WiFi 24/7** | o principal precisa **aparecer online ≥ 1x a cada ~14 dias**, senão o WhatsApp **desloga os vinculados** | WAHA cai → disparo para |
| **Chip DENTRO do aparelho** | o WhatsApp **re-verifica** a conta de tempos em tempos (mais em conta de automação) e manda **SMS pro número** | sem o chip pra ler o código → conta **trancada** |
| **IP/proxy consistente** | trocar o número de país/IP de repente é gatilho de ban | sessão derrubada / número banido |

> O chip **não** precisa de saldo/plano de dados — o WhatsApp roda pelo WiFi. Ele serve pra **receber o
> SMS** de re-verificação. Por isso fica **dentro** do celular barato (ou num gateway que te repassa o SMS).

## Como liga no sistema (já está pronto no software)

1. **Parear o WAHA** (uma vez): no dashboard, na tela de conexão → **QR** (ou código) → no celular
   barato: **Aparelhos conectados → Conectar um aparelho**. Pronto — a aba "Celular" passa a mostrar a
   identidade real. (Já validamos: status `WORKING`.)
2. **Proxy por ambiente** (recomendado pra anti-ban): defina `WAHA_PROXY_1` (Ambiente A), `WAHA_PROXY_2`
   (B)… com o IP do chip. Ver [`proxy.md`](proxy.md). Idealmente o **celular principal** também sai por
   um IP da **mesma região** do proxy do WAHA.
3. **Monitorar:** a **landing** (cards A–J) pinta o selo do chip (Pareado / Chip com falha /
   Desconectado) e a **aba "Celular"** mostra número/nome + estado **ao vivo** (poll de 5s). Se cair, o
   selo muda e a tela de conexão (QR) reaparece sozinha.

Nada a codar pra isso funcionar: o WAHA companion **é** o motor, e o disparo/opt-out/webhook já operam
sobre ele.

## Rotina mínima de manutenção

- Deixe o celular **ligado, carregando, no WiFi**, num canto. Não o use pra mais nada.
- **1x por semana**: olhe a landing — todos os cards **Pareado**? Se algum virar "Chip com falha"
  (breaker aberto) ou "Desconectado", religue/reparei aquele.
- Quando o WhatsApp pedir re-verificação no celular: **digite o código** (chega por SMS no próprio
  aparelho). Resolvido, o WAHA volta sozinho.
- **Não** abra o mesmo número no WhatsApp Web de outro lugar sem necessidade (mais um vinculado = mais
  risco/limite de 4 dispositivos).

## Escala pros 10 ambientes (futuro)

- O simples e robusto: **10 celulares baratos** (1 por chip/ambiente), todos no WiFi, cada um pareado ao
  WAHA do seu ambiente, cada um com seu **proxy** (`WAHA_PROXY_1..10`).
- Avançado (centralizar chips): um **gateway de SMS** com os 10 chips só pra receber re-verificação, e os
  10 principais como celulares com o app — mais complexo; só se a quantidade justificar.

## Resumo honesto

Isso **não** "joga o chip fora" — o número segue **registrado num aparelho físico** (o celular barato).
O que você ganha é **parar de depender do SEU celular**: o número mora num aparelho dedicado, sempre
ligado, e o **disparo roda pelo WAHA** independentemente. É o jeito estável e barato. Descartar o chip
de vez só com o emulador-principal (servidor/KVM) — e mesmo ele precisa do chip pro registro/re-verificação.
