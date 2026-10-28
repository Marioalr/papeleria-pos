@echo off
rem Recopilar los datos de Control Print (docs/impresion.md, «Recopilar los datos de Control Print»), SOLO LEYENDO: no cobra,
rem no cambia ni borra nada en Control Print. Pide el código de conexión (Control Print, Ajustes, Red, en la caja principal)
rem sin que se vea: se escribe cuando salga «Código de conexión:», no antes. Lo recopilado queda en
rem Documentos\TPV\control-print-datos\<fecha>: son datos reales, no van al repositorio.
rem
rem En la caja 2 (sin el SDK de .NET) va junto a PapeleriaPos.Herramientas.exe, el de un solo archivo; en una PC con el
rem repositorio y el SDK, corre las herramientas con dotnet run. Control Print se busca en CONTROLPRINT_SERVIDOR o, si no
rem está, en PRINCIPAL, y tiene que presentar el certificado con la huella de CONTROLPRINT_HUELLA o, si no está, la de la caja
rem principal (docs/impresion.md): así lo que conteste antes de pedir el código ya es el Control Print de verdad. Lo que se
rem escriba después se pasa tal cual: --miniaturas, --desde AAAA-MM-DD, --servidor <IP>…
rem Ctrl+C lo detiene; al volver a correrlo el mismo día, sigue donde se quedó.
setlocal
chcp 65001 >nul

set "SERVIDOR=%CONTROLPRINT_SERVIDOR%"
if "%SERVIDOR%"=="" set "SERVIDOR=PRINCIPAL"
set "HUELLA=%CONTROLPRINT_HUELLA%"
if "%HUELLA%"=="" set "HUELLA=7B:D8:39:26:F8:86:B2:2E:B0:35:99:E8:C4:BA:08:85:51:85:E8:39:A9:64:AC:4B:00:C8:E6:34:56:D9:A5:62"

if not exist "%~dp0PapeleriaPos.Herramientas.exe" goto :conElSdk
"%~dp0PapeleriaPos.Herramientas.exe" recopilar-control-print --servidor "%SERVIDOR%" --huella "%HUELLA%" %*
goto :fin

:conElSdk
dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- recopilar-control-print --servidor "%SERVIDOR%" --huella "%HUELLA%" %*

:fin
echo.
pause
