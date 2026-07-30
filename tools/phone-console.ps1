<#
.SYNOPSIS
  Abre o console do aparelho FÍSICO já apontado para um celular do `adb devices`.

.DESCRIPTION
  Resolve o adb, lista os aparelhos plugados, deixa você escolher um, seta as três variáveis que o
  engine `physical` exige e chama `mtrx phone console`.

  Uma janela = um aparelho. As variáveis de ambiente são POR PROCESSO, então abrir este atalho duas
  vezes e escolher seriais diferentes opera dois celulares em paralelo sem que um enxergue o outro.

  ⚠️ Não abra duas janelas no MESMO serial: o `uiautomator dump` grava num arquivo fixo dentro do
  aparelho, e os dois processos leriam a tela um do outro.

.PARAMETER Serial
  Pula o menu e usa este serial direto. Útil para criar um atalho por aparelho.

.PARAMETER AdbPath
  Caminho do adb.exe. Se omitido, tenta $env:Phone__AdbPath, depois o SDK padrão, depois o PATH.
#>
[CmdletBinding()]
param(
    [string] $Serial,
    [string] $AdbPath
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

function Resolver-Adb {
    param([string] $Informado)

    $candidatos = @()
    if ($Informado) { $candidatos += $Informado }
    if ($env:Phone__AdbPath) { $candidatos += $env:Phone__AdbPath }
    $candidatos += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe')
    $candidatos += 'C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe'

    foreach ($c in $candidatos) {
        if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path }
    }
    # Último recurso: PATH. Costuma FALHAR no Windows, porque o platform-tools do Android SDK não
    # entra no PATH por padrão. Quando falha, o engine reporta "unavailable", que é indistinguível
    # de cabo solto e manda o diagnóstico para o lado errado.
    $noPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($noPath) { return $noPath.Source }
    return $null
}

function Resolver-Mtrx {
    # Pelo MAIS RECENTE, e não por ordem de preferência de pasta: um `bin\Release\net10.0\win-x64\`
    # de meses atrás ficou parado no repo e era escolhido na frente do build de hoje, respondendo
    # "Unknown command 'phone'" — erro que parece bug do comando e é binário velho. Medido 2026-07-30.
    $bin = Join-Path $raiz 'src\MtrxSys.Cli\bin'
    if (-not (Test-Path $bin)) { return $null }
    $exe = Get-ChildItem -Path $bin -Recurse -Filter 'mtrx.exe' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1
    if (-not $exe) { return $null }

    # Binário mais velho que o código-fonte = você editou e não recompilou.
    $fonte = Get-ChildItem -Path (Join-Path $raiz 'src') -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
             Sort-Object LastWriteTime -Descending |
             Select-Object -First 1
    if ($fonte -and $fonte.LastWriteTime -gt $exe.LastWriteTime) {
        Write-Host "AVISO: $($exe.FullName) e mais antigo que o codigo-fonte." -ForegroundColor Yellow
        Write-Host '       rode: dotnet build MtrxSys.slnx -c Release' -ForegroundColor Yellow
    }

    # Roda de uma CÓPIA, nunca do bin do projeto. Enquanto o console está aberto o Windows bloqueia o
    # arquivo em uso, e todo `dotnet build` falhava com "used by another process" — o que obrigava a
    # fechar o console a cada compilacao. Copiando, as duas coisas convivem.
    $copia = Join-Path $env:LOCALAPPDATA 'MtrxSys\bin'
    $alvo = Join-Path $copia $exe.Name
    try {
        if (-not (Test-Path $copia)) { New-Item -ItemType Directory -Force $copia | Out-Null }
        if (-not (Test-Path $alvo) -or (Get-Item $alvo).LastWriteTime -lt $exe.LastWriteTime) {
            Copy-Item (Join-Path $exe.DirectoryName '*') $copia -Recurse -Force -ErrorAction Stop
        }
        return $alvo
    }
    catch {
        # Copia bloqueada (outro console rodando a mesma copia) e o alvo ja existe: usa o que esta la.
        if (Test-Path $alvo) { return $alvo }
        return $exe.FullName
    }
}

function Em-Uso {
    param([string] $Serial)

    # Mesma trava que o console segura (FileStream aberto durante toda a sessão). Tentar abrir para
    # ESCRITA é o teste: se o handle do outro console ainda existe, isto estoura. Console morto no
    # tranco não deixa trava presa, porque o Windows fecha o handle junto com o processo.
    $arquivo = Join-Path $env:LOCALAPPDATA ('MtrxSys\phone-console\{0}.lock' -f ($Serial -replace '[^a-zA-Z0-9]', '_'))
    if (-not (Test-Path $arquivo)) { return $false }
    try {
        $fs = [System.IO.File]::Open($arquivo, 'Open', 'Write', 'None')
        $fs.Close()
        return $false
    }
    catch {
        return $true
    }
}

function Listar-Aparelhos {
    param([string] $Adb)

    $saida = & $Adb devices -l
    $lista = @()
    foreach ($linha in $saida) {
        if ($linha -match '^(\S+)\s+(device|unauthorized|offline)\b(.*)$') {
            $modelo = ''
            if ($Matches[3] -match 'model:(\S+)') { $modelo = $Matches[1] }
            $serialLido = ($linha -split '\s+')[0]
            $lista += [pscustomobject]@{
                Serial = $serialLido
                Estado = ($linha -split '\s+')[1]
                Modelo = $modelo
                EmUso  = (Em-Uso -Serial $serialLido)
            }
        }
    }
    return $lista
}

