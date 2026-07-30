@echo off
rem Atalho de clique duplo: abre o console do aparelho fisico.
rem Existe porque .ps1 nao roda por clique duplo no Windows por padrao (a associacao abre o editor).
rem Passa adiante o que voce der: tools\phone-console.cmd -Serial RQ8WB048RFW
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0phone-console.ps1" %*
set CODIGO=%ERRORLEVEL%

rem Saida 0 = voce escolheu sair. Fecha a janela sem pedir tecla, porque "pressione qualquer tecla"
rem depois de um encerramento normal so faz parecer que deu errado.
if "%CODIGO%"=="0" exit /b 0

echo.
echo ============================================================
echo  O CONSOLE NAO ABRIU (codigo %CODIGO%).
echo  O motivo esta na mensagem ACIMA desta caixa.
echo ============================================================
echo.
pause
exit /b %CODIGO%
