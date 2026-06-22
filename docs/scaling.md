# Escala dos 10 ambientes — emulador por número sem derrubar a performance

Regra de arquitetura (ver [architecture.md](./architecture.md)): `1 número : 1 emulador : 1 proxy : 1
ambiente`. Logo, 10 números = **10 emuladores** Android no servidor. A preocupação natural é "10× pesado".
Não é — o gargalo não está onde parece.

## Onde a performance pesa (e onde não)

1. **Emulador registrado ≈ idle.** Quem dispara é o **WAHA** (API). O emulador só segura a sessão de
   **primário** viva. Android parado consome pouco.
2. **O caro é o vídeo, não o Android.** CPU sobe quando você **assiste** a tela (noVNC/scrcpy codifica
   vídeo). Olhando **1 ambiente por vez**, só **1 stream** roda; os outros 9 ficam headless.

Ou seja: disparo, dashboard e responsividade **não** são limitados pelos 10 emuladores se eles rodarem
headless e a tela for transmitida **sob demanda**.

## As 5 regras de escala

| Regra | Por quê |
|---|---|
| **Headless por padrão, stream sob demanda** | os 10 rodam sem GUI; o `<iframe>` (noVNC) só conecta no ambiente aberto. 1 stream ativo, não 10. |
| **Teto de CPU/RAM por container** (`--cpus`, `--memory`) | um emulador não rouba o host dos outros 9. Isola por ambiente. |
| **Specs mínimas do Android** (resolução baixa, sem áudio, poucos cores) | ele só mantém o WhatsApp vivo, não roda jogo. |
| **Boot escalonado** (não ligar os 10 juntos) | evita tempestade de CPU no boot. Liga ao provisionar, um a um. |
| **Manter vivo, não derrubar** | a sessão de primário cai se o Android ficar offline demais → WAHA desconecta. Vivos sempre, mas headless-idle (leve). |

## Dimensionamento do servidor (10 ambientes)

Cada `docker-android` **headless idle** ≈ ~0,3–0,5 vCPU + ~3 GB RAM + ~8 GB disco. Para 10:

| Recurso | Estimativa (10 idle + 1 em stream) |
|---|---|
| RAM | ~32–40 GB |
| CPU | ~16 cores (folga p/ boot e o stream ativo) |
| Disco | ~100 GB NVMe |
| Virtualização | **/dev/kvm real** → servidor **dedicado / bare-metal** |

Um dedicado **16 cores / 64 GB / NVMe** segura os 10 com folga (9 idle-headless, 1 em stream).

> ⚠️ **VPS comum não serve** (sem nested virt confiável). Hetzner **dedicado** tem KVM nativo. O **GCP
> free trial** segura **1** emulador de teste, não os 10 — ver [gcp-emulator.md](./gcp-emulator.md).

## Mapa dos 10 ambientes (container/porta/volume ÚNICOS)

O botão "Provisionar número" faz `docker run -p <NoVncPort>:6080 -v <VolumeName>:...`. Num host com 10
stacks, cada ambiente **precisa** de porta E volume de host únicos — senão o 2º provisionamento bate na
mesma 6080 (falha) ou monta o mesmo volume (compartilha conta/digital = colisão). A porta tem que
**bater** com a do `PHONE_VIEW_URL_N`. **Já parametrizado nos 10 composes** (defaults abaixo); o emulador
fica **desligado** até você setar `PHONE_VIEW_URL_N`.

| Stack | `PHONE_CONTAINER_N` | `PHONE_NOVNC_PORT_N` | `PHONE_VOLUME_N` | `PHONE_VIEW_URL_N` |
|---|---|---|---|---|
| A | `mtrx-android`   | 6080 | `android-data`    | `http://HOST:6080` |
| B | `mtrx2-android`  | 6081 | `android-data-2`  | `http://HOST:6081` |
| C | `mtrx3-android`  | 6082 | `android-data-3`  | `http://HOST:6082` |
| D | `mtrx4-android`  | 6083 | `android-data-4`  | `http://HOST:6083` |
| E | `mtrx5-android`  | 6084 | `android-data-5`  | `http://HOST:6084` |
| F | `mtrx6-android`  | 6085 | `android-data-6`  | `http://HOST:6085` |
| G | `mtrx7-android`  | 6086 | `android-data-7`  | `http://HOST:6086` |
| H | `mtrx8-android`  | 6087 | `android-data-8`  | `http://HOST:6087` |
| I | `mtrx9-android`  | 6088 | `android-data-9`  | `http://HOST:6088` |
| J | `mtrx10-android` | 6089 | `android-data-10` | `http://HOST:6089` |

> ⚠️ Container/porta/volume **não bastam** pra isolar contra ban — falta a **digital própria por
> emulador** (IMEI/Android ID), que **não é automatizada** (limitação real do emulador). Ver a ressalva
> em [architecture.md](./architecture.md) e a recomendação de validar 1 no GCP antes de escalar.

> Já parametrizado no compose (`Phone__NoVncPort: ${PHONE_NOVNC_PORT_N}`). Defina a env de cada stack.

## Como entra no código (sem refazer)

- O `docker-compose*.yml` já tem `PHONE_VIEW_URL_N`/`PHONE_CONTAINER_N` por ambiente → cada aba "Celular"
  embute o emulador do **seu** ambiente. Escalar = replicar a linha + apontar a porta noVNC.
- O botão **"Provisionar número"** (boot → instalar → proxy → SMS → vincular WAHA) roda **por ambiente**,
  sob demanda — não orquestra os 10 de uma vez.
