@echo off
rem ============================================================================
rem  MtrxSys - instalacao automatica num PC novo.
rem
rem  Funciona de dois jeitos:
rem    1) Ja clonou o repo?  Rode este arquivo de dentro da pasta do projeto.
rem    2) Nao clonou nada?   Baixe SO este arquivo, ponha numa pasta vazia e
rem       rode. Ele clona o projeto do lado e continua sozinho.
rem
rem  O que ele faz: confere o git, clona (se precisar), confere o Docker e ate
rem  liga o Docker Desktop se estiver fechado, cria o .env com uma chave JWT
rem  aleatoria, avisa se alguma porta esta ocupada e chama o start.cmd.
rem
rem  Sem acentos de proposito: o console do Windows usa codepage 850 e acento
rem  em .cmd vira caractere quebrado na tela.
rem ============================================================================

setlocal EnableExtensions EnableDelayedExpansion
title MtrxSys - instalacao automatica

set "REPO_URL=https://github.com/FinnCL/MtrxSys.git"
set "FALHOU="

rem Pasta onde ESTE arquivo esta (sem a barra final).
set "AQUI=%~dp0"
if "%AQUI:~-1%"=="\" set "AQUI=%AQUI:~0,-1%"

echo.
echo ============================================================
echo   MtrxSys - instalacao automatica
echo ============================================================
echo.

rem ---------------------------------------------------------------------------
rem  1) Achar o repositorio. O MtrxSys.slnx e a prova de que estamos na raiz.
rem ---------------------------------------------------------------------------
set "RAIZ="
if exist "%AQUI%\MtrxSys.slnx" set "RAIZ=%AQUI%"
if not defined RAIZ if exist "%AQUI%\MtrxSys\MtrxSys.slnx" set "RAIZ=%AQUI%\MtrxSys"

if defined RAIZ (
    echo [1/5] Projeto encontrado em: !RAIZ!
    goto :temrepo
)

echo [1/5] Projeto nao encontrado aqui do lado. Vou clonar.

where git >nul 2>nul
if errorlevel 1 (
    call :fatal "Git nao esta instalado." "Baixe em https://git-scm.com/download/win , instale e rode este arquivo de novo."
    goto :fim
)

if exist "%AQUI%\MtrxSys" (
    call :fatal "Ja existe uma pasta MtrxSys aqui, mas sem o MtrxSys.slnx dentro." "Provavelmente e um clone que deu errado no meio. Apague a pasta e rode de novo."
    goto :fim
)

git clone "%REPO_URL%" "%AQUI%\MtrxSys"
if errorlevel 1 (
    call :fatal "O git clone falhou." "Se o repositorio for privado, autentique antes (gh auth login) e rode de novo."
    goto :fim
)
set "RAIZ=%AQUI%\MtrxSys"

:temrepo

rem ---------------------------------------------------------------------------
rem  2) Docker. E o unico pre-requisito que nao tem plano B: o sistema inteiro
rem     roda em container, nada e instalado na maquina.
rem ---------------------------------------------------------------------------
echo.
echo [2/5] Conferindo o Docker...

where docker >nul 2>nul
if errorlevel 1 (
    call :fatal "Docker Desktop nao esta instalado." "Baixe em https://www.docker.com/products/docker-desktop/ , instale, abra uma vez ate o icone ficar verde e rode este arquivo de novo."
    goto :fim
)

rem "docker info" fala com o servico. O "docker version" responde ate com o
rem servico morto (so mostra a versao do cliente), por isso nao serve aqui.
docker info >nul 2>nul
if not errorlevel 1 goto :dockerok

echo       Docker instalado, mas o servico nao respondeu. Tentando abrir o Docker Desktop...

set "DD=%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
if not exist "!DD!" set "DD=%ProgramW6432%\Docker\Docker\Docker Desktop.exe"
if exist "!DD!" (
    start "" "!DD!"
) else (
    echo       Nao achei o Docker Desktop.exe. Abra ele na mao agora.
)

echo       Aguardando o Docker ficar pronto (ate 3 minutos).
set /a TENTATIVA=0

:esperadocker
set /a TENTATIVA+=1
docker info >nul 2>nul
if not errorlevel 1 (
    echo.
    goto :dockerok
)
if !TENTATIVA! geq 90 (
    echo.
    call :fatal "O Docker nao ficou pronto em 3 minutos." "Abra o Docker Desktop, espere o icone ficar verde e rode este arquivo de novo."
    goto :fim
)
<nul set /p "=."
rem O timeout morre quando a entrada esta redirecionada (rodando por pipe, por
rem exemplo). O ping serve de plano B: -n 3 gasta uns 2 segundos.
timeout /t 2 /nobreak >nul 2>nul || ping -n 3 127.0.0.1 >nul 2>nul
goto :esperadocker

