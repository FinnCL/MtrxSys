@echo off
setlocal enabledelayedexpansion
pushd "%~dp0"

echo === MtrxSys DEV (HMR no front): 10 ambientes + landing ===
echo.
echo Os 10 sobem com o 'web' em Vite dev server (HMR). Edite src\mtrxsys-web\src\...
echo e os 10 recarregam sozinhos. O backend roda como build normal (sem dotnet watch).
echo Ao mexer em C# (src\MtrxSys.*\...), rode:  rebuild-backend.cmd
echo.

echo --- Stack 1 (Ambiente A) ---
docker compose -f docker-compose.yml -f docker-compose.web.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 1. Verifique se o Docker Desktop esta rodando.
    popd
    exit /b 1
)

for %%N in (2 3 4 5 6 7 8 9 10) do (
    echo.
    echo --- Stack %%N ---
    docker compose -f docker-compose-%%N.yml -f docker-compose-%%N.web.yml up -d --build
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
echo === Tudo no ar (HMR de front nos 10). Abrindo landing em http://localhost:5175 ===
start "" "http://localhost:5175"
echo.
echo Pra parar tudo: down-all.cmd
popd
endlocal
