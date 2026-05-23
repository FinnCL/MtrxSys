#!/usr/bin/env pwsh
# Atalho pro CLI nativo. Se mtrx.exe não existe, publica primeiro.

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $scriptDir
try {
    $exe = Join-Path $scriptDir 'bin\mtrx.exe'
    if (-not (Test-Path $exe)) {
        Write-Host "Primeiro uso: publicando mtrx.exe..." -ForegroundColor Yellow
        & dotnet publish src\MtrxSys.Cli -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -o bin --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if (-not (Test-Path '.\tmp')) {
        New-Item -ItemType Directory -Path '.\tmp' | Out-Null
    }

    & $exe @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
