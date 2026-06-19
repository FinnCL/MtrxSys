#!/usr/bin/env pwsh
# Confere, por chip JA RODANDO, se o proxy entrou no CONFIG DA SESSAO do WAHA.
# Rode DEPOIS de recriar a api.
#
# Uso:
#   ./scripts/check-proxy-live.ps1               # todos os *waha de pe
#   ./scripts/check-proxy-live.ps1 -Chips 1,2    # so chips 1 e 2
#   ./scripts/check-proxy-live.ps1 -Session default
#
# POR QUE config da sessao (e nao a env var): o WAHA 2026.x (CORE/NOWEB) IGNORA a env var
# WHATSAPP_PROXY_SERVER. O proxy so pega via config.proxy da sessao, injetado pela api. Este
# script le esse config direto da API do WAHA (de dentro do container, usando a key dele).
#
# IMPORTANTE: a prova DEFINITIVA do IP de saida e o painel da Decodo (trafego no IP alugado
# quando o chip conecta). Aqui confirmamos que o proxy esta GRAVADO no config da sessao.

param([int[]]$Chips = @(), [string]$Session = 'default')

function ContainerFor([int]$n) {
    if ($n -eq 1) { return 'mtrx-waha' }
    return "mtrx$n-waha"
}

function ComposeFor([int]$n) {
    if ($n -eq 1) { return 'docker-compose.yml' }
    return "docker-compose-$n.yml"
}

# JS rodado DENTRO do container (node sempre existe na imagem do WAHA): le config.proxy da sessao.
$js = @'
const k=process.env.WHATSAPP_API_KEY||'';
const s=process.argv[1]||'default';
require('http').get({host:'localhost',port:3000,path:'/api/sessions/'+s,headers:{'X-Api-Key':k}},r=>{
  let d='';r.on('data',c=>d+=c);r.on('end',()=>{
    try{const j=JSON.parse(d);const p=j&&j.config&&j.config.proxy;
      console.log(p&&p.server?('PROXY:'+p.server):'NOPROXY');}
    catch(e){console.log('PARSEFAIL:'+d.slice(0,80));}
  });
}).on('error',e=>console.log('ERR:'+e.message));
'@

if ($Chips.Count -eq 0) {
    $running = docker ps --filter "name=waha" --format "{{.Names}}"
    if (-not $running) { Write-Host "Nenhum container *waha rodando." -ForegroundColor Yellow; exit 0 }
    foreach ($name in $running) {
        if ($name -eq 'mtrx-waha') { $Chips += 1 }
        elseif ($name -match '^mtrx(\d+)-waha$') { $Chips += [int]$Matches[1] }
    }
    $Chips = @($Chips | Sort-Object -Unique)
}

foreach ($n in $Chips) {
    $c = ContainerFor $n
    Write-Host ""
    Write-Host "==== Chip $n ($c) — sessao '$Session' ====" -ForegroundColor Cyan

    if (-not (docker ps -q -f "name=^/$c$")) {
        Write-Host "  [AVISO] container nao esta rodando." -ForegroundColor Yellow
        continue
    }

    $out = (docker exec $c node -e $js $Session) 2>$null
    if ($out -is [array]) { $out = $out -join '' }
    $out = "$out".Trim()

    if ($out -like 'PROXY:*') {
        Write-Host "  [OK] proxy no config da sessao -> $($out.Substring(6))" -ForegroundColor Green
    }
    elseif ($out -eq 'NOPROXY') {
        Write-Host "  [SEM PROXY] a sessao sai pelo IP da maquina." -ForegroundColor Yellow
        Write-Host "  (preencheu o .env? recrie a api: docker compose -f $(ComposeFor $n) up -d --force-recreate api)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  [?] resposta inesperada: $out" -ForegroundColor Red
    }

    Write-Host "  --- logs recentes (proxy/conexao) ---" -ForegroundColor DarkGray
    $logs = docker logs $c --tail 200 2>&1 |
        Select-String -SimpleMatch -Pattern 'proxy','connected to WA','ECONN','timeout','WORKING','FAILED','STOPPED'
    if ($logs) { $logs | Select-Object -Last 10 | ForEach-Object { Write-Host "    $_" } }
    else { Write-Host "    (nada relevante nos ultimos 200 logs)" -ForegroundColor DarkGray }
}

Write-Host ""
Write-Host "Prova DEFINITIVA do IP: painel da Decodo deve mostrar trafego no IP alugado do chip." -ForegroundColor Cyan
Write-Host "E teste o opt-out: mande 'SAIR' e confirme que o contato vira 'Saiu'." -ForegroundColor Cyan
