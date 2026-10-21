@echo off
rem Probar la impresora de tickets que comparten las dos cajas (docs/pruebas-en-tienda.md, el paso 7 del piloto): qué hace
rem cuando le llegan dos tickets a la vez. Manda tickets de prueba al puerto 9100, como los manda la caja. No toca
rem eleventa ni ninguna base: solo gasta un poco de papel.
rem
rem La IP de la impresora va como argumento o en la variable IMPRESORA_TICKETS; si no, se pregunta. Para probar desde
rem las dos cajas, se abre en las dos y se da Enter al mismo tiempo.
setlocal
chcp 65001 >nul

set "IMPRESORA=%~1"
if "%IMPRESORA%"=="" set "IMPRESORA=%IMPRESORA_TICKETS%"
if "%IMPRESORA%"=="" set /p "IMPRESORA=IP de la impresora de tickets: "
if "%IMPRESORA%"=="" exit /b 1

dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- probar-impresora-tickets --impresora "%IMPRESORA%"
