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

function Build-Utilizavel {
    param([string] $Pasta, [string] $NomeExe)

    # Ter o `mtrx.exe` na pasta NAO prova que aquele build roda: o host do .NET so levanta se os dois
    # arquivos de configuracao ao lado dele estiverem inteiros, e uma copia interrompida no meio deixa
    # arquivo com ZERO byte. O sintoma e "JSON parsing exception: The document is empty" e codigo
    # -2147450733, que tem cara de .NET quebrado na maquina e nao e: e o nosso arquivo pela metade.
    # Medido em 2026-08-07.
    #
    # Os DOIS json entram na conferencia porque falham igual: o runtimeconfig diz qual runtime carregar
    # e o deps diz quais assemblies existem. Conferir so um deixava metade do defeito passar.
    $base = Join-Path $Pasta ([IO.Path]::GetFileNameWithoutExtension($NomeExe))
    $exe = Join-Path $Pasta $NomeExe
    if (-not (Test-Path -LiteralPath $exe)) { return $false }
    if ((Get-Item -LiteralPath $exe).Length -eq 0) { return $false }

    # Sem a dll ao lado, isto e um publish self-contained de arquivo unico: os json moram DENTRO do
    # exe e cobrar os dois aqui reprovaria um build que funciona. So o layout normal e conferido.
    if (-not (Test-Path -LiteralPath "$base.dll")) { return $true }
    if ((Get-Item -LiteralPath "$base.dll").Length -eq 0) { return $false }

    if (-not (Test-Path -LiteralPath "$base.runtimeconfig.json")) { return $false }
    foreach ($json in @("$base.runtimeconfig.json", "$base.deps.json")) {
        # O deps.json pode faltar de forma legitima (o host cai no probing da propria pasta). Vazio ou
        # corrompido, nao: ai o host aborta igual ao runtimeconfig.
        if (-not (Test-Path -LiteralPath $json)) { continue }
        if ((Get-Item -LiteralPath $json).Length -eq 0) { return $false }
        try { Get-Content -LiteralPath $json -Raw | ConvertFrom-Json | Out-Null } catch { return $false }
    }
    return $true
}

function Copia-Valida {
    param([string] $Pasta, [string] $NomeExe)

    # Marcador gravado por ULTIMO na copia. Ausente = a copia parou no meio do caminho, mesmo que os
    # arquivos que interessam por acaso tenham chegado inteiros.
    if (-not (Test-Path -LiteralPath (Join-Path $Pasta '.copia-completa'))) { return $false }
    return (Build-Utilizavel -Pasta $Pasta -NomeExe $NomeExe)
}

function Exe-Em-Uso {
    param([string] $Caminho)

    # Windows tranca o binario enquanto o processo vive, entao "nao consigo abrir para escrita" =
    # "tem console rodando isto agora". E o mesmo teste do Em-Uso das travas de serial, e e a razao
    # original de rodarmos de uma copia: com o console aberto, o `dotnet build` esbarrava nessa trava.
    if (-not (Test-Path -LiteralPath $Caminho)) { return $false }
    try {
        $fs = [System.IO.File]::Open($Caminho, 'Open', 'Write', 'None')
        $fs.Close()
        return $false
    }
    catch { return $true }
}

