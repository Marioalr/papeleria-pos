@echo off
rem Probar el lector (docs/pruebas-en-tienda.md, el paso 7 del piloto): qué manda con cada código y a qué ritmo, y si en
rem el cobro la caja lo reconoce como el lector. No toca eleventa ni ninguna base.
setlocal
chcp 65001 >nul

dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- probar-lector
