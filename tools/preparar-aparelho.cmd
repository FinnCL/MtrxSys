@echo off
rem ============================================================================
rem  Prepara um celular Android novo para o console do aparelho fisico.
rem
rem  Faz, nesta ordem: acha o adb (e INSTALA o platform-tools se faltar), confere
rem  que o celular esta conectado e autorizado, baixa o ADB Keyboard, instala e
rem  habilita. Uma vez por APARELHO, em cada PC.
rem
rem  Por que o teclado: com digitacao humana ligada o sistema escreve pelo
rem  `input text` do Android, que so aceita ASCII. Mensagem com emoji OU ACENTO
rem  faz o pre-voo abortar o lote inteiro. O ADB Keyboard e um metodo de entrada
rem  que recebe texto por broadcast, entao digita qualquer caractere e o
rem  destinatario continua vendo "digitando...". Ver docs/aparelho-fisico-passo-a-passo.md.
rem
rem  Uso:
rem    tools\preparar-aparelho.cmd                  (um celular plugado)
rem    tools\preparar-aparelho.cmd RQ8WB048RFW      (escolhe por serial)
rem
rem  Sem acentos de proposito: o console usa codepage 850 e acento em .cmd vira
rem  caractere quebrado na tela.
rem ============================================================================

setlocal EnableExtensions EnableDelayedExpansion
title MtrxSys - preparar aparelho

set "APK_URL=https://github.com/senzhk/ADBKeyBoard/raw/master/ADBKeyboard.apk"
set "PT_URL=https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
set "IME=com.android.adbkeyboard/.AdbIME"
set "PACOTE=com.android.adbkeyboard"
set "SERIAL=%~1"
set "FALHOU="
set "LISTA=%TEMP%\mtrx-devices.txt"

echo.
echo ============================================================
echo   MtrxSys - preparar aparelho (ADB Keyboard)
echo ============================================================
echo.

rem ---------------------------------------------------------------------------
rem  1) adb. Instala o platform-tools se nao achar em nenhum lugar conhecido.
rem ---------------------------------------------------------------------------
echo [1/5] Procurando o adb...

set "ADB="
if defined Phone__AdbPath if exist "%Phone__AdbPath%" set "ADB=%Phone__AdbPath%"
if not defined ADB if exist "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" set "ADB=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
if not defined ADB if exist "%ProgramFiles(x86)%\Android\android-sdk\platform-tools\adb.exe" set "ADB=%ProgramFiles(x86)%\Android\android-sdk\platform-tools\adb.exe"
if not defined ADB for /f "delims=" %%A in ('where adb 2^>nul') do if not defined ADB set "ADB=%%A"

if defined ADB goto :temadb

echo       Nao achei. Baixando o platform-tools...
curl -L -o "%TEMP%\platform-tools.zip" "%PT_URL%"
if errorlevel 1 (
    call :fatal "Nao consegui baixar o platform-tools." "Confira a internet, ou baixe na mao em https://developer.android.com/tools/releases/platform-tools"
    goto :fim
)
if not exist "%LOCALAPPDATA%\Android\Sdk" mkdir "%LOCALAPPDATA%\Android\Sdk"
tar -xf "%TEMP%\platform-tools.zip" -C "%LOCALAPPDATA%\Android\Sdk"
set "ADB=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
if not exist "!ADB!" (
    call :fatal "O platform-tools baixou mas o adb.exe nao apareceu." "Extraia na mao para %LOCALAPPDATA%\Android\Sdk\platform-tools\"
    goto :fim
)
echo       platform-tools instalado.

:temadb
echo       adb: !ADB!

rem ---------------------------------------------------------------------------
rem  2) O celular precisa aparecer como "device". "unauthorized" e o caso mais
rem     comum e tem conserto proprio, entao vale distinguir dos outros.
rem ---------------------------------------------------------------------------
echo.
echo [2/5] Procurando o aparelho...

"!ADB!" devices > "%LISTA%" 2>nul
set "ACHOU="
set "NAOAUTH="
set "DETECTADO="
for /f "skip=1 tokens=1,2" %%a in ('type "%LISTA%"') do (
    if "%%b"=="device" (
        set "ACHOU=1"
        if not defined DETECTADO set "DETECTADO=%%a"
    )
    if "%%b"=="unauthorized" set "NAOAUTH=1"
)

rem ATENCAO: NENHUM PARENTESE nas mensagens daqui pra baixo, nem entre aspas: dentro de um
rem bloco if ^( ^) um ")" solto fecha o bloco antes da hora, o goto :fim deixa de fazer
rem parte dele e a execucao cai direto na subrotina. Sintoma medido em 2026-08-05: a
rem caixa de erro saiu DUAS vezes, a segunda vazia, e o codigo de saida veio 0 em vez
rem de 1. Por isso a lista abaixo usa "1." e nao "1)".
rem Estrutura chapada pelo mesmo motivo: goto dentro de bloco e onde isso morde.
if defined ACHOU goto :temaparelho
if defined NAOAUTH (
    call :fatal "O celular esta como unauthorized." "Olhe a TELA DO CELULAR: tem um pop-up 'Permitir depuracao USB?' esperando. Aceite marcando 'Sempre permitir deste computador'."
) else (
    call :fatal "Nenhum aparelho no adb devices." "Confira nesta ordem: 1. cabo de DADOS, nao so de carga. 2. plugado direto no PC, sem hub. 3. tela desbloqueada e Depuracao USB ligada. 4. na notificacao de USB do celular, escolher Transferencia de arquivos."
)
goto :fim

