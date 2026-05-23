@echo off
setlocal
pushd "%~dp0"
if not exist "bin\mtrx.exe" (
    echo Primeiro uso: publicando mtrx.exe...
    dotnet publish src\MtrxSys.Cli -c Release -r win-x64 --self-contained true ^
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
        -o bin --nologo
    if errorlevel 1 (
        popd
        exit /b %ERRORLEVEL%
    )
)
if not exist "tmp" mkdir tmp
"bin\mtrx.exe" %*
set EC=%ERRORLEVEL%
popd
exit /b %EC%
