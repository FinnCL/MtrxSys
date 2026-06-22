# LDPlayer como "Celular virtual" na aba Celular

Usa o **LDPlayer** (emulador Android de desktop, com Play Store e **multi-instância**) como o
**aparelho principal** de cada número, e espelha a tela **na aba "Celular"** via ws-scrcpy. Cada
instância do LDPlayer = um número = um ambiente. O **disparo** continua no **WAHA** (companion).

> **Por que LDPlayer e não o emulador do SDK:** o LDPlayer é estável (o emulador cru do SDK morria com
> ANR de SystemUI). Como o ws-scrcpy só espelha um adb estável, com o LDPlayer a tela embutida tem
> chance real de funcionar — diferente do que testamos antes.

## Verdades honestas (não dá pra fugir)
- **LDPlayer é app de desktop** → ele não "vive dentro" do dashboard; o que entra na aba é a **tela
  espelhada** (ws-scrcpy). O LDPlayer roda em janela no host.
- **Hyper-V/Docker:** o Docker exige WSL2/Hyper-V ligado; com isso o LDPlayer roda em **modo
  compatível (mais lento)** e disputa RAM/CPU. Ideal: LDPlayer numa **máquina dedicada**, separada do
  Docker. Cada instância ~2–4 GB.
- **SIM:** registrar o WhatsApp exige **SMS num chip real** (o LDPlayer não tem modem). O chip entra só
  no registro/re-verificação; depois sai. Quem segura o número vivo é a **instância** (sempre ligada).
- **Ban:** WhatsApp em emulador pra disparo em massa = **alto risco**. Números descartáveis.

## Setup (uma vez)

1. **Instale o LDPlayer** e crie **1 instância por número/ambiente** (LDMultiPlayer).
2. Em **cada instância**: Configurações → Outras → **Depuração ADB = "Abrir conexão local
   (127.0.0.1)"**. Anote a **porta adb** de cada uma (1ª costuma ser **5555**; as próximas variam).
3. Em cada instância: **Play Store → instale o WhatsApp**.

## Ligar (cada vez)

```powershell
# 1) Conecta o adb das instâncias do LDPlayer e expõe pro ws-scrcpy (ajuste as portas):
powershell -ExecutionPolicy Bypass -File scripts\ldplayer-bridge.ps1 -Ports 5555,5557,5559
#    → anote os UDIDs que aparecem (ex.: 127.0.0.1:5555) — vão em PHONE_UDID_N

# 2) Sobe o ws-scrcpy (espelha a tela):
docker compose -f docker-compose.yml --profile phone-local up -d scrcpy

# 3) Aponta a aba "Celular" do ambiente pro device certo e rebuilda o web (exemplo Ambiente A):
$env:PHONE_VIEW_URL_1="http://localhost:8000"; $env:PHONE_VIEWER_KIND_1="scrcpy"; $env:PHONE_UDID_1="127.0.0.1:5555"
docker compose -p mtrxsys -f docker-compose.yml up -d --no-deps --build web
```

Na aba **"Celular"** → **Mostrar tela do Android** → a tela do LDPlayer aparece embutida. Lá dentro:
abra o WhatsApp, registre o número (SMS do chip), e **pareie o WAHA** (Aparelhos conectados → escaneie
o QR que o dashboard mostra na tela de conexão do ambiente).

## Mapa por ambiente (10 ambientes)

| Ambiente | Instância LDPlayer | UDID (ex.) | Var |
|---|---|---|---|
| A (mtrxsys) | inst 1 | `127.0.0.1:5555` | `PHONE_UDID_1` |
| B (docker-compose-2) | inst 2 | `127.0.0.1:5557` | `PHONE_UDID_2` |
| … | … | … | … |

Cada instância vincula seu WAHA (QR) e fica **sempre ligada** (é o principal daquele número). ~2–4
instâncias por máquina pela RAM; pros 10, dimensione a máquina ou divida em mais de um host.

## Fluxo completo (resumo)
```
LDPlayer inst N (WhatsApp, número N)  ──QR──>  WAHA do Ambiente N  ──>  disparo
        ▲ tela espelhada (ws-scrcpy)                  
        └── embutida na aba "Celular" do Ambiente N (PHONE_UDID_N)
SIM do número N: entra só no registro/re-verificação (depois sai).
```

## Se a tela não renderizar
- Confirme que o `scripts\ldplayer-bridge.ps1` listou o device (`adb devices`).
- Confirme o `PHONE_UDID_N` igual ao nome do device.
- Fallback: **"Abrir em nova aba"** (`localhost:8000`) e clique no device → Stream.
- LDPlayer pesado/lento = Hyper-V + disputa de RAM com o Docker → considere máquina dedicada.
