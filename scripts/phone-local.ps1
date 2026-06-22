# Religa o "Android local" da aba "Celular" (Windows): boota o AVD com Play Store headless e expoe o
# adb pro container ws-scrcpy embutir a tela na aba. É o caminho que roda no SEU Windows (emulador no
# host via WHPX) — o container docker-android só roda em servidor Linux com KVM. Ver docs/phone.md.
#
# Pre-req (1x): AVD criado com imagem Play Store:
#   sdkmanager "system-images;android-34;google_apis_playstore;x86_64"
#   avdmanager create avd --name mtrxA --package "system-images;android-34;google_apis_playstore;x86_64" --device pixel_6
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File scripts\phone-local.ps1            # boota mtrxA + expoe adb
#   powershell -ExecutionPolicy Bypass -File scripts\phone-local.ps1 -Avd mtrxB
# Depois: docker compose -f docker-compose.yml --profile phone-local up -d scrcpy   (tela em :8000)
param(
    [string]$Avd = "mtrxA"
)

$ErrorActionPreference = "Stop"
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$adb = "$sdk\platform-tools\adb.exe"
$emu = "$sdk\emulator\emulator.exe"

if (-not (Test-Path $emu)) { Write-Error "Emulador nao encontrado em $emu. Crie o AVD com Play Store (ver topo)."; exit 1 }

# 1) liga o emulador headless (sem janela no desktop; a tela aparece na aba via ws-scrcpy)
if (Get-Process emulator -ErrorAction SilentlyContinue) {
    Write-Host "Emulador ja esta rodando."
} else {
    Write-Host "Iniciando o emulador '$Avd' headless..."
    Start-Process $emu -ArgumentList "-avd", $Avd, "-no-window"
}

# 2) espera o boot completar
& $adb wait-for-device
Write-Host "Aguardando o boot completar (pode levar 1-2 min)..."
do {
    Start-Sleep -Seconds 3
    $booted = (& $adb shell getprop sys.boot_completed 2>$null | Out-String).Trim()
} until ($booted -eq "1")
Write-Host "Android pronto."

# 3) expoe o adb em todas as interfaces pro container ws-scrcpy (host.docker.internal:5037)
& $adb kill-server
Start-Process $adb -ArgumentList "-a", "-P", "5037", "nodaemon", "server" -WindowStyle Hidden
Start-Sleep -Seconds 3
& $adb devices

Write-Host ""
Write-Host "Pronto. Agora suba o ws-scrcpy e abra a aba 'Celular':"
Write-Host "  docker compose -f docker-compose.yml --profile phone-local up -d scrcpy"
