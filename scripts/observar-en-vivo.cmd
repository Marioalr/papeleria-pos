@echo off
rem Observar a eleventa en vivo (el piloto, docs/migracion-eleventa.md, «Verlo en vivo»).
rem Mientras pasan de verdad una cancelación, una devolución o el cierre de un turno, toma fotos de la base de la caja
rem principal, SOLO LEYENDO, y dice si eleventa hizo lo mismo que escribe la caja 2. No hace operaciones de prueba: las
rem de la tienda pasan cuando pasan. Las entradas y salidas de dinero no se revisan aquí.
rem
rem Se corre en la caja 2 (la que tiene eleventa configurado en red). La clave de Firebird se pide al empezar, sin que
rem se vea (o va en la variable ELEVENTA_CLAVE). A dónde se conecta se puede cambiar con ELEVENTA_SERVIDOR,
rem ELEVENTA_PUERTO y ELEVENTA_RUTA (por ejemplo, para probarlo contra una copia en un Firebird de prueba).
setlocal
chcp 65001 >nul

rem La caja principal: la que tiene configurada el eleventa de esta PC (pdventa.ini, [EnRed] IPServidor).
set "INI=%ProgramFiles(x86)%\AbarrotesMultiCaja\pdventa.ini"
set "SERVIDOR=%ELEVENTA_SERVIDOR%"
if not "%SERVIDOR%"=="" goto :ruta
for /f "tokens=1,* delims==" %%a in ('findstr /b /i "IPServidor=" "%INI%" 2^>nul') do set "SERVIDOR=%%b"
if not "%SERVIDOR%"=="" goto :ruta
echo No encontré la caja principal en "%INI%". Ponla en la variable ELEVENTA_SERVIDOR.
exit /b 1

:ruta
rem La base en esa PC: la de una instalación normal de eleventa, o la que diga ELEVENTA_RUTA.
set "RUTA=%ELEVENTA_RUTA%"
if "%RUTA%"=="" set "RUTA=C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB"
set "PUERTO=%ELEVENTA_PUERTO%"
if "%PUERTO%"=="" set "PUERTO=3050"

rem Las observaciones son datos reales: van con los demás datos de la tienda, fuera del repositorio.
set "CARPETA=%USERPROFILE%\Documents\TPV\observaciones-eleventa"

echo.
echo Observando el eleventa de la caja principal, solo leyendo.
echo.
echo Lo que falta ver en vivo (en cualquiera de las cajas):
echo   - una cancelación de un ticket;
echo   - una devolución de parte de un ticket (mejor si se devuelve un artículo completo);
echo   - el cierre de un turno con su corte (mejor si faltó o sobró algo al contar).
echo Cuando pase una de verdad, vuelve aquí y escribe qué fue. No hagas operaciones de prueba en la tienda.
echo.
dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- observar-eleventa --servidor "%SERVIDOR%" --ruta "%RUTA%" --puerto %PUERTO% --usuario SYSDBA --carpeta "%CARPETA%"
