@echo off
setlocal enabledelayedexpansion
pushd "%~dp0"

echo === MtrxSys: rebuild do backend (api + dispatcher) nos 10 ambientes ===
echo.
echo So recompila api/dispatcher; o 'web' (Vite/HMR) e os bancos seguem de pe.
echo O 1o stack compila; os demais batem no cache de build do Docker (rapido).
echo.

echo --- Stack 1 ---
docker compose up -d --build api dispatcher
if errorlevel 1 (
    echo Falha no Stack 1.
    popd
    exit /b 1
)

for %%N in (2 3 4 5 6 7 8 9 10) do (
    echo --- Stack %%N ---
    docker compose -f docker-compose-%%N.yml up -d --build api dispatcher
    if errorlevel 1 (
        echo Falha no Stack %%N.
        popd
        exit /b 1
    )
)

echo.
echo === Backend recriado nos 10. O HMR do front nao foi tocado. ===
popd
endlocal
