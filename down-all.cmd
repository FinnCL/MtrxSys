@echo off
setlocal
pushd "%~dp0"

echo === Parando Stack 2 + landing ===
docker compose -f docker-compose-2.yml down

echo.
echo === Parando Stack 1 ===
docker compose down

echo.
echo Tudo parado. Dados persistidos nos volumes (pgdata, pgdata2, waha-sessions, waha-sessions2).
echo Pra apagar tudo (incluindo dados):
echo   docker compose down -v
echo   docker compose -f docker-compose-2.yml down -v
popd
endlocal
