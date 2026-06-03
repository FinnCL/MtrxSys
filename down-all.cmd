@echo off
setlocal
pushd "%~dp0"

rem Para na ordem inversa: os ambientes mais novos primeiro, Stack 1 por último.
for %%N in (10 9 8 7 6 5 4 3 2) do (
    echo === Parando Stack %%N ===
    docker compose -f docker-compose-%%N.yml down
    echo.
)

echo === Parando Stack 1 ===
docker compose down

echo === Parando o banco compartilhado (registro de dedup/opt-out) ===
docker compose -f docker-compose.shared.yml down

echo.
echo Tudo parado. Dados persistidos nos volumes de cada stack (pgdata[N] / waha-sessions[N])
echo e do banco compartilhado (shared-pgdata).
echo Pra apagar tudo (incluindo dados), rode o down de cada stack com -v, ex.:
echo   docker compose down -v
echo   docker compose -f docker-compose-2.yml down -v       ^(... ate o -10.yml^)
echo   docker compose -f docker-compose.shared.yml down -v  ^(banco compartilhado^)
popd
endlocal
