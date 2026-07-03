# Engine `redroid` do aparelho virtual (piloto)

> **Objetivo:** rodar o "aparelho virtual" da aba **Celular** com **redroid** (Android no kernel do
> host, **sem `/dev/kvm`**, ~0.5–1 GB/instância) em vez do `budtmo/docker-android`. Isso permite
> **entrar no emulador, instalar o WhatsApp (sideload do APK) e registrar o número com o chip físico
> (SMS)** — e escalar pros 10 ambientes num servidor só. Coexiste com o docker-android: seleciona por
> `Phone:Engine`. Contexto e comparação em [phone-primary-server.md](phone-primary-server.md#engine-embutível-e-escala).

Este é o **piloto de 1 ambiente (stack A)**. Depois de validar, replica-se pros 10 (ver o fim).

---

## Fase 0 — Portão do host (make-or-break) 🔴

redroid usa os módulos **binder/ashmem** do kernel do host. **Sem eles, redroid NÃO sobe** — e o
caminho é cair no modelo já pronto (WAHA + chip físico/modem).

```bash
# 1) O host é Linux? (redroid não roda no Docker Desktop do Windows de forma confiável.)
uname -a
# 2) binder disponível?
lsmod | grep -i binder   ||   ls -l /dev/binder* 2>/dev/null
# 3) Se faltar, carregar (precisa root; persistir depois):
sudo modprobe binder_linux devices="binder,hwbinder,vndbinder"
echo 'binder_linux' | sudo tee /etc/modules-load.d/redroid.conf
echo 'options binder_linux devices=binder,hwbinder,vndbinder' | sudo tee /etc/modprobe.d/redroid.conf
```

Se `modprobe` falhar (kernel sem o módulo / host gerenciado sem acesso), **pare aqui**: redroid não é
viável nesse host.

---

## Fase 1 — Código (já implementado)

- `Phone:Engine` (`docker-android` default | `redroid`) seleciona o orquestrador no DI
  (`DependencyInjection.cs`). Nada mais muda no contrato (`IPhoneOrchestrator`, 8 métodos) nem no front.
- `RedroidPhoneOrchestrator` provisiona `docker run … redroid/redroid` (sem `/dev/kvm`, volume em
  `/data`, adb publicado em `127.0.0.1:AdbPort`) e fala com o Android por **adb TCP**
  (`host.docker.internal:AdbPort`). O cliente `adb` foi adicionado à imagem da API
  (`android-tools` no `src/MtrxSys.Api/Dockerfile`).
- Helpers de processo/download ficam em `Phone/DockerCli.cs` (compartilhados com o docker-android).

## Fase 2 — Configurar e subir o stack A (no host Linux)

`adb`/redroid usam **1 porta host por ambiente**: A=5555 … J=5564 (`Phone__AdbPort`).

No `.env` (ou export) do host, pro stack A:

```bash
# Engine + porta adb
export PHONE_ENGINE_1=redroid
export PHONE_ADB_PORT_1=5555
# Volume do estado do Android (sessão do WhatsApp) — separado do docker-android:
export PHONE_VOLUME_1=redroid-data-a
export PHONE_CONTAINER_1=mtrx-redroid          # nome do container que a aba provisiona
export PHONE_WA_APK_URL_1=https://SEU_HOST/whatsapp.apk   # p/ o botão "Instalar WhatsApp"

# Tela na aba = ws-scrcpy (não noVNC). Estes 4 vão pro BUILD do web:
export PHONE_SERVER_OPTION_1=1                  # mostra a seção "Android em container"
export PHONE_VIEWER_KIND_1=scrcpy              # embute o stream do ws-scrcpy
export PHONE_VIEW_URL_1=http://localhost:8000  # (prod: https://<url pública do ws-scrcpy>)
export PHONE_UDID_1=127.0.0.1:5555             # device que o ws-scrcpy enxerga (ver Fase 2b)

docker compose -f docker-compose.yml up -d --build api web
```

> A API **não** exige o serviço `android`/`redroid` do compose — a aba provisiona sozinha via
> `docker run` (socket montado). O `docker run` do redroid que a API emite é:
> ```
> docker run -itd --name mtrx-redroid --privileged [--restart no] [--memory 8g] [--cpus 4] \
>   -p 5555:5555 -v redroid-data-a:/data redroid/redroid:14.0.0-latest \
>   androidboot.redroid_width=720 androidboot.redroid_height=1280 \
>   androidboot.redroid_dpi=320 androidboot.redroid_gpu_mode=guest
> ```
> O adb é publicado em **0.0.0.0:AdbPort** (não 127.0.0.1): a API roda em container bridged e alcança
> o redroid pelo **host-gateway** (`host.docker.internal`) — uma porta em 127.0.0.1 do host não
> responde por esse caminho. A exposição é travada no firewall (`deploy/setup-firewall.sh` dropa
> 5555-5564 de fora da rede docker). **Rode `sudo bash deploy/setup-firewall.sh` antes de expor.**

### Fase 2b — Tela via ws-scrcpy (a parte mais fiddly)

O redroid não tem tela embutida; a aba embute o **ws-scrcpy** (adb → stream). O ws-scrcpy fala com um
**servidor adb** que já tenha conectado no device. O análogo pronto está em `scripts/phone-local.ps1`
(Windows) — no host Linux é o mesmo padrão:

```bash
# depois de a aba provisionar o redroid (adb em 127.0.0.1:5555):
adb connect 127.0.0.1:5555
adb kill-server
adb -a -P 5037 nodaemon server &     # servidor adb aberto p/ o container do ws-scrcpy alcançar
docker compose -f docker-compose.yml --profile phone-local up -d scrcpy   # ws-scrcpy em :8000
```

O `scrcpy` (`docker-compose.yml`) já aponta `ADB_SERVER_SOCKET=tcp:host.docker.internal:5037` e a
imagem já traz `adb` — ele vê o device `127.0.0.1:5555` (= `PHONE_UDID_1`). Em produção, exponha o
ws-scrcpy atrás do portão (subdomínio próprio) e ponha essa URL em `PHONE_VIEW_URL_1`.

## Fase 3 — Validar (fluxo real da aba, stack A)

1. Aba **Celular** → **Provisionar** → aguardar `GET /api/phone/booted` virar `true` (~1–2 min).
2. **Mostrar tela** → o stream do ws-scrcpy renderiza o Android.
3. **Instalar WhatsApp** → retorna `Success` (sideload do APK via adb).
4. Na tela, **registrar o número por SMS** (chip no seu celular físico; digite o código no emulador)
   → o redroid vira o **PRINCIPAL** do número.
5. **Vincular o WAHA** por QR (o QR de `/api/waha/qr.png`, escaneado dentro do emulador) → sessão
   **WORKING** (`/api/waha/status`).
6. O `PhoneKeepAliveService` vê WORKING + carência e **desliga** o redroid (`exited`) — daí só o WAHA
   roda; keep-alive a cada ~10 dias.

**Fallback provado:** sem `PHONE_ENGINE_1` (ou `=docker-android`), o comportamento atual (budtmo/KVM)
segue idêntico.

## Caveats honestos (não mudam com o engine)

- **Ban alto:** registrar WhatsApp fresco em emulador é a ação de maior detecção. Use **número
  descartável** no piloto. O proxy cobre o IP, não o fingerprint.
- **Sem Play Store:** redroid vanilla não traz GApps — por isso o WhatsApp entra por **APK sideload**
  (é o que já fazemos). Play Store exigiria imagem com gapps (mais pesada e mais detectável).
- **Re-verificação:** o WhatsApp re-pede SMS de tempos em tempos — **guarde o chip** num modem/aparelho
  acessível. Ver [modem-keepalive.md](modem-keepalive.md).

## Replicar pros 10 (após validar o piloto)
- `docker-compose-2..10.yml`: `PHONE_ENGINE_N=redroid`, `PHONE_ADB_PORT_N` = 5556…5564, volumes
  `redroid-data-b..j`, containers `mtrx2-redroid`…`mtrx10-redroid`.
- `deploy/up-all-prod.sh`: o portão `[ -e /dev/kvm ]` (linha ~13) vira checagem de `binder` quando
  o engine é redroid.
- `deploy/gen-config.sh`: o subdomínio `phone-*` deixa de proxyar o noVNC (6080+) e passa a servir o
  ws-scrcpy (8000) — atrás do portão.
- `deploy/setup-firewall.sh`: **já cobre 5555-5564** (adb do redroid) — dropa de fora da rede docker.
  Rode `sudo bash deploy/setup-firewall.sh` no host antes de expor.
