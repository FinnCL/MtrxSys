# Rebuild limpo do emulador do stack A (`mtrx-dandroid`)

Recria o emulador do **stack A** do zero, resetando o estado do container — inclusive o
**gfxstream corrompido** que causa o crash-loop de renderização. Um comando: `bash deploy/rebuild-emulator-a.sh`.

## Quando usar

O emulador do A entrou num **crash-loop de renderização**. Sintomas:

- Tela (aba Celular) trava / não boota; `adb: device 'emulator-5554' not found` mesmo com o container `Up`.
- `docker exec mtrx-dandroid sh -lc 'ps -ef | grep qemu'` mostra `[qemu-system-x86] <defunct>` (qemu morto).
- `docker exec mtrx-dandroid sh -lc 'grep -c 0x502 /home/androidusr/logs/device.stdout.log'` alto —
  são **GL_INVALID_OPERATION (0x502)** do swiftshader no `glAttachShader`/resize (a "renderização" quebrada).
- `d_wm exited (exit status 1)` nos `docker logs mtrx-dandroid`.

O que **NÃO** é (já descartado num incidente 2026-07-24): OOM (`OOMKilled=false`, host com RAM sobrando),
KVM (nada no `dmesg`), lock stale de AVD (o `emulator.py` self-healing está mountado). É **estado gráfico
corrompido** — acumulado por churn (reset/recreate/adb pesado em sequência). O rebuild **reseta** esse estado.

> **GPU (atualizado 2026-07-24):** a causa raiz do crash-ao-abrir-o-WhatsApp era o renderizador
> **`swiftshader_indirect`** (default do docker-android) jogando **GL 0x502 em rajada** no
> `glAttachShader`/resize. A config agora usa **`-gpu swangle_indirect`** (ANGLE) — provado: abrir o
> WhatsApp deixa o qemu VIVO e **não gera nenhum GL 0x502 novo**. No A vem do `EMULATOR_ADDITIONAL_ARGS`
> do `emulator-a.yml`; nos B-J vem do default `PhoneOptions.EmulatorAdditionalArgs` (docker-run). Um
> nível baixo de 0x502 residual ainda é ruído "ignored"; o que importa é **não subir** ao abrir o app.
> Se o rebuild sozinho não estabilizar, confirme que o `-gpu swangle_indirect` está no processo do qemu
> (`docker exec mtrx-dandroid sh -lc 'ps -ef | grep qemu | grep -o "\-gpu [a-z_]*"'` → o ÚLTIMO vence).

## O que ele faz

```bash
docker rm -f mtrx-dandroid                                              # apaga o container corrompido
docker compose -p mtrxsys -f deploy/docker-compose.emulator-a.yml up -d dandroid   # recria do zero
```

1. `docker rm -f` apaga o container. O A guarda o estado **no próprio container** (persistência via
   *commit-to-image*, **sem volume**), então apagar = **zerar o estado** (gfxstream incluso).
2. `docker compose ... up -d dandroid` recria **da imagem** (`budtmo/docker-android:emulator_14.0`) com a
   config certa do `emulator-a.yml`: projeto `mtrxsys` (rede `mtrxsys_default` → alcança o gost/proxy),
   porta **6090** (que o Caddy do `phone-a` espera), self-healing do X-lock, `SCREEN 404x850`.

## ⚠️ Custo

- **Perde o WhatsApp/chip logado** (é commit-to-image, sem volume) → **re-registrar** depois
  (físico→emulador; ver o roteiro anti-ban). Como só se faz rebuild quando o emulador já está inutilizável,
  não se perde nada de fato aproveitável.
- Depois do boot, **deixe o watchdog montar o proxy** (~20s) — o badge "Proxy do emulador: OK" confirma.
  **Não** rode adb pesado em cima (force-stop/monkey/dumpsys em rajada) — churn re-degrada o estado.

## Por que pelo compose (e não pelo `docker run` / botão da UI)

O emulador do A é **gerenciado por compose** (`docker-compose.emulator-a.yml`): container `mtrx-dandroid`,
porta 6090, entrypoint self-healing, mount do `emulator.py`, sem volume. Um `docker run` (o que o antigo
botão "Resetar emulador" fazia, hoje removido) recria **ERRADO** — porta 6080, rede `bridge` (não alcança o
proxy), sem self-healing. Por isso o rebuild do A é **operação de servidor pelo compose**, não um botão.
Os stacks **B–J** usam o modelo docker-run (provisionado pela aba), então este script é **só do A**.

