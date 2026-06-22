# Aparelho virtual (aba "Celular")

A aba **"Celular"** é o **aparelho virtual** do número. Há **dois modelos**:

### 1. WAHA (padrão, leve) — o aparelho virtual é o motor de WhatsApp Web
O **WAHA** (mesma categoria de Evolution API / WppConnect) é um motor de WhatsApp Web **headless em
container**, controlado pelo app por HTTP. O chip é **pareado por QR** (tela de conexão) e o WAHA vira
um **dispositivo vinculado** (companion). **Roda no localhost, sem KVM/emulador** — é o que o sistema
já usa pro disparo. Na aba "Celular": mostra a **identidade real** do chip (número/nome) e o estado.
Para reparear/trocar o chip: **Desconectar WhatsApp** no topo.

> **Honestidade:** companion ≠ "aparelho definitivo". Você desliga o físico no dia a dia, mas o número
> segue **registrado no físico** (principal) e o WhatsApp exige que ele **reapareça online de tempos em
> tempos** (~14 dias sem ele → vinculados caem). **Guarde o chip** num modem pras re-verificações.
>
> Setup durável (celular barato sempre-ligado + regras de manutenção): **[modem-keepalive.md](modem-keepalive.md)**.

### 2. docker-android (opção de servidor) — o aparelho virtual vira o PRINCIPAL
Quando você quiser **dispensar o físico de vez**, um **Android de verdade em container**
(`budtmo/docker-android`) pode **registrar o número por SMS** e virar o **dispositivo principal** (e o
WAHA passa a companion pro disparo). A **própria aba** provisiona/liga/instala — sem prompt, falando com
o **socket do Docker**. **EXIGE host Linux com `/dev/kvm`** (servidor dedicado); o resto deste doc é
sobre este modelo. Fica na seção recolhível "opção de servidor" da aba.

> Pra fechar o ciclo **sem aparelho físico nenhum** (o SMS de re-verificação chega num **SIM gateway**
> via API, não num celular na mão): arquitetura completa em **[phone-primary-server.md](phone-primary-server.md)**.

### Android LOCAL com Play Store (Windows) — ver/usar o WhatsApp num Android real
Pra **ver e usar** um Android de verdade **com Play Store** dentro da aba "Celular" **no Windows** (sem
servidor): o emulador roda **no host** (AVD `google_apis_playstore`, via WHPX) e o **ws-scrcpy** espelha
a tela embutida na aba. É a experiência tipo BlueStacks pra **baixar/usar** o WhatsApp; o **disparo
continua no WAHA** (não torne o emulador o principal de um número real aqui — risco de ban).

```powershell
# 1x (criar o AVD com Play Store): sdkmanager "system-images;android-34;google_apis_playstore;x86_64"
#                                  avdmanager create avd --name mtrxA --package <esse pacote> --device pixel_6
scripts\phone-local.ps1                                              # boota o AVD + expõe o adb
docker compose -f docker-compose.yml --profile phone-local up -d scrcpy   # tela em :8000
# rebuild o web com VITE_EMULATOR_URL=http://localhost:8000 (PHONE_VIEW_URL_1) → aba "Celular" → "Mostrar tela do Android"
```

## A ideia (o que isso resolve)

**Emparelhar o aparelho físico com o virtual e parar de depender do físico:**

1. O Android virtual instala o WhatsApp e **registra o número por SMS** → ele vira o **dispositivo
   principal** do número (registrar num novo aparelho **desregistra** o antigo).
2. O **WAHA** entra como **companion** (dispositivo vinculado por QR) e continua fazendo o **disparo**.
3. Com o virtual já principal, você pode **tirar o chip do aparelho físico** — o virtual segue dono.

```
┌─────────────── mesma conta WhatsApp ───────────────┐
│  Android virtual (aba "Celular")  ← PRINCIPAL       │  ← registra por SMS
│  WAHA (container)                 ← companion       │  ← disparo em massa
└────────────────────────────────────────────────────┘
```

## ⚠️ Pré-requisito que decide tudo: host com `/dev/kvm`

O Android em container precisa de virtualização **`/dev/kvm`**. Isso **NÃO existe** no Docker Desktop
do Windows/WSL2 (testado: `vmx` aparece, mas o device `/dev/kvm` não). Logo, o serviço `android`
(profile `phone`) **só roda**:

- **Linux bare-metal** com KVM (`ls -l /dev/kvm` existe), ou
- **VPS/cloud com virtualização aninhada** habilitada.

No Windows este serviço fica **desligado** e a aba "Celular" continua na **maquete fake**
(`src/MtrxSys.WahaEmulator`) — útil só pra mexer na UI. Pra validar o aparelho real, use um **host
Linux** (uma VM, um VPS, ou o servidor dedicado). O dashboard pode ficar onde estiver e orquestrar o
Android pelo socket daquele host.

## Tudo pela aba (sem prompt)

