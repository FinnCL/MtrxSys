# Emulador-principal no servidor — o virtual vira o dono do número

O caminho pra **aposentar o aparelho físico de vez**: o **Android virtual (em container) vira o
dispositivo PRINCIPAL** do número (registra por SMS), e o **WAHA** fica como companion pro disparo.
Diferente do modelo leve ([modem-keepalive.md](modem-keepalive.md), onde o principal é um celular
físico), aqui **não há celular físico** — só um **chip físico num gateway** acessível por API.

> **Pré-requisito que não roda no Windows:** exige host **Linux com `/dev/kvm`** (servidor dedicado /
> VPS com virtualização aninhada). Testamos: no WSL2 as vCPUs do emulador travam. É **só servidor**.
>
> A regra geral do multi-ambiente está em **[architecture.md](architecture.md)** (1 número : 1 emulador
> : 1 proxy : 1 ambiente). O **embed** na aba "Celular" é o mesmo seja o emulador na GCP (teste) ou no
> servidor (prod) — muda só o `PHONE_VIEW_URL_N` (= URL do noVNC daquele Android).

## Testar de graça na GCP (sem servidor próprio, sem pagar)

A GCP dá **$300 de crédito (90 dias)** e **suporta virtualização aninhada** — é o jeito gratuito de ter
`/dev/kvm` e validar o "passo 1" (emulador embutido com Play Store). ⚠️ Pede cartão pra validar (não
cobra no trial). **Hetzner Cloud (VPS) NÃO serve** (sem nested virt confiável); só dedicado/bare-metal.

```bash
# No Cloud Shell da GCP (cloud.google.com → trial → ícone >_):
gcloud compute instances create android-test --zone=us-central1-a \
  --machine-type=n2-standard-4 --image-family=ubuntu-2204-lts --image-project=ubuntu-os-cloud \
  --enable-nested-virtualization --boot-disk-size=40GB
gcloud compute ssh android-test --zone=us-central1-a
ls -l /dev/kvm && egrep -c '(vmx|svm)' /proc/cpuinfo   # /dev/kvm tem que existir + número > 0
```
Com o `/dev/kvm` confirmado: instala o Docker, sobe o **docker-android (imagem com Play Store) + noVNC**
(porta 6080), abre a porta no firewall da GCP, e aponta a aba do dashboard: `PHONE_VIEW_URL_1` = a URL
pública do noVNC da VM. O dashboard (local ou servidor) **embute** a tela por iframe.

> Pra não gastar o crédito: `gcloud compute instances stop android-test --zone=us-central1-a` quando
> não estiver usando.

## A verdade sobre o SIM (o limite físico)

O SIM **não** dá pra virtualizar: a operadora só entrega o SMS de registro/re-verificação pra um
**chip físico real** (o WhatsApp recusa VoIP/números virtuais). O que se virtualiza é o **acesso ao
chip**:

- **SIM gateway / banco de SIMs** = hardware que segura os chips físicos (1 a centenas) e expõe os SMS
  recebidos por **API HTTP**. Ex.: gateways GoIP / SMS gateways industriais.
- Assim o **código de re-verificação chega "no virtual"** (a API do gateway), e o sistema (ou você) lê
  e digita no WhatsApp do emulador. **Esse é o "aparelho físico sendo virtual".**

```
┌──────────────────── servidor Linux (KVM) ────────────────────┐
│  docker-android (emulador)  = PRINCIPAL virtual (WhatsApp)    │
│  WAHA (container)           = companion (disparo)            │
└───────────────────────────────────────────────────────────────┘
            ▲ código SMS (registro / re-verificação)
            │  via API HTTP
┌───────────┴───────────┐
│  SIM gateway (chips)   │  ← chips FÍSICOS, acesso VIRTUAL/API
└────────────────────────┘
```

## O que JÁ está pronto no código (orquestração pela aba)

A aba "Celular" → seção "opção de servidor — Android real (KVM)" já faz, via `/api/phone/*` (docker.sock):

- **Provisionar** (`docker run` com `--device /dev/kvm`, noVNC, volume) · **Ligar** · **Mostrar tela** (noVNC)
- **Instalar WhatsApp** (sideload do APK de `Phone:WhatsAppApkUrl`)
- **Aplicar proxy** (http_proxy global do Android = IP do chip)
- **Logs**

Ou seja: no servidor, o ciclo é **clicar na aba** — sem prompt. Falta só plugar o **SIM gateway** (abaixo).

## Fluxo de registro (1ª vez) e re-verificação

1. Aba "Celular" → opção de servidor → **Provisionar** → **Ligar** → **Mostrar tela**.
2. **Instalar WhatsApp** → na tela (noVNC), **registrar o número**.
3. O **SMS chega no SIM gateway** → leia o código pela **API do gateway** (ou no painel dele) → digite
   no WhatsApp do emulador. → O Android vira o **principal** (o físico, se houver, é desconectado).
4. **Vincular o WAHA** por QR (companion) → disparo segue pelo WAHA.
5. **Re-verificação** (quando o WhatsApp pedir): mesmo ciclo — o código chega no gateway, você lê e
   digita. Sem aparelho na mão.

## Automação futura (fase 2 — opcional)

Pra tirar o humano do passo 3/5, dá pra automatizar a digitação do código:

- **`ISmsGateway`** (a adicionar quando você escolher o gateway): `Task<string?> GetLatestCodeAsync(numero)`
  — lê o último SMS/OTP da API do gateway.
