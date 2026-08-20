@echo off
rem Atalho de clique duplo: abre o console do aparelho fisico.
rem Existe porque .ps1 nao roda por clique duplo no Windows por padrao (a associacao abre o editor).
rem Passa adiante o que voce der: tools\phone-console.cmd -Serial RQ8WB048RFW
rem
rem ABRE E SAI NA MESMA HORA, de proposito. Antes ele ficava esperando o console terminar, e por isso
rem todo Ctrl+C caia no "Deseja finalizar o arquivo em lotes (S/N)?" do cmd.exe: uma pergunta que nao
rem e nossa, que nao da pra reescrever, e que decidia sobre este .cmd e nao sobre o console. Sem lote
rem rodando, o Windows nao tem o que perguntar.
rem
rem A caixa de "o console nao abriu" e a espera de tecla foram para o phone-console.ps1 (funcao
rem Fechar), que agora e quem segura a janela aberta quando algo da errado.
start "mtrx phone console" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0phone-console.ps1" %*
