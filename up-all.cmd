@echo off
setlocal
pushd "%~dp0"

echo === MtrxSys: subindo Stack 1 + Stack 2 + Stack 3 + landing ===
echo.

echo --- Stack 1 (Ambiente A / Chip A) ---
docker compose up -d --build
if errorlevel 1 (
    echo Falha no Stack 1. Verifique se o Docker Desktop esta rodando.
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
echo --- Stack 3 (Ambiente C / Chip C) ---
docker compose -f docker-compose-3.yml up -d --build
if errorlevel 1 (
    echo Falha no Stack 3.
    popd
    exit /b 1
)

echo.
echo === Aguardando todas as APIs ficarem healthy (timeout 120s) ===
set /a TRIES=0
:wait
set /a TRIES+=1
if %TRIES% gtr 60 (
    echo Timeout aguardando APIs. Veja: docker compose logs api / docker compose -f docker-compose-2.yml logs api / docker compose -f docker-compose-3.yml logs api
    popd
    exit /b 1
)
set H1=
set H2=
set H3=
for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" mtrx-api 2^>nul') do set H1=%%i
for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" mtrx2-api 2^>nul') do set H2=%%i
for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" mtrx3-api 2^>nul') do set H3=%%i
if "%H1%"=="healthy" if "%H2%"=="healthy" if "%H3%"=="healthy" goto ready
echo [%TRIES%/60] Stack1=%H1% Stack2=%H2% Stack3=%H3%
timeout /t 2 /nobreak >nul
goto wait

:ready
echo.
echo === Tudo no ar. Abrindo landing em http://localhost:5175 ===
start "" "http://localhost:5175"

echo.
echo Ambientes ativos:
echo   Landing:     http://localhost:5175  (cards de selecao de ambiente)
echo   Ambiente A:  http://localhost:5173  (admin@local / admin123!)
echo   Ambiente B:  http://localhost:5174  (admin-b@local / chipB123!)
echo   Ambiente C:  http://localhost:5176  (admin-c@local / chipC123!)
echo   API A:       http://localhost:5080/swagger
echo   API B:       http://localhost:5081/swagger
echo   API C:       http://localhost:5082/swagger
echo   WAHA A:      http://localhost:3000  (Chip A)
echo   WAHA B:      http://localhost:3001  (Chip B)
echo   WAHA C:      http://localhost:3002  (Chip C - parear no 3o celular)
echo.
echo Pra parar tudo: down-all.cmd
popd
endlocal
