@echo off
rem Recopilar los datos de Control Print (docs/impresion.md, «Recopilar los datos de Control Print»), SOLO LEYENDO: no cobra,
rem no cambia ni borra nada en Control Print. Pide el código de conexión (Control Print, Ajustes, Red, en la caja principal)
rem sin que se vea. Lo recopilado queda en Documentos\TPV\control-print-datos\<fecha>: son datos reales, no van al repositorio.
rem
rem En la caja 2 (sin el SDK de .NET) va junto a PapeleriaPos.Herramientas.exe, el de un solo archivo; en una PC con el
rem repositorio y el SDK, corre las herramientas con dotnet run. Control Print se busca en CONTROLPRINT_SERVIDOR o, si no
rem está, en PRINCIPAL. Lo que se escriba después se pasa tal cual: --miniaturas, --desde AAAA-MM-DD, --servidor <IP>…
rem Ctrl+C lo detiene; al volver a correrlo el mismo día, sigue donde se quedó.
setlocal
chcp 65001 >nul

set "SERVIDOR=%CONTROLPRINT_SERVIDOR%"
if "%SERVIDOR%"=="" set "SERVIDOR=PRINCIPAL"

if not exist "%~dp0PapeleriaPos.Herramientas.exe" goto :conElSdk
"%~dp0PapeleriaPos.Herramientas.exe" recopilar-control-print --servidor "%SERVIDOR%" %*
goto :fin

:conElSdk
dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- recopilar-control-print --servidor "%SERVIDOR%" %*

:fin
echo.
pause
