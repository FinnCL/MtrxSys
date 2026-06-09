# empacotar-limpo.ps1
# Copia o projeto para outra pasta/PC SEM o lixo de build (bin/obj/node_modules/
# .git/dist). Esse lixo é gerado no Windows e, ao ser copiado para outro computador,
# QUEBRA o build dentro do container Linux (erro CS5001 / "Program does not contain
# a static Main method"). Use este script em vez de copiar a pasta inteira na mão.
#
# Uso:
#   .\empacotar-limpo.ps1 -Destino "D:\MtrxSys"
#   .\empacotar-limpo.ps1 -Destino "\\OUTRO-PC\compartilhada\MtrxSys"
#
# Depois, NO OUTRO PC, dentro da pasta copiada:
#   docker compose -f docker-compose.yml up -d --build

param(
    [Parameter(Mandatory = $true)]
    [string]$Destino
)

$ErrorActionPreference = "Stop"
$origem = $PSScriptRoot

# Pastas e arquivos que NÃO devem viajar para a outra máquina.
$excluirPastas   = @("bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "tmp")
$excluirArquivos = @("*.user", ".env")

Write-Host "Copiando de:  $origem"
Write-Host "Para:         $Destino"
Write-Host "Excluindo:    $($excluirPastas -join ', ')"
Write-Host ""

# robocopy /E = subpastas (inclui vazias); /XD = exclui pastas por nome (em qualquer
# nível); /XF = exclui arquivos; flags /N* só deixam o log mais limpo.
robocopy $origem $Destino /E /XD $excluirPastas /XF $excluirArquivos /NFL /NDL /NJH /NP

# robocopy usa códigos 0..7 como SUCESSO (8+ é erro de verdade).
if ($LASTEXITCODE -ge 8) {
    throw "robocopy falhou (código $LASTEXITCODE)."
}

Write-Host ""
Write-Host "Copia limpa concluida." -ForegroundColor Green
Write-Host "No outro PC, dentro da pasta copiada, rode:" -ForegroundColor Green
Write-Host "    docker compose -f docker-compose.yml up -d --build" -ForegroundColor Green

# Zera o exit code (robocopy 0..7 nao e erro) para nao confundir quem chamar o script.
$global:LASTEXITCODE = 0
