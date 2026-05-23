@echo off
setlocal
pushd "%~dp0"

echo === MtrxSys DEV: subindo containers com HMR ===
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
if errorlevel 1 (
    echo Falha no docker compose. Verifique se o Docker Desktop esta rodando.
    popd
    exit /b 1
)

echo.
echo === Aguardando API ficar healthy (timeout 120s) ===
set /a TRIES=0
:wait
set /a TRIES+=1
if %TRIES% gtr 60 (
    echo Timeout aguardando a API. Veja: docker compose logs api
    popd
    exit /b 1
)
for /f "tokens=*" %%i in ('docker inspect -f "{{.State.Health.Status}}" mtrx-api 2^>nul') do set HEALTH=%%i
if not "%HEALTH%"=="healthy" (
    echo [%TRIES%/60] status=%HEALTH%
    timeout /t 2 /nobreak >nul
    goto wait
)

echo.
echo === API healthy. Abrindo http://localhost:5173 ===
start "" "http://localhost:5173"

echo.
echo Modo DEV ativo. Hot reload habilitado.
echo   Web:        http://localhost:5173  (Vite dev server, HMR ligado)
echo   API:        http://localhost:5080/swagger
echo   WAHA UI:    http://localhost:3000
echo.
echo Edite arquivos em src\mtrxsys-web\src\... e o browser recarrega sozinho.
echo Pra parar: docker compose down
popd
endlocal
