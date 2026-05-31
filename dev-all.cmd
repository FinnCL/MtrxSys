@echo off
setlocal enabledelayedexpansion
pushd "%~dp0"

echo === MtrxSys DEV: Stack 1 com HMR + Stacks 2..10 + landing ===
echo.
echo Stack 1 sobe com docker-compose.dev.yml (dotnet watch + Vite HMR).
echo Stacks 2..10 sobem em modo producao (sem hot-reload — ambientes espelhados).
echo Edite codigo em src\... e o Stack 1 recarrega sozinho. Os outros precisam rebuild.
echo.

echo --- Stack 1 (Ambiente A / Chip A) em modo dev ---
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 1.
    popd
    exit /b 1
)

for %%N in (2 3 4 5 6 7 8 9 10) do (
    echo.
    echo --- Stack %%N (modo producao) ---
    docker compose -f docker-compose-%%N.yml up -d --build
    if errorlevel 1 (
        echo Falha no Stack %%N.
        popd
        exit /b 1
    )
)

echo.
echo === Aguardando todas as APIs ficarem healthy (timeout ~360s, dev demora mais) ===
set /a TRIES=0
:wait
set /a TRIES+=1
if !TRIES! gtr 180 (
    echo Timeout aguardando APIs.
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
echo [!TRIES!/180] aguardando APIs ficarem healthy...
timeout /t 2 /nobreak >nul
goto wait

:ready
echo.
echo === Tudo no ar. Abrindo landing em http://localhost:5175 ===
start "" "http://localhost:5175"

echo.
echo Ambientes ativos:
echo   Landing:     http://localhost:5175
echo   Ambiente A:  http://localhost:5173  (Vite dev server, HMR ligado)
echo   Ambientes B..J: webs 5174, 5176..5183 (build estatico)
echo.
echo Pra parar tudo: down-all.cmd
popd
endlocal
