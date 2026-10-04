#Requires -RunAsAdministrator
<#
    Instala el servicio de Papeleria POS en la caja principal. Se corre como administrador desde la carpeta publicada
    del servicio (donde esta PapeleriaPos.Servicio.exe):

        powershell -ExecutionPolicy Bypass -File .\instalar-servicio.ps1
        powershell -ExecutionPolicy Bypass -File .\instalar-servicio.ps1 -Quitar

    - El servicio arranca con Windows, aunque nadie inicie sesion, y se reinicia solo si falla.
    - La base queda en C:\ProgramData\PapeleriaPos\pos.db y los respaldos en C:\ProgramData\PapeleriaPos\respaldos.
    - Abre el firewall solo para la red local: el puerto 5080 (las cajas) y el 5081 (buscar el servicio en la red).
#>
param([switch]$Quitar)

$ErrorActionPreference = 'Stop'
$nombre = 'PapeleriaPos'
$reglaCajas = 'Papeleria POS - cajas'
$reglaBuscar = 'Papeleria POS - buscar en la red'

if ($Quitar) {
    if (Get-Service $nombre -ErrorAction SilentlyContinue) {
        Stop-Service $nombre -ErrorAction SilentlyContinue
        sc.exe delete $nombre | Out-Null
    }
    netsh advfirewall firewall delete rule name="$reglaCajas" | Out-Null
    netsh advfirewall firewall delete rule name="$reglaBuscar" | Out-Null
    Write-Host 'Servicio quitado. La base y los respaldos se quedan en C:\ProgramData\PapeleriaPos.'
    return
}

$exe = Join-Path $PSScriptRoot 'PapeleriaPos.Servicio.exe'
if (-not (Test-Path $exe)) {
    throw "No se encontro $exe. Corre este script desde la carpeta publicada del servicio."
}
if (Get-Service $nombre -ErrorAction SilentlyContinue) {
    throw "El servicio $nombre ya esta instalado. Para reinstalarlo, primero corre este script con -Quitar."
}

New-Service -Name $nombre -BinaryPathName "`"$exe`"" -DisplayName 'Papeleria POS' -StartupType Automatic `
    -Description 'Servicio de la sucursal: base de datos y API para las cajas.' | Out-Null
# Si se cae: reinicia a los 5 s, otra vez a los 5 s y luego al minuto; el contador vuelve a cero cada dia.
sc.exe failure $nombre reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

netsh advfirewall firewall add rule name="$reglaCajas" dir=in action=allow protocol=TCP localport=5080 remoteip=localsubnet profile=any | Out-Null
netsh advfirewall firewall add rule name="$reglaBuscar" dir=in action=allow protocol=UDP localport=5081 remoteip=localsubnet profile=any | Out-Null

Start-Service $nombre
Write-Host "Servicio instalado y en marcha. Las cajas lo encuentran en http://$(hostname):5080 o con 'Buscar en la red'."
