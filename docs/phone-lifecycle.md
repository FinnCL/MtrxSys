# Ciclo de vida do aparelho primário (leve) + disparo com o WAHA

> Resumo: o **emulador é a "casa" da conta** (o WhatsApp registrado por SMS). **Quem dispara é o
> WAHA** (companion vinculado por QR). O emulador **não roda 24/7** — ele dorme e só acorda pro
> setup e pra um **keep-alive a cada ~10 dias**. No regime normal só o WAHA roda → leve e
> performático, permitindo os 10 ambientes no mesmo servidor. Ver também `docs/phone.md`,
> `docs/architecture.md`, `docs/modem-keepalive.md`.

## Por que o emulador pode dormir
O WAHA (NOWEB) é um **dispositivo vinculado (companion)** — ele não é dono da conta, ele se vincula a
uma conta que mora no emulador. Pelo protocolo multi-dispositivo do WhatsApp, um companion **funciona
sozinho por ~14 dias** sem o principal aparecer. O único requisito: o **principal (emulador) precisa
ficar online ≥1× a cada ~14 dias**, senão o WhatsApp desloga o companion e o disparo para.

Por isso o lifecycle: liga só pra **registrar/parear** e pra um **keep-alive folgado (~10 dias)**.

## Onboarding de 1 chip (ex.: ambiente A)
1. **Provisionar** — aba **Celular** → *"Provisionar número (automático)"*. Sobe o emulador **com
   teto de recurso** (`--memory 8g --cpus 4`, `--restart no`), instala o WhatsApp e aplica o proxy
   (se informado).
2. **Registrar por SMS DENTRO do emulador** — ponha o chip num **celular físico só para receber o
   código**. No WhatsApp do emulador, digite o número → o WhatsApp manda o SMS → leia no físico →
   **digite o código no emulador**. Agora o **emulador é o PRINCIPAL** (dono da conta). Clique
   *"Registrei o número"*. Pode **remover o chip** — não é mais necessário.
   > ⚠️ QR **não** transfere a conta. Vincular o emulador por QR a um físico deixaria o físico como
   > principal — tirar o chip deslogaria tudo. O caminho certo é **registrar por SMS no emulador**.
3. **Vincular o WAHA (companion)** — a aba gera o **QR do WAHA**; escaneie-o **dentro do WhatsApp do
   emulador** (Aparelhos conectados → Conectar um aparelho). Clique *"Vinculei o WAHA"*. Espere a
   sessão ficar **WORKING** (`/api/waha/status`).
4. **O primário dorme sozinho** — o `PhoneKeepAliveService` vê `WORKING` + uma carência (~5 min, pro
   sync inicial terminar) e **desliga o emulador**, gravando o `phone_primary_last_online_utc`. A
   partir daí, **só o WAHA roda**; o disparo continua normal com o emulador desligado.

## Regime normal
- **Só o WAHA roda.** O emulador fica `exited`. A aba mostra *"💤 primário dormindo — WAHA ativo"*.
- **Keep-alive automático** a cada ~10 dias (< 14): o serviço acorda o primário num **horário
  escalonado por stack** (pra os 10 não subirem juntos), espera o WhatsApp reconectar, segura alguns
  minutos, grava o novo `last-online` e **desliga de novo**.
- **Keep-alive manual**: botão *"Acordar / Keep-alive agora"* na aba (útil pra forçar antes de uma
  viagem/janela de manutenção). É não-bloqueante — a API só agenda; o serviço roda o ciclo.

## Problemas & recuperação
- **WhatsApp pede re-verificação por SMS** (o emulador não tem chip): reinsira o chip no físico pra
  ler o código e digite no emulador. (Futuro: SIM gateway pra automatizar.)
- **WAHA caiu de WORKING após um keep-alive** (vínculo perdido — ex.: host ficou >14 dias fora): o
  serviço loga o aviso e a landing mostra *"Desconectado"*. Recuperação = **re-parear por QR** (passo
  3). A margem de 4 dias (10 vs 14) absorve uma falha isolada.
- **Aplicar os tetos num emulador já criado antes desta mudança**: recrie-o 1× (`docker rm -f
  mtrx-android` + *Provisionar*). O **volume persistente preserva a sessão pareada** — não precisa
  re-registrar.

## Config (por stack, `PhoneOptions` — defaults seguros)
`MemoryLimit=8g`, `Cpus=4`, `RestartPolicy=no`, `KeepAliveEnabled=true`,
`KeepAliveIntervalHours=240` (10 dias), `StopAfterPairGraceMinutes=5`, `KeepAliveHoldMinutes=3`,
`KeepAliveStaggerSlot=-1` (deriva do nome do container). Exponíveis via env `Phone__...` no
`docker-compose*.yml` de cada ambiente.