## Como rodar

```bash
ssh ubuntu@198.27.75.37
cd ~/MtrxSys && bash deploy/rebuild-emulator-a.sh
```

O script apaga, recria, espera o boot (~2-3 min) e confirma `sys.boot_completed=1`.

---

# Imagem-ouro: aparelho NOVO quando um chip morre

## O problema que ela resolve

Até 2026-07-25, "recriar limpo" **não limpava o aparelho** — só o container. Como o A guarda o estado
no próprio container (commit-to-image, sem volume) e a tag `budtmo/docker-android:emulator_14.0` vinha
sendo **sobrescrita com commits do device em uso**, cada rebuild **restaurava a identidade acumulada**:
`android_id`, checkin do GSF (05/07), `phoneid` do WhatsApp (06/07) e restos do número anterior.

Consequência medida: em 25/07 um chip **saudável** foi registrado às 03:21 nesse device (que horas antes
hospedara um chip restringido) e foi **restringido às 09:55 — parado, sem enviar nenhuma mensagem**.
A condição de correlação-por-device, escrita em 24/07 *antes* do fato ("só suspeitar se um número novo
morrer rápido no mesmo aparelho"), se cumpriu. Ver a memória `chip-a-restricted-anti-ban-hardening`.

## O desenho: duas tags, papéis separados

| Tag | Papel |
|---|---|
| `mtrx-android:golden` | **molde imutável** — Android + Google + WhatsApp, **nunca registrou número**. Nunca recebe commit. |
| `mtrx-android:live` | **estado de trabalho** — recebe commit depois que um chip é registrado. É pra onde o compose aponta. |
| `budtmo/docker-android:emulator_14.0` | volta a ser só a imagem de origem — **não sobrescrever mais**. |

## Os três comandos

```bash
bash deploy/build-golden-image-a.sh        # 1x: constrói o molde limpo
bash deploy/limpar-emulador-a.sh           # chip morreu -> aparelho NOVO, sem histórico
bash deploy/salvar-estado-emulador-a.sh    # chip registrado e OK -> salva (sobrevive a crash)
```

`rebuild-emulator-a.sh` continua existindo e **preserva** o chip (recria de `:live`) — é pra emulador
quebrado, não pra chip queimado. Se `:live` não existir, ele migra sozinho da imagem antiga.

## Ciclo prático quando um chip morre

1. `bash deploy/limpar-emulador-a.sh` — retaga `golden → live`, recria, espera boot **e confirma o
   proxy in-guest** (registrar antes disso manda o número pelo IP do datacenter = ban).
2. Registrar o chip novo — **uma vez**. Cada registro é um novo julgamento do WhatsApp.
3. Reconceder `READ_CONTACTS`/`WRITE_CONTACTS` (o registro **reseta** as permissões; sem elas o espelho
   `com.whatsapp` fica vazio → todo número vira "não tem WhatsApp" → **0 envios em silêncio**).
4. **Re-importar os grupos** com o chip novo (`OnlyCurrentChipContacts` pula os contatos do chip antigo).
5. `bash deploy/salvar-estado-emulador-a.sh`.
6. **Não mexer mais** — nada de reset/recreate/adb pesado.

O `limpar-emulador-a.sh` imprime os passos 2–6 ao terminar, então não é preciso decorar.

## ⚠️ O que a imagem-ouro NÃO resolve

Remove **um** fator: o histórico do aparelho. Continuam de pé: o emulador ser emulador
(`ro.hardware=ranchu`, `ro.product.model=sdk_gphone64_x86_64`, root, `ro.debuggable=1`, sem SIM) e o IP
de saída (proxy residencial compartilhado). Esses fatores já existiam quando chips duravam dias, então
não são fatais sozinhos — mas "sem risco" não é uma promessa que este desenho pode fazer.

**O benefício não é verificável diretamente:** só se descobre queimando um chip, e nem o resultado bom é
conclusivo (é possível provar que um device está marcado, nunca que está limpo). O ganho colateral é
**interpretativo**: com aparelho limpo a cada ciclo, se um chip ainda morrer, a causa passa a ser a
arquitetura (emulador-como-primário) — e a resposta vira o caminho do **aparelho vinculado** (número no
celular físico, emulador como companion) ou a API oficial.
