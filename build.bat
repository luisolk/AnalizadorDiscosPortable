@echo off
setlocal
echo ====================================================
echo  Compilando Analizador de Discos Portable (C# 4.8)
echo ====================================================

set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist %CSC% (
    echo Error: No se encontro el compilador csc.exe en %CSC%
    pause
    exit /b 1
)

%CSC% /target:winexe /optimize+ /platform:anycpu /win32manifest:app.manifest /win32icon:app.ico /out:AnalizadorDiscosPortable.exe src\*.cs

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ====================================================
    echo  COMPILACION EXITOSA!
    echo  Ejecutable generado: AnalizadorDiscosPortable.exe
    echo ====================================================
) else (
    echo.
    echo [ERROR] La compilacion ha fallado.
)

endlocal
