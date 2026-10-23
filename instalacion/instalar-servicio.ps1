#Requires -RunAsAdministrator
<#
    Instala el servicio de Papeleria POS en la caja principal (y, durante el piloto, en la caja 2). Se corre como administrador
    desde la carpeta servicio\ del paquete (donde esta PapeleriaPos.Servicio.exe; instalacion/armar-paquete.ps1):

        powershell -ExecutionPolicy Bypass -File .\instalar-servicio.ps1
        powershell -ExecutionPolicy Bypass -File .\instalar-servicio.ps1 -Quitar

    - Copia el servicio a C:\Program Files\PapeleriaPos\Servicio y lo registra desde ahí, como la caja: ahí las cuentas comunes de
      Windows no pueden cambiar el programa, que corre como sistema.
    - El servicio arranca con Windows, aunque nadie inicie sesion, y se reinicia solo si falla.
    - La base queda en C:\ProgramData\PapeleriaPos\pos.db y los respaldos en C:\ProgramData\PapeleriaPos\respaldos.
    - La configuración de esta PC va en C:\ProgramData\PapeleriaPos\servicio.json: si no hay, se pone la plantilla (sin ajustes);
      nunca encima de una que ya esté. Actualizar no la toca (docs/pruebas-en-tienda.md, «Actualizar la caja 2 del piloto»).
    - Deja C:\ProgramData\PapeleriaPos\Caja con escritura para los usuarios: la caja de esta PC se usa desde un usuario
      común (docs/plan.md, instalación y usuarios de Windows). Para las demás cajas, instalar-caja.ps1.
    - Abre el firewall solo para la red local: el puerto 5080 (las cajas) y el 5081 (buscar el servicio en la red).
    - Si ya está instalado, no hace nada: una versión nueva se pone con actualizar.ps1 del paquete, que respalda la base y
      regresa sola si algo sale mal.
    - Con -Quitar: detiene y quita el servicio, sus reglas del firewall y C:\Program Files\PapeleriaPos\Servicio. La base, los
      respaldos y servicio.json se quedan en C:\ProgramData\PapeleriaPos.
#>
param([switch]$Quitar)

$ErrorActionPreference = 'Stop'
$nombre = 'PapeleriaPos'
$reglaCajas = 'Papeleria POS - cajas'
$reglaBuscar = 'Papeleria POS - buscar en la red'
$destino = Join-Path $env:ProgramFiles 'PapeleriaPos\Servicio'
$datos = Join-Path $env:ProgramData 'PapeleriaPos'
$configuracion = Join-Path $datos 'servicio.json'

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    throw 'Esta es la ventana de PowerShell de 32 bits (x86): abre Windows PowerShell (sin x86) como administrador.'
}

if ($Quitar) {
    if (Get-Service $nombre -ErrorAction SilentlyContinue) {
        Stop-Service $nombre -ErrorAction SilentlyContinue
        sc.exe delete $nombre | Out-Null
    }
    netsh advfirewall firewall delete rule name="$reglaCajas" | Out-Null
    netsh advfirewall firewall delete rule name="$reglaBuscar" | Out-Null
    # Que esta ventana no tenga abierta la carpeta que se borra (si se corrio desde ahi).
    Set-Location -LiteralPath $env:SystemRoot
    [IO.Directory]::SetCurrentDirectory($env:SystemRoot)
    if (Test-Path -LiteralPath $destino) {
        # El proceso del servicio termina un poco despues de que Windows lo da por detenido.
        for ($intento = 1; ; $intento++) {
            try { Remove-Item -LiteralPath $destino -Recurse -Force; break }
            catch { if ($intento -ge 10) { throw }; Start-Sleep -Seconds 1 }
        }
    }
    Write-Host 'Servicio quitado. La base, los respaldos y servicio.json se quedan en C:\ProgramData\PapeleriaPos.'
    # Bien, aunque netsh no haya encontrado alguna regla que quitar.
    exit 0
}

$origen = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $origen 'PapeleriaPos.Servicio.exe'))) {
    throw "No se encontro PapeleriaPos.Servicio.exe en $origen. Corre este script desde la carpeta servicio del paquete."
}
if (Get-Service $nombre -ErrorAction SilentlyContinue) {
    throw "El servicio $nombre ya esta instalado. Para ponerle una version nueva, corre actualizar.ps1 del paquete (respalda la base y regresa sola si algo sale mal). Para reinstalarlo, primero corre este script con -Quitar."
}

# El programa, en Archivos de programa (salvo que ya se este corriendo desde ahi).
if ([IO.Path]::GetFullPath($origen).TrimEnd('\') -ne $destino) {
    if (Test-Path -LiteralPath $destino) { Remove-Item -LiteralPath $destino -Recurse -Force }
    [IO.Directory]::CreateDirectory($destino) | Out-Null
    Get-ChildItem -LiteralPath $origen -Force | Copy-Item -Destination $destino -Recurse -Force
}
# Sin la marca de «descargado de internet».
Get-ChildItem -LiteralPath $destino -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue
$exe = Join-Path $destino 'PapeleriaPos.Servicio.exe'

# La configuración de esta PC: la plantilla, si no hay una (nunca encima).
[IO.Directory]::CreateDirectory($datos) | Out-Null
$plantilla = Join-Path $destino 'plantilla-servicio.json'
if (Test-Path -LiteralPath $configuracion) {
    Write-Host "Configuracion de esta PC: se queda la que ya estaba ($configuracion)."
} elseif (Test-Path -LiteralPath $plantilla) {
    Copy-Item -LiteralPath $plantilla -Destination $configuracion
    Write-Host "Configuracion de esta PC: $configuracion (sin ajustes; ahi va, por ejemplo, el puente con eleventa)."
}

New-Service -Name $nombre -BinaryPathName "`"$exe`"" -DisplayName 'Papeleria POS' -StartupType Automatic `
    -Description 'Servicio de la sucursal: base de datos y API para las cajas.' | Out-Null
# Si se cae: reinicia a los 5 s, otra vez a los 5 s y luego al minuto; el contador vuelve a cero cada dia.
sc.exe failure $nombre reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

netsh advfirewall firewall add rule name="$reglaCajas" dir=in action=allow protocol=TCP localport=5080 remoteip=localsubnet profile=any | Out-Null
netsh advfirewall firewall add rule name="$reglaBuscar" dir=in action=allow protocol=UDP localport=5081 remoteip=localsubnet profile=any | Out-Null

# La carpeta de la caja: los usuarios comunes escriben su configuración y su copia para trabajar sin conexión.
# S-1-5-32-545 es el grupo Usuarios (se llama distinto según el idioma de Windows).
$carpetaCaja = Join-Path $env:ProgramData 'PapeleriaPos\Caja'
New-Item -ItemType Directory -Force -Path $carpetaCaja | Out-Null
icacls $carpetaCaja /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null

Start-Service $nombre
Write-Host "Servicio instalado en $destino y en marcha. Las cajas lo encuentran en http://$(hostname):5080 o con 'Buscar en la red'."
exit 0
