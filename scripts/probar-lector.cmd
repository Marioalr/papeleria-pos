@echo off
rem Probar el lector (docs/pruebas-en-tienda.md, el paso 7 del piloto): qué manda con cada código y a qué ritmo, si en el
rem cobro la caja lo reconoce como el lector y si el QR del ticket de prueba (sale con probar-impresora-tickets) llega
rem igual. No toca eleventa ni ninguna base.
setlocal
chcp 65001 >nul

dotnet run --project "%~dp0..\src\PapeleriaPos.Herramientas" -- probar-lector