:temaparelho

rem Sem serial informado, usa o que foi detectado. Com serial, o -s manda.
if not defined SERIAL set "SERIAL=!DETECTADO!"
set "ADBS="!ADB!" -s !SERIAL!"
echo       aparelho: !SERIAL!

rem ---------------------------------------------------------------------------
rem  3) APK. A checagem existe porque um 404 do GitHub baixa uma PAGINA HTML com
rem     nome de .apk, e o `adb install` falharia com um erro que nao aponta pra
rem     causa.
rem
rem     NAO use tamanho grande como criterio: medido em 2026-08-05, o
rem     ADBKeyboard.apk legitimo tem 17 KB. Ele e um IME minusculo, sem recursos
rem     graficos. Um limite de 100 KB rejeitava o arquivo CERTO. O criterio que
rem     vale e o conteudo: pagina de erro tem "<html" dentro, APK nao.
rem ---------------------------------------------------------------------------
echo.
echo [3/5] Baixando o ADB Keyboard...

set "APK=%TEMP%\ADBKeyboard.apk"
curl -L -o "%APK%" "%APK_URL%"
if errorlevel 1 (
    call :fatal "Nao consegui baixar o APK." "Baixe na mao em https://github.com/senzhk/ADBKeyBoard e rode: adb install ADBKeyboard.apk"
    goto :fim
)

for %%F in ("%APK%") do set /a TAM=%%~zF/1024

set "RUIM="
if !TAM! lss 5 set "RUIM=tem so !TAM! KB"
findstr /m /i /c:"<html" /c:"<!DOCTYPE" "%APK%" >nul 2>nul
if not errorlevel 1 set "RUIM=e uma pagina HTML, nao um APK"

if defined RUIM (
    call :fatal "O arquivo baixado !RUIM!." "A URL provavelmente mudou. Baixe o ADBKeyboard.apk na mao em https://github.com/senzhk/ADBKeyBoard e rode: adb install ADBKeyboard.apk"
) else (
    echo       baixado: !TAM! KB
    goto :apkok
)
goto :fim

:apkok

rem ---------------------------------------------------------------------------
rem  4) Instalar. -r reinstala por cima, entao rodar de novo e seguro.
rem ---------------------------------------------------------------------------
echo.
echo [4/5] Instalando no aparelho...

%ADBS% install -r "%APK%"
if errorlevel 1 (
    call :fatal "A instalacao falhou." "Se o erro foi INSTALL_FAILED_USER_RESTRICTED, ligue 'Instalar via USB' nas Opcoes do desenvolvedor do celular. Pode aparecer um pop-up no aparelho pedindo permissao."
    goto :fim
)

rem ---------------------------------------------------------------------------
rem  5) Habilitar. HABILITAR so, NUNCA definir como padrao: este IME nao desenha
rem     teclas, so recebe texto por broadcast. Como padrao, o teclado SOME DA TELA
rem     do celular e quem for usar o aparelho a mao fica sem digitar. O sistema
rem     seleciona ele apenas em volta da digitacao e restaura o anterior depois.
rem ---------------------------------------------------------------------------
echo.
echo [5/5] Habilitando o teclado...

%ADBS% shell ime enable %IME%
%ADBS% shell ime list -s > "%LISTA%" 2>nul
findstr /i "%PACOTE%" "%LISTA%" >nul
if errorlevel 1 (
    call :fatal "O teclado instalou mas nao aparece no 'ime list -s'." "Sem isso o sistema nao vai detecta-lo. Tente: adb shell ime enable %IME%"
    goto :fim
)

echo.
echo ============================================================
echo   PRONTO
echo ============================================================
echo   O aparelho !SERIAL! ja digita emoji e acento.
echo.
echo   O console detecta sozinho: nao precisa mudar nada nele.
echo   Deixe a digitacao humana LIGADA - agora ela aguenta o texto inteiro.
echo.
echo   AVISO: nao defina este teclado como padrao nas Configuracoes do
echo   celular. Ele nao desenha teclas, entao o teclado sumiria da tela.
echo   O sistema troca pra ele so na hora de digitar e devolve o seu depois.
echo.
goto :fim

rem ---------------------------------------------------------------------------
rem  Subrotinas
rem ---------------------------------------------------------------------------

:fatal
echo.
echo ============================================================
echo   PAROU AQUI: %~1
echo ============================================================
echo   %~2
echo.
set "FALHOU=1"
exit /b 0

:fim
if exist "%LISTA%" del "%LISTA%" >nul 2>nul
echo.
pause
if defined FALHOU exit /b 1
exit /b 0