# ── adb ──────────────────────────────────────────────────────────────────────────────────────────
$adb = Resolver-Adb -Informado $AdbPath
if (-not $adb) {
    Write-Host 'adb nao encontrado.' -ForegroundColor Red
    Write-Host 'Instale o platform-tools do Android SDK e rode de novo com -AdbPath "C:\...\adb.exe".'
    exit 1
}
Write-Host "adb: $adb" -ForegroundColor DarkGray

# ── mtrx ─────────────────────────────────────────────────────────────────────────────────────────
$mtrx = Resolver-Mtrx
if (-not $mtrx) {
    Write-Host 'mtrx.exe nao encontrado (bin/ e gitignored, entao o clone nao traz o executavel).' -ForegroundColor Red
    Write-Host 'Compile antes:' -ForegroundColor Yellow
    Write-Host '  dotnet build MtrxSys.slnx -c Release'
    exit 1
}

# ── aparelhos ────────────────────────────────────────────────────────────────────────────────────
# @(...) NÃO é decoração: o PowerShell desembrulha array de UM elemento no return, e um PSCustomObject
# solto não tem .Count no 5.1 — com um celular só, $aparelhos.Count vinha vazio e o script caía no
# menu de escolha com zero linhas. Medido em 2026-07-30.
$aparelhos = @(Listar-Aparelhos -Adb $adb)

if ($aparelhos.Count -eq 0) {
    Write-Host 'Nenhum aparelho no adb devices.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Confira, nesta ordem:'
    Write-Host '  1. cabo de DADOS, nao so de carga (o Explorador do Windows precisa abrir o celular)'
    Write-Host '  2. plugado direto no PC, sem hub no meio'
    Write-Host '  3. tela desbloqueada, e Depuracao USB ligada nas Opcoes do desenvolvedor'
    Write-Host '  4. na notificacao de USB do celular, escolher Transferencia de arquivos'
    exit 1
}

$livres = @($aparelhos | Where-Object { -not $_.EmUso })

$escolhido = $null
if ($Serial) {
    $escolhido = $aparelhos | Where-Object { $_.Serial -eq $Serial } | Select-Object -First 1
    if (-not $escolhido) {
        Write-Host "Serial $Serial nao esta plugado agora." -ForegroundColor Red
        exit 1
    }
    if ($escolhido.EmUso) {
        Write-Host "O aparelho $Serial ja esta aberto em outro console." -ForegroundColor Red
        exit 1
    }
}
elseif ($livres.Count -eq 0) {
    Write-Host 'Todos os aparelhos plugados ja estao abertos em outro console.' -ForegroundColor Red
    Write-Host 'Feche uma das janelas, ou plugue outro celular.'
    exit 1
}
elseif ($livres.Count -eq 1) {
    # Escolhe sozinho entre os LIVRES, nao entre os plugados: com dois celulares e um ja em uso, a
    # unica escolha possivel e obvia, e obrigar um menu de uma opcao so e ruido.
    $escolhido = $livres[0]
    if ($aparelhos.Count -gt 1) {
        Write-Host "Escolhido $($escolhido.Serial) (os demais ja estao em outro console)." -ForegroundColor DarkGray
    }
}
else {
    Write-Host ''
    Write-Host 'Aparelhos conectados:' -ForegroundColor Cyan
    for ($i = 0; $i -lt $aparelhos.Count; $i++) {
        $a = $aparelhos[$i]
        $marca = ''
        if ($a.EmUso) { $marca = '  << ja aberto em outro console' }
        $linhaMenu = "  [{0}] {1,-16} {2,-14} {3}{4}" -f ($i + 1), $a.Serial, $a.Estado, $a.Modelo, $marca
        if ($a.EmUso) { Write-Host $linhaMenu -ForegroundColor DarkGray } else { Write-Host $linhaMenu }
    }
    Write-Host ''
    $resposta = Read-Host 'Escolha o aparelho (numero)'
    $indice = 0
    if (-not [int]::TryParse($resposta, [ref] $indice) -or $indice -lt 1 -or $indice -gt $aparelhos.Count) {
        Write-Host 'Escolha invalida.' -ForegroundColor Red
        exit 1
    }
    $escolhido = $aparelhos[$indice - 1]
    if ($escolhido.EmUso) {
        Write-Host "O aparelho $($escolhido.Serial) ja esta aberto em outro console." -ForegroundColor Red
        exit 1
    }
}

if ($escolhido.Estado -ne 'device') {
    Write-Host "Aparelho $($escolhido.Serial) esta '$($escolhido.Estado)'." -ForegroundColor Red
    if ($escolhido.Estado -eq 'unauthorized') {
        Write-Host 'Aceite "Permitir depuracao USB?" na tela do celular, marcando "Sempre permitir deste computador".'
    }
    exit 1
}

# ── sobe o console preso a esse serial ───────────────────────────────────────────────────────────
$env:Phone__Engine    = 'physical'
$env:Phone__AdbSerial = $escolhido.Serial
$env:Phone__AdbPath   = $adb

# Título da janela é o que distingue duas janelas de dois aparelhos na barra de tarefas. Nem todo
# host aceita o setter, e não vale derrubar o console por causa de um enfeite.
try { $host.UI.RawUI.WindowTitle = "mtrx phone console - $($escolhido.Serial) $($escolhido.Modelo)" } catch { }

Write-Host ''
& $mtrx phone console @args
exit $LASTEXITCODE
