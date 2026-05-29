@echo off
setlocal
pushd "%~dp0"

echo === MtrxSys DEV: Stack 1 com HMR + Stack 2 + landing ===
echo.
echo Stack 1 sobe com docker-compose.dev.yml (dotnet watch + Vite HMR).
echo Stack 2 sobe em modo producao (sem hot-reload — eh o "ambiente espelhado").
echo Edite codigo em src\... e o Stack 1 recarrega sozinho. Stack 2 precisa rebuild.
echo.

echo --- Stack 1 (Ambiente A / Chip A) em modo dev ---
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 1.
    popd
    exit /b 1
)

echo.
echo --- Stack 2 (Ambiente B / Chip B) + landing ---
docker compose -f docker-compose-2.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 2.
    popd
    exit /b 1
)

echo.
echo === Aguardando ambas APIs ficarem healthy (timeout 240s, dev demora mais) ===
set /a TRIES=0
:wait
set /a TRIES+=1
if %TRIES% gtr 120 (
    echo Timeout aguardando APIs.
    popd
    exit /b 1
)
set H1=
set H2=
for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" mtrx-api 2^>nul') do set H1=%%i
for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" mtrx2-api 2^>nul') do set H2=%%i
if "%H1%"=="healthy" if "%H2%"=="healthy" goto ready
echo [%TRIES%/120] Stack1=%H1% Stack2=%H2%
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
echo   Ambiente B:  http://localhost:5174  (build estatico)
echo.
echo Pra parar tudo: down-all.cmd
popd
endlocal
