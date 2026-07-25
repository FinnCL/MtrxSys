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

> Os **GL 0x502 são ruído normal** do render de software — aparecem mesmo num emulador são e são "ignored".
> Eles **não** são a causa do crash; a causa é o **estado corrompido + carga**. Não troque a config (GPU/
> resolução) por causa deles: a config atual (`swiftshader_indirect`, S10, 404x850) é a que funciona.

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
