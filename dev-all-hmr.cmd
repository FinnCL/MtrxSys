@echo off
setlocal enabledelayedexpansion
pushd "%~dp0"

echo === MtrxSys DEV (HMR COMPLETO): 10 ambientes, front + backend em tempo real ===
echo.
echo Todos os 10 sobem com Vite HMR (front) + dotnet watch (backend). Build feito UMA vez:
echo depois, editar .tsx ou .cs atualiza sozinho, sem rebuild.
echo PESADO: ~10 Vite + 20 dotnet watch + 10 WAHA/Chromium. Precisa de bastante RAM.
echo.

echo --- Stack 1 (Ambiente A) ---
docker compose -f docker-compose.yml -f docker-compose.web.yml -f docker-compose.backend-dev.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 1. Verifique se o Docker Desktop esta rodando.
    popd
    exit /b 1
)

for %%N in (2 3 4 5 6 7 8 9 10) do (
    echo.
    echo --- Stack %%N ---
    docker compose -f docker-compose-%%N.yml -f docker-compose-%%N.web.yml -f docker-compose.backend-dev.yml up -d --build
    if errorlevel 1 (
        echo Falha no Stack %%N.
        popd
        exit /b 1
    )
)

echo.
echo === Aguardando todas as APIs ficarem healthy (timeout ~480s; 1o build dev demora) ===
set /a TRIES=0
:wait
set /a TRIES+=1
if !TRIES! gtr 240 (
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
echo [!TRIES!/240] aguardando APIs ficarem healthy...
timeout /t 2 /nobreak >nul
goto wait

:ready
echo.
echo === Tudo no ar (HMR completo nos 10). Abrindo landing em http://localhost:5175 ===
start "" "http://localhost:5175"
echo.
echo Agora e so editar: front (src\mtrxsys-web\src) ou backend (src\MtrxSys.*) recarrega sozinho.
echo Pra parar tudo: down-all.cmd
popd
endlocal
