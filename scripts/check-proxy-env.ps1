#!/usr/bin/env pwsh
# Pre-checagem do .env (rode ANTES de recriar o waha).
# Garante que cada chip de proxy esteja COMPLETO ou VAZIO -- nunca pela metade
# (server sem credencial, ou credencial sem server), que e o unico jeito de o
# proxy quebrar a conexao de um chip de forma silenciosa.
#
# Uso:
#   ./scripts/check-proxy-env.ps1            # checa o .env da raiz
#   ./scripts/check-proxy-env.ps1 -EnvFile ./outro.env
#
# Sai com codigo 1 se achar erro (da pra encadear: ... ; if ($?) { docker compose ... }).

param([string]$EnvFile)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
if (-not $EnvFile) { $EnvFile = Join-Path $repoRoot '.env' }

if (-not (Test-Path $EnvFile)) {
    Write-Host "[ERRO] .env nao encontrado em: $EnvFile" -ForegroundColor Red
    exit 2
}

# Le o .env num dicionario KEY=VALUE (ignora comentarios e linhas vazias).
$vars = @{}
foreach ($line in Get-Content $EnvFile) {
    $t = $line.Trim()
    if ($t -eq '' -or $t.StartsWith('#')) { continue }
    $i = $t.IndexOf('=')
    if ($i -lt 1) { continue }
    $vars[$t.Substring(0, $i).Trim()] = $t.Substring($i + 1).Trim()
}

function Val($key) {
    if ($vars.ContainsKey($key)) { return $vars[$key] }
    return ''
}

$errors = 0
$warns  = 0
$active = 0

Write-Host "Checando slots de proxy em $EnvFile ..." -ForegroundColor Cyan
for ($n = 1; $n -le 10; $n++) {
    $server = Val "WAHA_PROXY_$n"
    $user   = Val "WAHA_PROXY_${n}_USER"
    $pass   = Val "WAHA_PROXY_${n}_PASS"

    $hasServer = $server -ne ''
    $hasUser   = $user   -ne ''
    $hasPass   = $pass   -ne ''

    # Vazio total = sem proxy = comportamento atual. Ok, nao polui a saida.
    if (-not $hasServer -and -not $hasUser -and -not $hasPass) { continue }

    if (-not $hasServer) {
        Write-Host "  [ERRO]  Chip ${n}: tem USER/PASS mas WAHA_PROXY_$n esta VAZIO (credencial sem servidor)." -ForegroundColor Red
        $errors++
        continue
    }

    if ($hasUser -and $hasPass) {
        Write-Host "  [OK]    Chip ${n}: proxy completo -> $server" -ForegroundColor Green
        $active++
    }
    elseif (-not $hasUser -and -not $hasPass) {
        Write-Host "  [AVISO] Chip ${n}: proxy SEM auth ($server). Confirme se o provedor dispensa user/senha (Decodo NAO dispensa)." -ForegroundColor Yellow
        $warns++
        $active++
    }
    else {
        if ($hasUser) { $miss = 'PASS' } else { $miss = 'USER' }
        Write-Host "  [ERRO]  Chip ${n}: credencial pela metade -> falta WAHA_PROXY_${n}_$miss." -ForegroundColor Red
        $errors++
    }
}

Write-Host ""
Write-Host "Resumo: $active proxy(s) ativo(s), $warns aviso(s), $errors erro(s)." -ForegroundColor Cyan
if ($errors -gt 0) {
    Write-Host "NAO recrie o waha ate corrigir os erros acima (chip quebraria silenciosamente)." -ForegroundColor Red
    exit 1
}
Write-Host "Sem erros: pode recriar o waha dos ambientes." -ForegroundColor Green
exit 0