A aba "Celular" chama `/api/phone/*` e o `DockerCliPhoneOrchestrator` executa via `docker` CLI:

- **GET `/api/phone/status`** → `unavailable` (sem docker) · `not_created` · `exited`/`created`/`running` (+ `viewUrl`).
- **POST `/api/phone/provision`** → cria o container do Android (se não existe) e liga. Idempotente.
- **POST `/api/phone/start`** / **`/stop`** → liga/desliga.
- **GET `/api/phone/logs?tail=200`** → logs do boot do Android, na própria aba.
- **POST `/api/phone/whatsapp/install`** → baixa o APK (de `Phone:WhatsAppApkUrl`) e instala via `adb`.
- **POST `/api/phone/proxy`** `{ "server": "IP:porta" }` → aplica o `http_proxy` global do Android (vazio limpa).

Fluxo na aba: **Provisionar aparelho** → **Ligar** → **Mostrar tela na aba** (noVNC) → **Instalar
WhatsApp** → registrar o número por SMS (vira principal) → vincular o WAHA por QR (companion).

## Subir (num host Linux com KVM)

```bash
# Aponte a aba pro noVNC e (opcional) informe o APK do WhatsApp pro botão "Instalar WhatsApp":
export PHONE_VIEW_URL_1=http://localhost:6080      # B: PHONE_VIEW_URL_2=http://localhost:6081
export PHONE_WA_APK_URL_1=https://SEU_HOST/whatsapp.apk   # você fornece a URL (sem URL oficial estável)
docker compose -f docker-compose.yml up -d --build         # sobe stack + web
#   (repita com docker-compose-2.yml pro Ambiente B)

# No dashboard → aba "Celular" → "Provisionar aparelho" cria e liga o Android (a API dá `docker run`).
#   Acompanhe por "Ver logs"; quando bootar, "Mostrar tela na aba" embute o Android.
```

> Não precisa pré-criar o serviço `android` do compose — a aba provisiona sozinha. O serviço no
> compose existe como forma declarativa/opcional (ex.: deixar sempre ligado com `--profile phone up -d`).

## Instalar o WhatsApp + ligar o número

1. **Instalar WhatsApp** (botão na aba) — baixa o APK de `Phone:WhatsAppApkUrl` e roda `adb install`.
   Sem a URL configurada, o botão explica a alternativa manual (`docker cp` + `adb install`).
2. **Registrar** o número (na tela do Android, via SMS do seu chip/modem) → o Android vira o
   **principal** do número.
3. **Vincular o WAHA** por QR (na aba do WAHA) → o WAHA vira **companion** pro disparo.

## Proxy por chip (mesmo IP do WAHA)

Use o botão **Aplicar proxy** na aba (campo `IP:porta`) — equivale a:

```bash
docker exec mtrx-android adb shell settings put global http_proxy SEU_IP:PORTA   # limpar: deixe vazio na aba
```

> O `http_proxy` global cobre o tráfego do app, mas **não aceita user:senha** — use proxy
> IP-autenticado ou um sidecar local sem auth. Reaproveita os IPs de [`proxy.md`](proxy.md).

## Verificação
1. `ls -l /dev/kvm` no host → existe (senão, nada disso roda).
2. Aba "Celular" → "Provisionar" → "Ligar" → "Mostrar tela" mostra o Android.
3. "Instalar WhatsApp" instala; o WhatsApp abre e registra o número (SMS do chip/modem).
4. Tráfego sai pelo IP do proxy (confira no painel do provedor — ver `proxy.md`).
5. WAHA vincula por QR e o disparo roda; opt-out ("SAIR") chega.

## Notas honestas
- **Ban**: registrar WhatsApp fresco em emulador é a ação de **maior risco de ban** (e, sendo a mesma
  conta, derruba o WAHA junto). Use **número descartável**. O proxy cobre só o IP, não o fingerprint.
- **"Tirar o chip e funcionar pra sempre" não é garantido**: o WhatsApp **re-verifica** contas de
  tempos em tempos (emulador é alvo fácil) e aí pede o **SMS de novo**. Mantenha o chip num
  **modem/aparelho acessível** só pra receber esses códigos — não jogue o chip fora.
- **RAM**: cada Android consome ~2–4 GB. Pros **10 ambientes**, um **host dedicado**; nessa escala,
  considere o **redroid** (Android em container sem KVM, bem mais leve que 10 emuladores) — a
  orquestração da aba é a mesma (adb + tela).
- **Windows/WSL2** (testado): dá pra criar o `/dev/kvm` no WSL2 (`modprobe kvm_intel` + `chmod 666`,
  kernel 6.6 tem KVM como módulo), e o container até acessa o device — **mas as vCPUs do emulador
  travam** na virtualização aninhada (`detected a hanging thread 'QEMU2 CPU…'`). Ou seja: o modelo
  docker-android é **só host Linux bare-metal/VPS com KVM**. No Windows, use o modelo **WAHA** (que roda
  liso no localhost) e deixe o docker-android pro servidor.
