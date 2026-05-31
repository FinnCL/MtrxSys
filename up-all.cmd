@echo off
setlocal enabledelayedexpansion
pushd "%~dp0"

echo === MtrxSys: subindo 10 ambientes (A..J) + landing ===
echo.

echo --- Stack 1 (Ambiente A / Chip A) ---
docker compose up -d --build
if errorlevel 1 (
    echo Falha no Stack 1. Verifique se o Docker Desktop esta rodando.
    popd
    exit /b 1
)

for %%N in (2 3 4 5 6 7 8 9 10) do (
    echo.
    echo --- Stack %%N ---
    docker compose -f docker-compose-%%N.yml up -d --build
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
echo === Tudo no ar. Abrindo landing em http://localhost:5175 ===
start "" "http://localhost:5175"

echo.
echo Ambientes ativos (web / api / waha):
echo   Landing:  http://localhost:5175
echo   A: 5173 / 5080 / 3000   admin@local    / admin123!
echo   B: 5174 / 5081 / 3001   admin-b@local  / chipB123!
echo   C: 5176 / 5082 / 3002   admin-c@local  / chipC123!
echo   D: 5177 / 5083 / 3003   admin-d@local  / chipD123!
echo   E: 5178 / 5084 / 3004   admin-e@local  / chipE123!
echo   F: 5179 / 5085 / 3005   admin-f@local  / chipF123!
echo   G: 5180 / 5086 / 3006   admin-g@local  / chipG123!
echo   H: 5181 / 5087 / 3007   admin-h@local  / chipH123!
echo   I: 5182 / 5088 / 3008   admin-i@local  / chipI123!
echo   J: 5183 / 5089 / 3009   admin-j@local  / chipJ123!
echo.
echo Pra parar tudo: down-all.cmd
popd
endlocal