function Limpar-Copias {
    param([string] $Raiz, [string] $Manter, [string] $NomeExe)

    # Cada build ganha uma pasta nova, entao sem faxina o LOCALAPPDATA cresce sem parar. Tambem leva
    # embora o lixo do formato antigo, quando os arquivos ficavam soltos direto no bin\.
    #
    # Pular a pasta EM USO nao e educacao, e obrigacao: uma janela pode estar operando o celular 1 a
    # partir da pasta antiga enquanto voce recompila e abre a janela do celular 2. O `Remove-Item`
    # falha no exe travado mas APAGA o resto, e o .NET carrega assembly sob demanda: a janela aberta
    # so quebraria mais tarde, no comando que precisasse da dll que sumiu debaixo dela. Erro assim
    # nao tem como ser ligado de volta a causa.
    if (-not (Test-Path -LiteralPath $Raiz)) { return }
    $legadoEmUso = Exe-Em-Uso -Caminho (Join-Path $Raiz $NomeExe)

    Get-ChildItem -LiteralPath $Raiz -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $Manter } |
        ForEach-Object {
            if ($_.PSIsContainer) {
                if (Exe-Em-Uso -Caminho (Join-Path $_.FullName $NomeExe)) { return }
            }
            elseif ($legadoEmUso) { return }
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

function Resolver-Mtrx {
    # Pelo MAIS RECENTE, e não por ordem de preferência de pasta: um `bin\Release\net10.0\win-x64\`
    # de meses atrás ficou parado no repo e era escolhido na frente do build de hoje, respondendo
    # "Unknown command 'phone'" — erro que parece bug do comando e é binário velho. Medido 2026-07-30.
    $bin = Join-Path $raiz 'src\MtrxSys.Cli\bin'
    if (-not (Test-Path -LiteralPath $bin)) { return $null }
    $candidatos = @(Get-ChildItem -LiteralPath $bin -Recurse -Filter 'mtrx.exe' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending)
    if ($candidatos.Count -eq 0) { return $null }

    # O build de ORIGEM tambem pode estar quebrado, e nesse caso copiar so espalha o defeito. Acontece
    # quando o projeto e trazido de outro PC com o `bin\` junto: o `bin\` e lixo de build, nao viaja
    # bem, e uma copia de rede que morre no meio deixa arquivo com zero byte do lado de ca. E por isso
    # que o empacotar-limpo.ps1 exclui `bin` e `obj` da mudanca. Medido em 2026-08-07.
    $exe = $candidatos | Where-Object { Build-Utilizavel -Pasta $_.DirectoryName -NomeExe $_.Name } |
           Select-Object -First 1
    if (-not $exe) {
        Write-Host 'O mtrx.exe existe, mas o build ao lado dele esta incompleto.' -ForegroundColor Red
        Write-Host 'Falta o mtrx.runtimeconfig.json, ou ele esta vazio. Sem esse arquivo o .NET nao levanta o programa.'
        Write-Host ''
        Write-Host 'Quase sempre e bin\ trazido de outro PC. Recompile nesta maquina:' -ForegroundColor Yellow
        Write-Host '  dotnet build MtrxSys.slnx -c Release' -ForegroundColor Yellow
        Write-Host 'Se insistir, apague a pasta src\MtrxSys.Cli\bin antes de compilar.'
        # Sai daqui em vez de devolver $null: quem chama trata $null como "nao compilou ainda" e
        # imprimiria por cima um recado diferente do problema real.
        exit 1
    }

    # Cair num build mais antigo porque o mais novo esta quebrado nao pode ser silencioso: e assim que
    # nasce o "Unknown command 'phone'" do comentario la de cima, onde o binario velho responde e o
    # erro parece do comando. Melhor rodar avisando do que travar tudo, mas avisando alto.
    if ($exe.FullName -ne $candidatos[0].FullName) {
        Write-Host "AVISO: o build mais novo ($($candidatos[0].FullName)) esta incompleto e foi ignorado." -ForegroundColor Yellow
        Write-Host "       Rodando o anterior: $($exe.FullName)" -ForegroundColor Yellow
        Write-Host '       Recompile com: dotnet build MtrxSys.slnx -c Release' -ForegroundColor Yellow
    }

    # Identidade do build = arquivo mais novo + soma dos tamanhos da pasta de saida, e nao a data do
    # mtrx.exe sozinho. O apphost mtrx.exe nao e regravado quando so uma dependencia muda: mexer em
    # MtrxSys.Core e recompilar troca a Core.dll e deixa o exe com a data velha. Pela data do exe, a
    # copia de ontem parecia atual e o console rodava a Core antiga, que e a mesma armadilha de
    # binario velho de 2026-07-30. Pela pasta inteira, qualquer recompilacao muda a chave.
    $arquivos = @(Get-ChildItem -LiteralPath $exe.DirectoryName -File -Force -ErrorAction SilentlyContinue)
    $carimbo = ($arquivos | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    $soma = ($arquivos | Measure-Object -Property Length -Sum).Sum

    # Binário mais velho que o código-fonte = você editou e não recompilou. Compara com o carimbo da
    # PASTA pelo mesmo motivo: contra a data do exe, editar Core.cs e recompilar disparava o aviso
    # "recompile" logo depois de voce ter recompilado.
    $fonte = Get-ChildItem -LiteralPath (Join-Path $raiz 'src') -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
             Sort-Object LastWriteTimeUtc -Descending |
             Select-Object -First 1
    if ($fonte -and $fonte.LastWriteTimeUtc -gt $carimbo) {
        Write-Host "AVISO: $($exe.FullName) e mais antigo que o codigo-fonte." -ForegroundColor Yellow
        Write-Host '       rode: dotnet build MtrxSys.slnx -c Release' -ForegroundColor Yellow
    }

    # Roda de uma CÓPIA, nunca do bin do projeto. Enquanto o console está aberto o Windows bloqueia o
    # arquivo em uso, e todo `dotnet build` falhava com "used by another process" — o que obrigava a
    # fechar o console a cada compilacao. Copiando, as duas coisas convivem.
    #
    # A copia vai para uma pasta POR BUILD (carimbo de data + tamanho do exe de origem), e nao para
    # uma pasta fixa. A pasta fixa se sobrescrevia: se a copia morria no meio — janela fechada, exe
    # travado por outro console, antivirus segurando um arquivo — sobrava uma mistura de arquivos
    # novos, velhos e truncados, e o `catch` entregava essa mistura como se estivesse boa. Pasta nova
    # por build nunca escreve por cima do que ja esta em uso, e o que sobrou de uma tentativa morta
    # e apagado antes de recomecar em vez de ser herdado. Medido em 2026-08-07.
    # Sem LOCALAPPDATA nao ha para onde copiar. Acontece em conta de servico e sessao sem perfil, e o
    # Join-Path com nulo estoura em vermelho antes de qualquer diagnostico util.
    if (-not $env:LOCALAPPDATA) {
        Write-Host 'AVISO: LOCALAPPDATA nao esta definido. Rodando direto do bin do projeto.' -ForegroundColor Yellow
        return $exe.FullName
    }

    $raizCopias = Join-Path $env:LOCALAPPDATA 'MtrxSys\bin'
    $copia = Join-Path $raizCopias ('v-{0}-{1}' -f $carimbo.ToString('yyyyMMdd-HHmmss'), $soma)
    $alvo = Join-Path $copia $exe.Name

    if (Copia-Valida -Pasta $copia -NomeExe $exe.Name) {
        Limpar-Copias -Raiz $raizCopias -Manter $copia -NomeExe $exe.Name
        return $alvo
    }

    # Duas janelas abertas ao mesmo tempo cairiam na MESMA pasta e copiariam em paralelo, uma
    # truncando o arquivo que a outra acabou de gravar. Com a trava, a segunda espera e acorda com a
    # copia ja pronta. Mutex abandonado = a outra janela morreu segurando a trava; a pasta esta
    # suspeita, e e exatamente o caso que o Copia-Valida abaixo pega.
    $tranca = New-Object System.Threading.Mutex($false, 'MtrxSys-phone-console-copia')
    $peguei = $false
    $motivo = $null
    try {
        try { $peguei = $tranca.WaitOne(120000) }
        catch [System.Threading.AbandonedMutexException] { $peguei = $true }

        # Sem a trava na mao, NAO copia. Copiar assim mesmo apagaria a pasta que a outra janela esta
        # enchendo neste instante, que e exatamente a corrupcao que este codigo existe para evitar.
        if (-not $peguei) {
            $motivo = 'outra janela ficou mais de 2 minutos preparando a copia'
        }
        elseif (-not (Copia-Valida -Pasta $copia -NomeExe $exe.Name)) {
            # Restos de uma tentativa que morreu no meio. Apagar e seguro aqui: pasta invalida nunca
            # foi entregue para ninguem rodar, e a trava garante que ninguem esta enchendo ela agora.
            if (Test-Path -LiteralPath $copia) { Remove-Item -LiteralPath $copia -Recurse -Force -ErrorAction SilentlyContinue }
            New-Item -ItemType Directory -Force $copia | Out-Null
            try {
                # O marcador e ignorado na ORIGEM: se alguem copiar uma copia de volta para dentro do
                # src, ele viajaria junto no meio dos arquivos e daria "copia completa" antes da hora,
                # que e justamente a mentira que ele existe para impedir.
                Get-ChildItem -LiteralPath $exe.DirectoryName -File -Force |
                    Where-Object { $_.Name -ne '.copia-completa' } |
                    Copy-Item -Destination $copia -Force -ErrorAction Stop

                # Subpasta com um mtrx.exe dentro e OUTRO build (o win-x64 self-contained mora dentro
                # do net10.0 e sozinho tem 92 MB). Copiar aquilo triplicaria o tempo de abertura para
                # levar junto um binario que nunca vai rodar.
                Get-ChildItem -LiteralPath $exe.DirectoryName -Directory -Force |
                    Where-Object { -not (Test-Path -LiteralPath (Join-Path $_.FullName $exe.Name)) } |
                    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $copia -Recurse -Force -ErrorAction Stop }

                Set-Content -LiteralPath (Join-Path $copia '.copia-completa') -Value $exe.FullName
            }
            catch {
                # Guarda o motivo em vez de engolir: disco cheio, permissao e antivirus dao mensagens
                # diferentes, e sem elas o aviso final vira adivinhacao.
                $motivo = $_.Exception.Message
            }
        }
    }
    finally {
        if ($peguei) { $tranca.ReleaseMutex() }
        $tranca.Dispose()
    }

    if (Copia-Valida -Pasta $copia -NomeExe $exe.Name) {
        Limpar-Copias -Raiz $raizCopias -Manter $copia -NomeExe $exe.Name
        return $alvo
    }

    # Copiar ficou impossivel (disco cheio, permissao, antivirus). Roda direto do bin do projeto: o
    # preco e o `dotnet build` reclamar de arquivo em uso enquanto este console estiver aberto. Uma
    # copia quebrada nao tem preco nenhum, so o erro do host do .NET.
    Write-Host 'AVISO: nao consegui preparar a copia em LOCALAPPDATA. Rodando direto do bin do projeto.' -ForegroundColor Yellow
    if ($motivo) { Write-Host "       Motivo: $motivo" -ForegroundColor Yellow }
    Write-Host '       Feche este console antes de rodar dotnet build.' -ForegroundColor Yellow
    return $exe.FullName
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
