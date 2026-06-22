@echo off
setlocal
pushd "%~dp0"

rem ==== Sobe A e B (WhatsApp EMULADO ja e o padrao em A/B) ====
rem Nada de comando/imagem extra: o servico waha de A/B usa a imagem do emulador por padrao e o
rem servico `waha-emulator-build` gera essa imagem no proprio `up --build`. O simulador aparece
rem como a aba "Celular" dentro do dashboard.
rem Pra VOLTAR ao WhatsApp real, suba com:  set WAHA_IMAGE=devlikeapro/waha:latest

echo === Ambiente A ===
docker compose -f docker-compose.yml up -d --build
if errorlevel 1 (
    echo Falha ao subir o Ambiente A. Verifique se o Docker Desktop esta rodando.
    popd
    exit /b 1
)

echo.
echo === Ambiente B ===
docker compose -f docker-compose-2.yml up -d --build
if errorlevel 1 (
    echo Falha ao subir o Ambiente B.
    popd
    exit /b 1
)

echo.
echo Tudo no ar (A e B com WhatsApp EMULADO).
echo   Landing:     http://localhost:5175
echo   Web A:       http://localhost:5173   (admin@local / admin123!)   -^> aba "Celular"
echo   Web B:       http://localhost:5174   (admin-b@local / chipB123!) -^> aba "Celular"
echo.
echo O simulador de celular fica DENTRO do dashboard, na aba "Celular".
echo (O painel tambem responde direto em http://localhost:3000 / 3001.)
popd
endlocal
