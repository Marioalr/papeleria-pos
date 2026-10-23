#Requires -RunAsAdministrator
<#
    Instala la caja en esta PC para todos sus usuarios. Lo corre el administrador desde la carpeta publicada de la caja
    (donde está PapeleriaPos.Caja.exe); después la caja se usa desde una cuenta común de Windows (docs/plan.md, instalación y
    usuarios de Windows):

        powershell -ExecutionPolicy Bypass -File .\instalar-caja.ps1
        powershell -ExecutionPolicy Bypass -File .\instalar-caja.ps1 -ColaImpresion "WF-6590"
        powershell -ExecutionPolicy Bypass -File .\instalar-caja.ps1 -Quitar

    - Copia la caja a C:\Program Files\PapeleriaPos\Caja. Para ponerle una versión nueva: actualizar.ps1 del paquete, que la
      respalda y regresa sola si algo sale mal (docs/pruebas-en-tienda.md, «Actualizar la caja 2 del piloto»); correr este otra
      vez con la versión nueva también la pone, sin respaldo.
    - Deja C:\ProgramData\PapeleriaPos\Caja con escritura para los usuarios: ahí guarda la caja su alta y su copia para
      trabajar sin conexión.
    - Pone el acceso «Caja Papelería POS» en el escritorio de todos los usuarios.
    - Con -ColaImpresion, activa "Conservar documentos impresos" en esa cola (la de la WF-6590): pide administrador.
#>
param([string]$ColaImpresion, [switch]$Quitar)

$ErrorActionPreference = 'Stop'
$destino = Join-Path $env:ProgramFiles 'PapeleriaPos\Caja'
$datos = Join-Path $env:ProgramData 'PapeleriaPos\Caja'
$acceso = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Caja Papelería POS.lnk'

if ($Quitar) {
    Get-Process PapeleriaPos.Caja -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $destino -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $acceso -Force -ErrorAction SilentlyContinue
    Write-Host "Caja quitada. Su alta y su copia se quedan en $datos."
    return
}

$exe = Join-Path $PSScriptRoot 'PapeleriaPos.Caja.exe'
if (-not (Test-Path $exe)) {
    throw "No se encontró $exe. Corre este script desde la carpeta publicada de la caja."
}

Get-Process PapeleriaPos.Caja -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $destino | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot '*') -Destination $destino -Recurse -Force
# Sin la marca de «descargado de internet»: si no, Windows pregunta antes de abrir la caja.
Get-ChildItem -LiteralPath $destino -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue

# S-1-5-32-545 es el grupo Usuarios (se llama distinto según el idioma de Windows).
New-Item -ItemType Directory -Force -Path $datos | Out-Null
icacls $datos /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null

$shell = New-Object -ComObject WScript.Shell
$atajo = $shell.CreateShortcut($acceso)
$atajo.TargetPath = Join-Path $destino 'PapeleriaPos.Caja.exe'
$atajo.WorkingDirectory = $destino
$atajo.Save()

if ($ColaImpresion) {
    Set-Printer -Name $ColaImpresion -KeepPrintedJobs $true
    Write-Host "La cola «$ColaImpresion» conserva los documentos impresos."
}

Write-Host "Caja instalada. Ábrela desde el escritorio con la cuenta de Windows de todos los días (no hace falta administrador)."