- **Appium/adb** no emulador: digita o número e o código na tela do WhatsApp programaticamente.
- Não scaffoldei isso ainda **de propósito**: a API do gateway varia por fabricante; codar agora seria
  chute. Quando você tiver o modelo do gateway, eu ligo o `ISmsGateway` ao fluxo (é um plugue limpo).

## Requisitos do servidor (checklist de quando você tiver a máquina)

- [ ] **Linux com `/dev/kvm`** (bare-metal, ou VPS com virtualização aninhada — confirme `ls -l /dev/kvm`).
- [ ] **RAM**: ~2–4 GB por emulador. Pros 10 ambientes, dimensione (ou use **redroid**, mais leve — ver phone.md).
- [ ] **Docker** + este repo; `Phone:WhatsAppApkUrl` apontando pro APK do WhatsApp.
- [ ] **SIM gateway** com os chips, acessível por API (anote a URL/credenciais — vão pro `ISmsGateway`).
- [ ] **Proxy por chip** (`WAHA_PROXY_1..10`) alinhado à região do número — ver [proxy.md](proxy.md).

## Caveats honestos (não mudam)

- **Ban alto**: registrar WhatsApp fresco em emulador + automação é o cenário de **maior detecção**.
  Use números descartáveis; espere perdas. O proxy cobre o IP, não o fingerprint do emulador.
- **Faixas de número**: o WhatsApp bloqueia muitos ranges de VoIP/gateway — use **chips de operadora
  comum** no gateway, não números "virtuais" de provedores de SMS.
- **Custo/complexidade**: emulador (RAM/CPU) + SIM gateway (hardware) + manutenção. Só compensa na escala.
- **Não é mágica**: o chip continua físico (no gateway). Você elimina o *aparelho* e o *manuseio*, não
  o SIM.

## Engine embutível + escala pros 10 ambientes (estudo)

Pra ter o "celular virtual" **embutido na aba** E **escalando pros 10 ambientes**, o engine certo é o
**redroid** — Android rodando **direto no kernel do host Linux** (módulos `binder`/`ashmem`), **sem
QEMU e sem KVM**. Muito mais leve que o emulador, conecta por adb e a tela embute via **ws-scrcpy**.

| Engine | Embute na aba? | KVM? | RAM/instância | 10 instâncias |
|---|---|---|---|---|
| BlueStacks | ❌ (app desktop) | host hypervisor | pesado | ❌ ToS/desktop |
| docker-android (budtmo) | ✅ noVNC built-in | ✅ exige `/dev/kvm` | ~2–4 GB | pesado (~30–40 GB + KVM) |
| **redroid** | ✅ via ws-scrcpy | ❌ **não precisa** | **~0.5–1 GB** | ✅ leve (~10–16 GB) |

### Layout dos 10 ambientes (no servidor)
```
servidor Linux (kernel com binder/ashmem)
 ├─ mtrx-redroid   (adb 5555) ─┐
 ├─ mtrx2-redroid  (adb 5556) ─┤
 ├─ …                          ├─ ws-scrcpy (web) → cada aba "Celular" embute o SEU device
 └─ mtrx10-redroid (adb 5564) ─┘
 + por ambiente: WAHA (disparo) · proxy próprio · volume (sessão do WhatsApp)
 + SIM gateway (10 chips físicos) → código de registro/re-verificação por API
```
- Cada ambiente: **1 redroid** (tela embutida) + **1 WAHA** (disparo) + **1 proxy** + **1 volume**.
- 1 ws-scrcpy serve todos (ele lista os devices) ou 1 por ambiente.
- Orquestração: o mesmo padrão `/api/phone/*` (hoje docker-android) ganha um modo `redroid` — `docker
  run redroid/redroid` por ambiente + adb connect; a tela é o stream do ws-scrcpy embutido.

### Dimensionamento
- **redroid:** ~10–16 GB RAM + ~12–20 vCPU pros 10 (vs ~30–40 GB + KVM com docker-android).
- Host: kernel Linux com `binder`/`ashmem` (kernels modernos ou imagem recomendada pelo redroid).

### Recomendação honesta pros 10 ambientes
- **Prático e JÁ construído:** **WAHA (companion) + chip num celular barato / SIM gateway** por
  ambiente. Leve, sem ban de "registro-em-emulador", e o disparo já roda assim hoje. **É o que eu
  recomendo** pro deploy dos 10 — a aba "Celular" mostra a identidade real de cada chip.
- **redroid-principal (emulador dono):** só compensa se você REALMENTE precisar **dispensar qualquer
  aparelho** — aceitando a RAM, o **SIM bank** e o **ban alto** de registrar WhatsApp em emulador em
  escala. É o caminho "sem aparelho nenhum", mas é o mais caro e arriscado.

> No localhost (Windows) nada disso roda (sem kernel Linux/binder/KVM) — é estudo pro **deploy**. Hoje,
> teste com WAHA (que roda) e deixe redroid/emulador pro servidor.

## Resumo

Quando você tiver o servidor: o **emulador-principal já está pronto pra orquestrar pela aba**. O que
transforma isso no "aparelho físico totalmente virtual" é o **SIM gateway** (chips físicos, acesso por
API) — e a fase 2 (automação do OTP) é um plugue (`ISmsGateway`) que eu ligo assim que você escolher o
hardware. É o único jeito honesto de fechar o ciclo: **o SIM não vira virtual, mas o aparelho e o
manuseio, sim.**
