# Ponte LDPlayer -> ws-scrcpy: conecta o adb das instâncias do LDPlayer e expõe o servidor adb em
# 0.0.0.0:5037 pra o container ws-scrcpy (Docker) espelhar a tela na aba "Celular".
#
# Pré-requisitos (1x no LDPlayer):
#   - LDPlayer instalado e a(s) instância(s) rodando (LDMultiPlayer pra vários números/ambientes).
#   - Em cada instância: Configurações → Outras → "Depuração ADB" = "Abrir conexão local (127.0.0.1)".
#     Anote a PORTA adb de cada instância (a 1ª costuma ser 5555; as próximas variam).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File scripts\ldplayer-bridge.ps1                 # porta 5555
#   powershell -ExecutionPolicy Bypass -File scripts\ldplayer-bridge.ps1 -Ports 5555,5557,5559
#
# Depois: o `adb devices` (e o ws-scrcpy) mostra cada instância como "127.0.0.1:<porta>".
# Esse é o UDID que vai em PHONE_UDID_1 (Ambiente A), PHONE_UDID_2 (B)… pra a aba embutir a tela certa.
param(
    [int[]]$Ports = @(5555)
)

# NÃO usar ErrorActionPreference=Stop aqui: em PS 5.1, a stderr do adb (nativo) vira erro fatal e mata
# o script no kill-server. Mantemos "Continue" e tratamos as falhas na mão.
$ErrorActionPreference = "Continue"
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$adb = "$sdk\platform-tools\adb.exe"
if (-not (Test-Path $adb)) {
    # cai pro adb do próprio LDPlayer, se o SDK não estiver instalado
    $ld = Get-ChildItem "C:\LDPlayer" -Recurse -Filter "adb.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ld) { $adb = $ld.FullName } else { Write-Host "ERRO: adb não encontrado (instale o platform-tools do Android SDK ou aponte pro adb do LDPlayer)."; exit 1 }
}

Write-Host "Usando adb: $adb"
# Expõe o servidor adb em todas as interfaces (pro container ws-scrcpy alcançar via host.docker.internal:5037).
# Sem `2>` aqui (redirecionar stderr de exe nativo em PS 5.1 dá erro fatal). O aviso "no server running" é inofensivo.
& $adb kill-server
Start-Process $adb -ArgumentList "-a", "-P", "5037", "nodaemon", "server" -WindowStyle Hidden
Start-Sleep -Seconds 3

foreach ($p in $Ports) {
    Write-Host "Conectando no LDPlayer 127.0.0.1:$p ..."
    & $adb connect "127.0.0.1:$p"
}

Write-Host ""
Write-Host "Devices visíveis (use o nome como PHONE_UDID_N):"
& $adb devices
Write-Host ""
Write-Host "Agora suba o ws-scrcpy:  docker compose -f docker-compose.yml --profile phone-local up -d scrcpy"
Write-Host "E rebuilde o web do ambiente com, ex.:"
Write-Host "  PHONE_VIEW_URL_1=http://localhost:8000 PHONE_VIEWER_KIND_1=scrcpy PHONE_UDID_1=127.0.0.1:5555 docker compose -p mtrxsys -f docker-compose.yml up -d --no-deps --build web"
