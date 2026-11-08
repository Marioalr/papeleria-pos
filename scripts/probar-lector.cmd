@echo off
rem Probar el lector (docs/pruebas-en-tienda.md, el paso 7 del piloto): qué manda con cada código y a qué ritmo, y si en
rem el cobro la caja lo reconoce como el lector. No toca eleventa ni ninguna base.
rem Con el lector por puerto COM (como lo tiene eleventa en la caja 2): probar-lector.cmd --puerto COM3 [--baudios 9600]
rem dice qué llega por el puerto y si está ocupado; probar-lector.cmd --puertos dice los puertos COM de la PC.
setlocal
chcp 65001 >nul

dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- probar-lector %*
