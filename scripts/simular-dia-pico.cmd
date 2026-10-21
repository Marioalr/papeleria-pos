@echo off
rem El día pico con las dos cajas (docs/pruebas-en-tienda.md, «El día pico en la caja principal real»): el simulador de cajas
rem vende 120 tickets en cada una de dos cajas simuladas, como la caja de verdad (con su copia, su contingencia y una caída de
rem la red de 30 s a la mitad del día), contra un servicio del POS que arranca él mismo en ESTA PC, con una COPIA de la base
rem de prueba, y que detiene al terminar. No toca eleventa, ni la base de la prueba con una persona, ni ningún dato de la
rem tienda. Tarda unos 13 minutos. El informe queda en Documentos\TPV\dia-pico.
rem
rem Se corre en la caja principal, con el repositorio al día y .NET (como observar-en-vivo en la caja 2), sin el POS de la
rem prueba abierto y sin nada más pesado abierto. Eleventa puede seguir abierto: no se usa.
setlocal
chcp 65001 >nul

set "RAIZ=%~dp0.."
rem La base de la prueba que dejó el instalador; si no está, el simulador arma una igual (la papelería ficticia).
set "BASE=%ProgramFiles%\PapeleriaPos-Prueba\datos\prueba.db"
set "CON_BASE="
if exist "%BASE%" set CON_BASE=--base "%BASE%"

echo Compilando el servicio y el simulador (la primera vez tarda)...
dotnet build "%RAIZ%\src\PapeleriaPos.Servicio" -c Release -v q -nologo || exit /b 1
dotnet build "%RAIZ%\src\PapeleriaPos.Herramientas" -c Release -v q -nologo || exit /b 1

set "INFORMES=%USERPROFILE%\Documents\TPV\dia-pico"
if not exist "%INFORMES%" mkdir "%INFORMES%"
for /f %%f in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HHmm"') do set "CUANDO=%%f"

dotnet "%RAIZ%\src\PapeleriaPos.Herramientas\bin\Release\net10.0\PapeleriaPos.Herramientas.dll" simular-cajas ^
  --iniciar-servicio "%RAIZ%\src\PapeleriaPos.Servicio\bin\Release\net10.0\PapeleriaPos.Servicio.dll" %CON_BASE% ^
  --cajas 2 --tickets 120 --minutos 10 --caida 30s@50%% --donde "%COMPUTERNAME% (caja principal)" ^
  --salida "%INFORMES%\dia-pico-%CUANDO%.md"
set "CODIGO=%ERRORLEVEL%"
echo.
if "%CODIGO%"=="0" (echo Cuadra. El informe quedó en "%INFORMES%".) else (echo NO cuadró o algo falló ^(código %CODIGO%^): revisa el informe en "%INFORMES%".)
pause
exit /b %CODIGO%