:dockerok
echo       Docker respondendo. OK.

rem ---------------------------------------------------------------------------
rem  3) .env. Sem ele o sistema sobe com os defaults do compose, mas o JWT
rem     default e um "change-me" publico. Geramos uma chave real.
rem ---------------------------------------------------------------------------
echo.
echo [3/5] Conferindo o arquivo .env...

if exist "%RAIZ%\.env" (
    echo       Ja existe. Nao vou mexer.
    goto :envpronto
)

if not exist "%RAIZ%\.env.example" (
    echo       AVISO: .env.example nao encontrado. Seguindo com os defaults do compose.
    goto :envpronto
)

copy /y "%RAIZ%\.env.example" "%RAIZ%\.env" >nul
if errorlevel 1 (
    call :fatal "Nao consegui criar o .env." "Confira se voce tem permissao de escrita na pasta do projeto."
    goto :fim
)

rem Dois GUIDs sem hifen = 64 caracteres hexadecimais. Passa folgado do minimo
rem de 32 exigido pelo ValidateOnStart da api e do dispatcher, e nao tem nenhum
rem caractere que atrapalhe o parser do .env nem o do cmd.
set "CHAVE="
for /f "delims=" %%K in ('powershell -NoProfile -Command "[guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N')"') do set "CHAVE=%%K"

if not defined CHAVE (
    echo       AVISO: nao consegui gerar a chave. O .env ficou com o valor de exemplo.
    goto :envpronto
)

rem Reescreve SEM BOM. O compose le o .env como texto puro e um BOM na frente
rem gruda na primeira variavel.
powershell -NoProfile -Command "$p='%RAIZ%\.env'; $t=(Get-Content -Raw -Encoding UTF8 $p) -replace '(?m)^JWT_SIGNING_KEY=.*','JWT_SIGNING_KEY=%CHAVE%'; [IO.File]::WriteAllText($p,$t,(New-Object Text.UTF8Encoding($false)))"
if errorlevel 1 (
    echo       AVISO: nao consegui trocar a chave no .env. Segue com o valor de exemplo.
) else (
    echo       .env criado, com JWT_SIGNING_KEY aleatoria de 64 caracteres.
)

:envpronto

rem ---------------------------------------------------------------------------
rem  4) Portas. Aviso, nao bloqueio: se o proprio MtrxSys ja estiver no ar,
rem     essas portas estao ocupadas por ele mesmo.
rem ---------------------------------------------------------------------------
echo.
echo [4/5] Conferindo portas...
set "OCUPADA="
call :porta 5173
call :porta 5080
call :porta 5432
call :porta 3000
if defined OCUPADA (
    echo       Se o MtrxSys ja estiver rodando, ignore. Senao, feche quem esta usando.
) else (
    echo       Todas livres. OK.
)

rem ---------------------------------------------------------------------------
rem  5) Subir. O start.cmd builda, espera a api ficar healthy e abre o navegador.
rem ---------------------------------------------------------------------------
echo.
echo [5/5] Subindo o sistema. A primeira vez leva de 2 a 5 minutos (build das imagens).
echo.
call "%RAIZ%\start.cmd"
if errorlevel 1 (
    call :fatal "O start.cmd falhou." "O motivo esta nas mensagens acima. Diagnostico util: docker compose logs api"
    goto :fim
)

echo.
echo ============================================================
echo   PRONTO
echo ============================================================
echo   Painel:  http://localhost:5173
rem Precisa de DOIS caretes: com EnableDelayedExpansion ligado, o "!" solto e
rem comido na expansao e um caret so nao basta. Testado, "^^!" e o que imprime.
echo   Login:   admin@local / admin123^^!
echo   Pasta:   %RAIZ%
echo.
echo   Do dia a dia em diante, use start.cmd (nao precisa mais deste arquivo).
echo   Para parar tudo: down-all.cmd

rem Sem parenteses nas mensagens daqui pra baixo: dentro de um bloco if ^( ^), um
rem ")" solto no meio de um echo fecha o bloco antes da hora e quebra o script.
where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo   Opcional: o .NET SDK 10 nao esta instalado. So faz falta para o CLI,
    echo   o mtrx.cmd. O painel nao precisa dele: o build acontece no container.
)

echo.
echo   O que NAO veio junto: contatos, historico e a sessao do WhatsApp.
echo   O banco nasce vazio e o chip precisa ser pareado de novo por QR.
echo.
goto :fim

rem ---------------------------------------------------------------------------
rem  Subrotinas
rem ---------------------------------------------------------------------------

:porta
netstat -ano | findstr /r /c:":%~1 .*LISTENING" >nul
if not errorlevel 1 (
    echo       AVISO: porta %~1 ja esta em uso.
    set "OCUPADA=1"
)
exit /b 0

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
echo.
pause
if defined FALHOU exit /b 1
exit /b 0
