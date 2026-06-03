@echo off
setlocal enabledelayedexpansion
pushd "%~dp0"

rem === Modo do registro compartilhado (dedup cross-chip + opt-out global) ===
rem   Observe = consulta/grava e LOGA o que faria, sem suprimir (validacao segura) <- padrao
rem   Enforce = suprime de fato (nao reenvia entre ambientes)
rem   Off     = desliga a dedup (sobe sem o recurso)
set SHARED_LEDGER_MODE=Observe

echo === MtrxSys DEV (HMR no front + registro compartilhado): 10 ambientes + landing ===
echo.
echo Front em Vite (HMR): edite src\mtrxsys-web\src\... e os 10 recarregam sozinhos.
echo Backend = build normal; ao mexer em C# rode: rebuild-backend.cmd
echo Dedup entre ambientes LIGADA em modo: %SHARED_LEDGER_MODE%  (troque no topo deste arquivo)
echo.

echo --- Banco compartilhado (registro de dedup/opt-out) ---
docker compose -f docker-compose.shared.yml up -d
if errorlevel 1 (
    echo Falha ao subir o banco compartilhado. Verifique se o Docker Desktop esta rodando.
    popd
    exit /b 1
)

echo.
echo --- Stack 1 (Ambiente A) ---
docker compose -f docker-compose.yml -f docker-compose.web.yml -f docker-compose.ledger.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 1.
    popd
    exit /b 1
)

for %%N in (2 3 4 5 6 7 8 9 10) do (
    echo.
    echo --- Stack %%N ---
    docker compose -f docker-compose-%%N.yml -f docker-compose-%%N.web.yml -f docker-compose-%%N.ledger.yml up -d --build
    if errorlevel 1 (
        echo Falha no Stack %%N.
        popd
        exit /b 1
    )
)

echo.
echo === Aguardando todas as APIs ficarem healthy (timeout ~300s) ===
set /a TRIES=0
:wait
set /a TRIES+=1
if !TRIES! gtr 150 (
    echo Timeout aguardando APIs. Veja: docker compose -f docker-compose-N.yml logs api
    popd
    exit /b 1
)
set ALLOK=1
for %%C in (mtrx-api mtrx2-api mtrx3-api mtrx4-api mtrx5-api mtrx6-api mtrx7-api mtrx8-api mtrx9-api mtrx10-api) do (
    set H=
    for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" %%C 2^>nul') do set H=%%i
    if not "!H!"=="healthy" set ALLOK=0
)
if !ALLOK!==1 goto ready
echo [!TRIES!/150] aguardando APIs ficarem healthy...
timeout /t 2 /nobreak >nul
goto wait

:ready
echo.
echo === Tudo no ar (HMR de front + dedup em %SHARED_LEDGER_MODE% nos 10). Abrindo landing ===
start "" "http://localhost:5175"
echo.
echo Dedup em Observe? confira os logs: docker compose -f docker-compose.yml -f docker-compose.ledger.yml logs dispatcher ^| findstr "ledger observe"
echo Pra parar tudo: down-all.cmd
popd
endlocal
