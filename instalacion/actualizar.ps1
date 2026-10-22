#Requires -RunAsAdministrator
<#
    Actualiza el servicio y la caja de esta PC con este paquete, sin perder la base ni la configuración de la PC
    (docs/pruebas-en-tienda.md, «Actualizar la caja 2 del piloto»). Se corre como administrador desde la carpeta del paquete
    nuevo, la que tiene servicio\ y caja\:

        powershell -ExecutionPolicy Bypass -File .\actualizar.ps1
        powershell -ExecutionPolicy Bypass -File .\actualizar.ps1 -Forzar

    Lo que hace, en orden:
    1. Dice qué versión hay y cuál trae el paquete (la de cada .exe). No pone una anterior ni la misma sin -Forzar.
    2. Revisa antes de tocar nada: que el servicio conteste, que la base no tenga el aviso de dañada, que el appsettings.json del
       programa no traiga ajustes propios de esta PC (esos van en C:\ProgramData\PapeleriaPos\servicio.json) y que haya espacio.
    3. Pide cerrar la caja (la venta que se está atendiendo se perdería) y espera a que se cierre; a la fuerza solo si se
       confirma o con -Forzar.
    4. Detiene el servicio con calma (90 s; si no, a la fuerza) y lo deja deshabilitado mientras cambia: así Windows no lo vuelve
       a arrancar a la mitad.
    5. Respalda en C:\ProgramData\PapeleriaPos\actualizaciones\<fecha>-<versión de antes>: la base (pos.db con su -wal, su -shm y
       sus marcas), la carpeta del servicio y la de la caja. Se guardan las últimas 3.
    6. Cambia las carpetas del programa por las del paquete, en C:\Program Files\PapeleriaPos. Un servicio instalado en otra
       carpeta (como se hacía antes) queda ahí, y su carpeta de antes ya no se usa. servicio.json no se toca (si no hay, se pone
       la plantilla, sin ajustes).
    7. Arranca el servicio y espera a que http://localhost:5080/api/estado conteste con la versión nueva.
    8. Si no contesta, se detiene solo o la revisión de la base al arrancar dice que está dañada, regresa solo a la versión de
       antes: las carpetas y también la base del respaldo (las migraciones pudieron cambiarla). Dice dónde quedó el registro.

    Termina con 0 si quedó (o no había nada que hacer), con 1 si no se actualizó (una revisión, se canceló o regresó a la de
    antes) y con 2 si tampoco pudo regresar solo: entonces lo de antes está completo en el respaldo.

    -Forzar           Pone una versión anterior o la misma, cierra la caja sin preguntar y sigue aunque no pase una revisión del
                      paso 2 (cada una dice qué pasa con -Forzar).
    -Puerto           El del servicio, si se cambió en servicio.json (Urls). Por omisión, 5080.
    -MinutosDeEspera  Lo más que se espera a que conteste la versión nueva (las migraciones de una base grande). Por omisión, 10.

    No lee ni escribe la clave de eleventa: va solo en la variable de entorno del servicio, y actualizar no cambia el servicio
    (solo la ruta de su programa, sin borrarlo).

    Los mensajes van sin acentos: así se leen igual en cualquier consola y en el registro de GitHub Actions.
#>
param(
    [switch]$Forzar,
    [int]$Puerto = 5080,
    [int]$MinutosDeEspera = 10
)

$ErrorActionPreference = 'Stop'
# Sin la barra de avance de Windows PowerShell en cada consulta a api/estado (la hace lenta).
$ProgressPreference = 'SilentlyContinue'

$nombre = 'PapeleriaPos'
$paquete = $PSScriptRoot
$datos = Join-Path $env:ProgramData 'PapeleriaPos'
$programa = Join-Path $env:ProgramFiles 'PapeleriaPos'
$carpetaServicio = Join-Path $programa 'Servicio'
$carpetaCaja = Join-Path $programa 'Caja'
$base = Join-Path $datos 'pos.db'
$configuracion = Join-Path $datos 'servicio.json'
$actualizaciones = Join-Path $datos 'actualizaciones'
$respaldosDelServicio = Join-Path $datos 'respaldos'
$estadoUrl = "http://localhost:$Puerto/api/estado"
# La base con su -wal y su -shm, y las marcas de la base contra apagones: se respaldan y se regresan juntas, tal como estaban.
$archivosDeLaBase = @('pos.db', 'pos.db-wal', 'pos.db-shm', 'pos.db.en-uso', 'pos.db.danada')
$segundosParaDetener = 90
$conservar = 3
$utf8 = New-Object System.Text.UTF8Encoding $false

# ── El registro: en la consola y, en cuanto existe la carpeta de esta actualización, en su actualizar.log ──

$script:bitacora = $null
$script:pendiente = New-Object System.Collections.Generic.List[string]

function Escribir([string]$texto = '', [string]$color = '') {
    if ($color) { Write-Host $texto -ForegroundColor $color } else { Write-Host $texto }
    $linea = '{0:HH:mm:ss} {1}' -f (Get-Date), $texto
    if ($script:bitacora) { [IO.File]::AppendAllText($script:bitacora, $linea + "`r`n", $utf8) } else { $script:pendiente.Add($linea) }
}

# Sale sin haber cambiado nada (una revisión, o se canceló).
function Terminar([string]$porque, [int]$codigo = 1) {
    Escribir $porque 'Red'
    Escribir 'No se cambio nada.'
    exit $codigo
}

# Sale a propósito ya empezado el cambio: el servicio queda como lo dejó cada camino (ver el final del script).
$script:terminado = $false
function Salir([int]$codigo) {
    $script:terminado = $true
    exit $codigo
}

function Mensaje([System.Management.Automation.ErrorRecord]$registro) {
    $ex = $registro.Exception
    while ($ex.InnerException) { $ex = $ex.InnerException }
    $ex.Message
}

# ── Versiones, carpetas y el servicio ──

function Leer-Version([string]$exe) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $null }
    $info = (Get-Item -LiteralPath $exe).VersionInfo
    # El .exe de .NET lleva la versión de su .dll; si no la trajera, se lee de ella.
    $dll = [IO.Path]::ChangeExtension($exe, '.dll')
    if (-not $info.ProductVersion -and (Test-Path -LiteralPath $dll -PathType Leaf)) { $info = (Get-Item -LiteralPath $dll).VersionInfo }
    $numero = New-Object System.Version -ArgumentList $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    # «1.0.57+abc123…»: la parte de antes del + es la que dice api/estado.
    $texto = ''
    if ($info.ProductVersion) { $texto = ($info.ProductVersion -split '\+')[0].Trim() }
    if (-not $texto) { $texto = '{0}.{1}.{2}' -f $numero.Major, $numero.Minor, $numero.Build }
    [pscustomobject]@{ Numero = $numero; Texto = $texto }
}

function Texto($version) { if ($version) { $version.Texto } else { 'desconocida' } }

function Misma-Carpeta([string]$a, [string]$b) {
    [IO.Path]::GetFullPath($a).TrimEnd('\') -eq [IO.Path]::GetFullPath($b).TrimEnd('\')
}

function Servicio-Instalado {
    $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nombre'"
    if (-not $cim) { return $null }
    $ruta = [string]$cim.PathName
    if ($ruta -match '^\s*"([^"]+)"') { $exe = $Matches[1] } elseif ($ruta -match '^\s*(.+?\.exe)') { $exe = $Matches[1] } else { $exe = $ruta.Trim() }
    [pscustomobject]@{ Ruta = $ruta; Exe = $exe; Carpeta = (Split-Path -Parent $exe); Modo = [string]$cim.StartMode; Estado = [string]$cim.State }
}

function Tamano([string]$ruta) {
    if (-not (Test-Path -LiteralPath $ruta)) { return [long]0 }
    $item = Get-Item -LiteralPath $ruta -Force
    if (-not $item.PSIsContainer) { return [long]$item.Length }
    $suma = (Get-ChildItem -LiteralPath $ruta -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
    if ($suma) { [long]$suma } else { [long]0 }
}

function Borrar-Carpeta([string]$ruta) {
    for ($intento = 1; ; $intento++) {
        if (-not (Test-Path -LiteralPath $ruta)) { return }
        try { Remove-Item -LiteralPath $ruta -Recurse -Force; return }
        catch { if ($intento -ge 5) { throw }; Start-Sleep -Seconds 1 }
    }
}

function Copiar-Carpeta([string]$origen, [string]$destino) {
    [IO.Directory]::CreateDirectory($destino) | Out-Null
    Get-ChildItem -LiteralPath $origen -Force | Copy-Item -Destination $destino -Recurse -Force
}

# Cambiar el nombre de una carpeta. Unos intentos: el antivirus puede tener abierto un momento un archivo recién copiado.
function Mover-Carpeta([string]$desde, [string]$hacia) {
    for ($intento = 1; ; $intento++) {
        try { Move-Item -LiteralPath $desde -Destination $hacia; return }
        catch { if ($intento -ge 5) { throw }; Start-Sleep -Seconds 1 }
    }
}

# Primero se copia aparte (.nuevo); después se cambian de nombre, que es un momento. Si algo tiene abierta la carpeta de antes,
# no se deja cambiar de nombre y no queda a medias.
function Reemplazar-Carpeta([string]$destino, [string]$origen) {
    $nueva = "$destino.nuevo"
    $anterior = "$destino.anterior"
    Borrar-Carpeta $nueva
    Borrar-Carpeta $anterior
    Copiar-Carpeta $origen $nueva
    if (Test-Path -LiteralPath $destino) { Mover-Carpeta $destino $anterior }
    Mover-Carpeta $nueva $destino
    try { Borrar-Carpeta $anterior } catch { Escribir "  (No se pudo borrar $anterior; se borra la siguiente vez.)" }
}

# Si la ventana se cerró a media actualización: la carpeta de antes se recupera y lo que sobró se borra.
function Limpiar-Restos {
    foreach ($carpeta in @($carpetaServicio, $carpetaCaja)) {
        $anterior = "$carpeta.anterior"
        if (-not (Test-Path -LiteralPath $carpeta) -and (Test-Path -LiteralPath $anterior)) {
            Move-Item -LiteralPath $anterior -Destination $carpeta
            Escribir "Se recupero $carpeta de una actualizacion que no termino."
        }
        Borrar-Carpeta "$carpeta.nuevo"
        Borrar-Carpeta $anterior
    }
}

function Leer-Estado {
    $parametros = @{ Uri = $estadoUrl; TimeoutSec = 5; UseBasicParsing = $true }
    if ($PSVersionTable.PSVersion.Major -ge 6) { $parametros.NoProxy = $true }
    try { Invoke-RestMethod @parametros } catch { $null }
}

function Detener-Servicio {
    $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
    if (-not $servicioAhora) { return }
    $idProceso = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$nombre'").ProcessId
    if ($servicioAhora.Status -ne 'Stopped') {
        try { if ($servicioAhora.Status -ne 'StopPending') { $servicioAhora.Stop() } } catch { Escribir "  (No acepto detenerse: $(Mensaje $_))" }
        try {
            $servicioAhora.WaitForStatus('Stopped', (New-TimeSpan -Seconds $segundosParaDetener))
        } catch {
            Escribir "  No se detuvo en $segundosParaDetener s: se cierra a la fuerza (al arrancar se revisa la base)." 'Yellow'
            if ($idProceso -gt 0) { Stop-Process -Id $idProceso -Force -ErrorAction SilentlyContinue }
            $servicioAhora.WaitForStatus('Stopped', (New-TimeSpan -Seconds 30))
        }
    }
    # Windows lo da por detenido un poco antes de que termine su proceso, que todavía tiene abierta la base.
    if ($idProceso -gt 0 -and (Get-Process -Id $idProceso -ErrorAction SilentlyContinue)) {
        Wait-Process -Id $idProceso -Timeout 30 -ErrorAction SilentlyContinue
        Stop-Process -Id $idProceso -Force -ErrorAction SilentlyContinue
    }
}

function Arrancar-Servicio {
    try { (Get-Service -Name $nombre).Start(); $null } catch { Mensaje $_ }
}

# Hasta que api/estado conteste con esa versión, o el servicio se detenga solo, o se acabe el tiempo.
function Esperar-Servicio([string]$version, [int]$segundos) {
    $limite = (Get-Date).AddSeconds($segundos)
    $ultimo = 'no contesto'
    while ($true) {
        $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
        if (-not $servicioAhora -or $servicioAhora.Status -eq 'Stopped') {
            return [pscustomobject]@{ Bien = $false; Estado = $null; Porque = 'el servicio se detuvo solo' }
        }
        $estado = Leer-Estado
        if ($estado) {
            if ([string]$estado.version -eq $version) { return [pscustomobject]@{ Bien = $true; Estado = $estado; Porque = $null } }
            $ultimo = "contesta la version $($estado.version)"
        }
        if ((Get-Date) -gt $limite) { return [pscustomobject]@{ Bien = $false; Estado = $null; Porque = "en $segundos s $ultimo" } }
        Start-Sleep -Seconds 2
    }
}

function Cambiar-Ruta([string]$ruta) {
    $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nombre'"
    $resultado = Invoke-CimMethod -InputObject $cim -MethodName Change -Arguments @{ PathName = $ruta }
    if ($resultado.ReturnValue -ne 0) { throw "Windows no dejo cambiar la ruta del servicio (codigo $($resultado.ReturnValue))." }
}

function Modo-De-Arranque([string]$modo) { if ($modo -eq 'Manual') { 'Manual' } else { 'Automatic' } }

# Lo que dice Windows del servicio desde que se arrancó (el Visor de eventos): por qué no quedó.
function Mostrar-Eventos([datetime]$desde) {
    $vistos = @()
    try {
        $vistos += @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $desde } -MaxEvents 300 -ErrorAction Stop | Where-Object {
                $_.ProviderName -like 'PapeleriaPos*' -or (($_.ProviderName -eq '.NET Runtime' -or $_.ProviderName -eq 'Application Error') -and $_.Message -match 'PapeleriaPos') })
    } catch { }
    try {
        $vistos += @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $desde } -MaxEvents 100 -ErrorAction Stop |
            Where-Object { $_.Message -match 'Papeleria ?POS' })
    } catch { }
    if ($vistos.Count -eq 0) { Escribir '  (El Visor de eventos no tiene nada del servicio desde que se arranco.)'; return }
    Escribir 'Lo que dice Windows del servicio (Visor de eventos):'
    foreach ($evento in ($vistos | Sort-Object TimeCreated | Select-Object -Last 12)) {
        $lineas = @(([string]$evento.Message -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 8)
        # Por si algún mensaje trajera una cadena de conexión: nunca una clave en la consola ni en el registro.
        $texto = ($lineas -join ' | ') -replace '(?i)(password|pwd|clave)\s*=\s*[^;|\s]+', '$1=***'
        Escribir ('  [{0:HH:mm:ss}] {1}: {2}' -f $evento.TimeCreated, $evento.ProviderName, $texto)
    }
}

# ── La caja abierta ──

function Cajas-Abiertas {
    # Solo la de esta instalación: la de la prueba con una persona real (PapeleriaPos-Prueba) usa otra carpeta y otra base.
    @(Get-CimInstance -ClassName Win32_Process -Filter "Name='PapeleriaPos.Caja.exe'" | Where-Object {
            -not $_.ExecutablePath -or $_.ExecutablePath.StartsWith($carpetaCaja + '\', [StringComparison]::OrdinalIgnoreCase) })
}

function Cerrar-A-La-Fuerza {
    foreach ($proceso in (Cajas-Abiertas)) { Stop-Process -Id $proceso.ProcessId -Force -ErrorAction SilentlyContinue }
    $limite = (Get-Date).AddSeconds(15)
    while ((Cajas-Abiertas).Count -gt 0 -and (Get-Date) -lt $limite) { Start-Sleep -Milliseconds 500 }
}

function Puede-Preguntar {
    if ($Host.Name -ne 'ConsoleHost') { return $false }
    try { [void][Console]::KeyAvailable; $true } catch { $false }
}

# La caja guarda los tickets en espera en su base local cada vez que cambia el ticket que se atiende, y el que se atiende al
# salir: cerrarla bien no pierde nada. A la fuerza, el que se atiende regresa como estaba en su último cambio de ticket, sin lo
# último que se escaneó.
function Cerrar-Caja {
    if ((Cajas-Abiertas).Count -eq 0) { return }
    if ($Forzar) {
        Escribir 'La caja esta abierta: se cierra (-Forzar). La venta que se este atendiendo puede regresar sin lo ultimo que se escaneo.' 'Yellow'
        Cerrar-A-La-Fuerza
        return
    }
    $preguntar = Puede-Preguntar
    Escribir 'La caja esta abierta en esta PC. Cobra o deja en espera la venta que se este atendiendo, sal con tu usuario y cierra la' 'Yellow'
    Escribir 'caja: los tickets en espera se guardan y regresan al abrirla. En cuanto se cierre, esto sigue solo.' 'Yellow'
    if ($preguntar) { Escribir '(C: cerrarla a la fuerza. Esc: no actualizar.)' }
    $limite = (Get-Date).AddMinutes(30)
    while ((Cajas-Abiertas).Count -gt 0) {
        if ((Get-Date) -gt $limite) { Terminar 'La caja siguio abierta 30 minutos. Cierrala y vuelve a correr actualizar.ps1.' }
        if ($preguntar -and [Console]::KeyAvailable) {
            $tecla = [Console]::ReadKey($true)
            if ($tecla.Key -eq 'Escape') { Terminar 'Se cancelo: no se actualizo.' }
            if ($tecla.Key -eq 'C') {
                $respuesta = Read-Host 'Cerrarla a la fuerza puede perder lo ultimo que se escaneo en la venta que se esta atendiendo (los tickets en espera se guardan). Escribe SI para cerrarla'
                if ($respuesta -match '^\s*s[ií]\s*$') { Cerrar-A-La-Fuerza; break }
                Escribir 'Se sigue esperando a que se cierre.'
            }
        }
        Start-Sleep -Milliseconds 500
    }
    if ((Cajas-Abiertas).Count -gt 0) { Terminar 'La caja no se pudo cerrar.' }
    Escribir 'La caja ya esta cerrada.'
}

# ── Lo de esta PC que se perdería al cambiar el programa ──

function Ajustes([object]$valor, [string]$prefijo, [hashtable]$salida) {
    if ($null -eq $valor) { $salida[$prefijo] = ''; return }
    if ($valor -is [System.Management.Automation.PSCustomObject]) {
        foreach ($propiedad in $valor.PSObject.Properties) {
            $clave = $propiedad.Name
            if ($prefijo) { $clave = "${prefijo}:$($propiedad.Name)" }
            Ajustes $propiedad.Value $clave $salida
        }
        return
    }
    if ($valor -is [System.Array]) {
        for ($i = 0; $i -lt $valor.Count; $i++) { Ajustes $valor[$i] "${prefijo}:$i" $salida }
        return
    }
    $salida[$prefijo] = [string]$valor
}

function Leer-Ajustes([string]$archivo) {
    try { $json = Get-Content -LiteralPath $archivo -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
    $salida = @{}
    Ajustes $json '' $salida
    $salida
}

# Los ajustes de appsettings*.json del programa instalado que el paquete no trae igual (solo los nombres: nunca un valor, que
# podría ser la clave de eleventa).
function Ajustes-Propios([string]$instalada, [string]$nueva) {
    $propios = @()
    foreach ($archivo in @(Get-ChildItem -LiteralPath $instalada -Filter 'appsettings*.json' -File -ErrorAction SilentlyContinue)) {
        $otro = Join-Path $nueva $archivo.Name
        if (-not (Test-Path -LiteralPath $otro)) { $propios += "$($archivo.Name) (el paquete no lo trae)"; continue }
        $antes = Leer-Ajustes $archivo.FullName
        $despues = Leer-Ajustes $otro
        if ($null -eq $antes -or $null -eq $despues) {
            if ((Get-FileHash -LiteralPath $archivo.FullName).Hash -ne (Get-FileHash -LiteralPath $otro).Hash) { $propios += "$($archivo.Name) (es distinto)" }
            continue
        }
        foreach ($clave in $antes.Keys) {
            if (-not $despues.ContainsKey($clave) -or $despues[$clave] -cne $antes[$clave]) { $propios += "$clave ($($archivo.Name))" }
        }
    }
    $propios
}

# ── Respaldo y vuelta atrás ──

function Respaldar-Base([string]$destino) {
    [IO.Directory]::CreateDirectory($destino) | Out-Null
    foreach ($archivo in $archivosDeLaBase) {
        $origen = Join-Path $datos $archivo
        if (Test-Path -LiteralPath $origen) {
            Copy-Item -LiteralPath $origen -Destination $destino -Force
            if ((Get-Item -LiteralPath $origen).Length -ne (Get-Item -LiteralPath (Join-Path $destino $archivo)).Length) { throw "La copia de $archivo no salio completa." }
        }
    }
}

# Tal como estaba: lo que no estaba en el respaldo (un -wal de la versión nueva, una marca) se quita antes.
function Restaurar-Base([string]$desde) {
    foreach ($archivo in $archivosDeLaBase) {
        $actual = Join-Path $datos $archivo
        if ($archivo -ne 'pos.db' -and (Test-Path -LiteralPath $actual)) { Remove-Item -LiteralPath $actual -Force }
    }
    foreach ($archivo in $archivosDeLaBase) {
        $guardado = Join-Path $desde $archivo
        if (Test-Path -LiteralPath $guardado) { Copy-Item -LiteralPath $guardado -Destination (Join-Path $datos $archivo) -Force }
    }
}

# Los respaldos diarios que alcanzó a hacer la versión nueva son de su base, ya migrada: se apartan para que el de hoy lo vuelva a
# hacer la de antes.
function Apartar-Respaldos-Nuevos([datetime]$desde) {
    if (-not (Test-Path -LiteralPath $respaldosDelServicio)) { return }
    $nuevos = @(Get-ChildItem -LiteralPath $respaldosDelServicio -File -Filter 'pos-*' | Where-Object { $_.LastWriteTime -ge $desde })
    if ($nuevos.Count -eq 0) { return }
    $aparte = Join-Path $script:respaldo 'respaldos-de-la-version-nueva'
    [IO.Directory]::CreateDirectory($aparte) | Out-Null
    foreach ($archivo in $nuevos) { Move-Item -LiteralPath $archivo.FullName -Destination $aparte -Force }
    Escribir "  Los respaldos diarios que hizo la version nueva se apartaron en $aparte."
}

function Limpiar-Actualizaciones {
    if (-not (Test-Path -LiteralPath $actualizaciones)) { return }
    $todas = @(Get-ChildItem -LiteralPath $actualizaciones -Directory | Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_' } | Sort-Object Name -Descending)
    foreach ($vieja in @($todas | Select-Object -Skip $conservar)) {
        try { Borrar-Carpeta $vieja.FullName; Escribir "  Se borro el respaldo de una actualizacion vieja: $($vieja.Name)." }
        catch { Escribir "  No se pudo borrar $($vieja.FullName): $(Mensaje $_)" }
    }
}

function Poner-Configuracion {
    if (Test-Path -LiteralPath $configuracion) { return }
    $plantilla = Join-Path $paquete 'servicio\plantilla-servicio.json'
    if (-not (Test-Path -LiteralPath $plantilla)) { return }
    [IO.Directory]::CreateDirectory($datos) | Out-Null
    Copy-Item -LiteralPath $plantilla -Destination $configuracion
    Escribir "  No habia configuracion de esta PC: se puso la plantilla, sin ajustes ($configuracion)."
}

# Regresa a lo de antes con el respaldo, solo lo que se alcanzó a cambiar: el programa del servicio (y su ruta), la caja y, si
# la versión nueva llegó a arrancar, la base. Termina el script.
function Regresar([string]$porque) {
    Escribir "La version nueva no quedo: $porque." 'Yellow'
    if ($script:arranque) { Mostrar-Eventos $script:arranque }
    Escribir "Se regresa a la version $($script:anterior) con el respaldo..." 'Yellow'
    try {
        if ($servicio) {
            Set-Service -Name $nombre -StartupType Disabled
            Detener-Servicio
            if ($script:cambiado.Ruta) {
                # Como se hacía antes: su carpeta no se tocó; vuelve a apuntar ahí.
                Cambiar-Ruta $servicio.Ruta
            }
            if ($script:cambiado.Servicio) {
                $guardado = Join-Path $script:respaldo 'servicio'
                if (-not (Misma-Carpeta $servicio.Carpeta $carpetaServicio)) { Borrar-Carpeta $carpetaServicio }
                elseif (Test-Path -LiteralPath $guardado) { Reemplazar-Carpeta $carpetaServicio $guardado }
                else { Escribir '  (No habia programa del servicio antes: se queda el nuevo.)' }
            }
            if ($script:arranque) {
                # Las migraciones de la versión nueva pudieron cambiar la base.
                Apartar-Respaldos-Nuevos $script:inicioDelCambio
                Restaurar-Base (Join-Path $script:respaldo 'base')
            }
        }
        if ($script:cambiado.Caja) { Reemplazar-Carpeta $carpetaCaja (Join-Path $script:respaldo 'caja') }
        if ($servicio) {
            Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo)
            $fallo = Arrancar-Servicio
            if (-not $fallo -and $versionServicio) {
                $resultado = Esperar-Servicio $versionServicio.Texto 300
                if (-not $resultado.Bien) { $fallo = $resultado.Porque }
            }
            if ($fallo) { throw "la version de antes tampoco contesto: $fallo" }
        }
    } catch {
        Escribir "No se pudo regresar solo: $(Mensaje $_)" 'Red'
        if ($servicio) {
            # Que nada toque la base hasta regresarla a mano.
            try { Set-Service -Name $nombre -StartupType Disabled; Detener-Servicio } catch { }
            Escribir 'El servicio quedo detenido y deshabilitado, para que nada toque la base hasta regresarla a mano.' 'Red'
        }
        Escribir "Lo de antes esta completo en $($script:respaldo): base\, servicio\ y caja\. Para regresarlo a mano: docs/pruebas-en-tienda.md, 'Actualizar la caja 2 del piloto'." 'Red'
        Escribir "El registro de lo que paso: $($script:bitacora)"
        Salir 2
    }
    Escribir "Se regreso a la version $($script:anterior): el servicio contesta, y la base y la caja son las de antes de actualizar." 'Yellow'
    Escribir "Por que no quedo la nueva: $($script:bitacora) (y en el Visor de eventos de Windows: Registros de Windows, Aplicacion)."
    Escribir 'Ya se puede abrir la caja.'
    Limpiar-Actualizaciones
    Salir 1
}

# ══ 1. Qué hay y qué trae el paquete ══

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    Terminar 'Esta es la ventana de PowerShell de 32 bits (x86): abre Windows PowerShell (sin x86) como administrador.'
}
if ($PSVersionTable.PSVersion.Major -lt 6) { [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy }
# Que esta ventana no tenga abierta ninguna carpeta del programa (no se dejaría cambiar).
Set-Location -LiteralPath $paquete
[IO.Directory]::SetCurrentDirectory($paquete)

$nuevoServicio = Join-Path $paquete 'servicio\PapeleriaPos.Servicio.exe'
$nuevaCaja = Join-Path $paquete 'caja\PapeleriaPos.Caja.exe'
if (-not (Test-Path -LiteralPath $nuevoServicio) -or -not (Test-Path -LiteralPath $nuevaCaja)) {
    Terminar "Esta carpeta no es un paquete completo: falta servicio\PapeleriaPos.Servicio.exe o caja\PapeleriaPos.Caja.exe ($paquete)."
}
# Sin la marca de «descargado de internet»: Windows no pregunta al abrir la caja.
Get-ChildItem -LiteralPath $paquete -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue

Escribir "Papeleria POS: actualizar esta PC con el paquete de $paquete"
Limpiar-Restos
$servicio = Servicio-Instalado
$cajaInstalada = Test-Path -LiteralPath (Join-Path $carpetaCaja 'PapeleriaPos.Caja.exe')
if (-not $servicio -and -not $cajaInstalada) {
    Terminar 'En esta PC no esta instalado el servicio ni la caja. La primera vez se instalan con servicio\instalar-servicio.ps1 y caja\instalar-caja.ps1.'
}

$versionNuevaServicio = Leer-Version $nuevoServicio
$versionNuevaCaja = Leer-Version $nuevaCaja
$versionServicio = $null
$versionCaja = $null
if ($servicio) { $versionServicio = Leer-Version $servicio.Exe }
if ($cajaInstalada) { $versionCaja = Leer-Version (Join-Path $carpetaCaja 'PapeleriaPos.Caja.exe') }
if ($servicio) { Escribir ('  Servicio: {0} -> {1}   ({2})' -f (Texto $versionServicio), $versionNuevaServicio.Texto, $servicio.Carpeta) }
else { Escribir '  Servicio: no esta instalado en esta PC (solo se actualiza la caja).' }
if ($cajaInstalada) { Escribir ('  Caja:     {0} -> {1}' -f (Texto $versionCaja), $versionNuevaCaja.Texto) }
else { Escribir '  Caja:     no esta instalada en esta PC (solo se actualiza el servicio).' }

$anteriores = @()
$todasIguales = $true
foreach ($parte in @(
        @{ Que = 'el servicio'; Hay = [bool]$servicio; Antes = $versionServicio; Despues = $versionNuevaServicio },
        @{ Que = 'la caja'; Hay = $cajaInstalada; Antes = $versionCaja; Despues = $versionNuevaCaja })) {
    if (-not $parte.Hay) { continue }
    if (-not $parte.Antes -or $parte.Despues.Numero -ne $parte.Antes.Numero) { $todasIguales = $false }
    if ($parte.Antes -and $parte.Despues.Numero -lt $parte.Antes.Numero) {
        $anteriores += '{0} ({1} -> {2})' -f $parte.Que, $parte.Antes.Texto, $parte.Despues.Texto
    }
}
if ($anteriores.Count -gt 0 -and -not $Forzar) {
    Terminar ('El paquete trae una version anterior a la instalada: {0}. Para regresar a ella a proposito: actualizar.ps1 -Forzar.' -f ($anteriores -join ' y '))
}
if ($todasIguales -and -not $Forzar) {
    Escribir 'Ya esta instalada esta version: no hay nada que actualizar (para volver a ponerla: actualizar.ps1 -Forzar).' 'Green'
    if ($servicio -and ($servicio.Estado -ne 'Running' -or $servicio.Modo -eq 'Disabled')) {
        Escribir ('Ojo: el servicio esta {0} ({1}). Para volver a ponerlo y arrancarlo: actualizar.ps1 -Forzar.' -f $servicio.Estado, $servicio.Modo) 'Yellow'
    }
    exit 0
}
if ($anteriores.Count -gt 0) { Escribir ('Se pone una version anterior: {0} (-Forzar).' -f ($anteriores -join ' y ')) 'Yellow' }

$script:anterior = Texto $versionServicio
$nueva = $versionNuevaServicio.Texto
if (-not $servicio) { $script:anterior = Texto $versionCaja; $nueva = $versionNuevaCaja.Texto }

# ══ 2. Antes de tocar nada ══

$yaDanada = $false
if ($servicio) {
    if (-not (Test-Path -LiteralPath $servicio.Exe)) {
        if (-not $Forzar) { Terminar "El servicio esta registrado, pero su programa no esta en $($servicio.Exe). Reinstalalo con servicio\instalar-servicio.ps1 -Quitar y luego servicio\instalar-servicio.ps1; con -Forzar se pone este encima." }
        Escribir "El programa del servicio no estaba en $($servicio.Exe): se pone el del paquete (-Forzar)." 'Yellow'
    }
    if (-not (Test-Path -LiteralPath $base)) {
        if (-not $Forzar) { Terminar "No esta la base en $base: no habria que regresar si algo sale mal. Si el servicio nunca ha arrancado, no hay nada que respaldar: actualizar.ps1 -Forzar. Si usa otra base (ConnectionStrings:Pos), se actualiza a mano." }
        Escribir "No esta la base en $base: se sigue sin respaldarla (-Forzar)." 'Yellow'
    }
    if (Test-Path -LiteralPath (Join-Path $datos 'pos.db.danada')) {
        $yaDanada = $true
        if (-not $Forzar) { Terminar 'La base tiene el aviso de danada (pos.db.danada): primero se restaura el respaldo de antes del apagon (lo dice la franja de la caja). Actualizar no lo arregla; con -Forzar se actualiza igual.' }
    }
    if ($servicio.Estado -eq 'Running') {
        $estadoAntes = $null
        for ($intento = 1; $intento -le 8 -and -not $estadoAntes; $intento++) {
            $estadoAntes = Leer-Estado
            if (-not $estadoAntes) { Start-Sleep -Seconds 2 }
        }
        if (-not $estadoAntes) {
            if (-not $Forzar) { Terminar "El servicio esta en marcha pero no contesta en $estadoUrl. Si se cambio su puerto (Urls en servicio.json), pasalo con -Puerto; con -Forzar se actualiza igual (y si algo sale mal, no se podra saber si la version de antes quedo bien)." }
            Escribir "El servicio no contesta en $estadoUrl; se sigue (-Forzar)." 'Yellow'
        } elseif ($estadoAntes.avisoBase) {
            $yaDanada = $true
            if (-not $Forzar) { Terminar "El servicio ya avisa: $($estadoAntes.avisoBase) Actualizar no lo arregla; con -Forzar se actualiza igual." }
        }
    }
    if (Test-Path -LiteralPath $servicio.Carpeta) {
        $propios = @(Ajustes-Propios $servicio.Carpeta (Join-Path $paquete 'servicio'))
        if ($propios.Count -gt 0) {
            $lista = ($propios | Select-Object -First 12) -join ', '
            if (-not $Forzar) {
                Terminar ("El appsettings del programa instalado trae ajustes que el del paquete no trae igual: {0}. Lo de esta PC va en {1}, que actualizar no toca (la clave de eleventa no: va solo en la variable de entorno Eleventa__Clave del servicio). Pasalos ahi (como administrador) y vuelve a correr actualizar.ps1. Con -Forzar se actualiza sin ellos; el appsettings de antes queda en el respaldo." -f $lista, $configuracion)
            }
            Escribir "Se actualiza sin los ajustes propios del appsettings de antes (-Forzar): $lista." 'Yellow'
        }
    }
}

$necesario = [long]0
if ($servicio) {
    foreach ($archivo in $archivosDeLaBase) { $necesario += Tamano (Join-Path $datos $archivo) }
    $necesario += (Tamano $servicio.Carpeta) + (Tamano (Join-Path $paquete 'servicio'))
}
if ($cajaInstalada) { $necesario += (Tamano $carpetaCaja) + (Tamano (Join-Path $paquete 'caja')) }
$libre = (New-Object System.IO.DriveInfo ([IO.Path]::GetPathRoot($datos))).AvailableFreeSpace
if ($libre -lt $necesario + 500MB) {
    Terminar ('No hay espacio en el disco para el respaldo: hacen falta unos {0:N0} MB y hay {1:N0} MB libres.' -f (($necesario + 500MB) / 1MB), ($libre / 1MB))
}

# ══ 3. La caja ══

if ($cajaInstalada) { Cerrar-Caja }

# ══ 4. a 8.: del respaldo a revisar que contesta (en una función: si se interrumpe a la mitad, el final del script lo atiende) ══

function Actualizar {
    # ── 4. y 5. Detener el servicio y respaldar ──

    $sello = Get-Date -Format 'yyyy-MM-dd_HH.mm.ss'
    $etiqueta = $script:anterior -replace '[^0-9A-Za-z.\-]', '_'
    $script:respaldo = Join-Path $actualizaciones ('{0}-{1}' -f $sello, $etiqueta)
    for ($n = 2; Test-Path -LiteralPath $script:respaldo; $n++) { $script:respaldo = Join-Path $actualizaciones ('{0}-{1}-{2}' -f $sello, $etiqueta, $n) }
    [IO.Directory]::CreateDirectory($script:respaldo) | Out-Null
    $script:bitacora = Join-Path $script:respaldo 'actualizar.log'
    [IO.File]::WriteAllLines($script:bitacora, $script:pendiente, $utf8)
    Escribir "El respaldo y el registro de esta actualizacion van en $($script:respaldo)."

    try {
        if ($servicio) {
            Escribir 'Deteniendo el servicio...'
            Set-Service -Name $nombre -StartupType Disabled
            Detener-Servicio
            Escribir 'Respaldando la base y el servicio...'
            Respaldar-Base (Join-Path $script:respaldo 'base')
            if (Test-Path -LiteralPath $servicio.Carpeta) { Copiar-Carpeta $servicio.Carpeta (Join-Path $script:respaldo 'servicio') }
        }
        if ($cajaInstalada) {
            Escribir 'Respaldando la caja...'
            Copiar-Carpeta $carpetaCaja (Join-Path $script:respaldo 'caja')
        }
    } catch {
        Escribir "No se pudo respaldar: $(Mensaje $_)" 'Red'
        if ($servicio) {
            try { Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo); [void](Arrancar-Servicio) } catch { }
        }
        Escribir 'No se cambio nada: el servicio sigue con la version de antes.' 'Red'
        Salir 1
    }

    # ── 6., 7. y 8. Cambiar, arrancar y revisar ──

    $script:inicioDelCambio = Get-Date
    $script:arranque = $null
    # Lo que ya se cambió: es lo único que regresa la vuelta atrás.
    $script:cambiado = @{ Servicio = $false; Ruta = $false; Caja = $false }
    try {
        if ($servicio) {
            Escribir 'Poniendo el servicio nuevo...'
            $script:cambiado.Servicio = $true
            Reemplazar-Carpeta $carpetaServicio (Join-Path $paquete 'servicio')
            if (-not (Misma-Carpeta $servicio.Carpeta $carpetaServicio)) {
                # Con la ruta entre comillas (Archivos de programa lleva espacio). Sin borrar el servicio: se queda todo lo demás,
                # como su variable de entorno con la clave de eleventa.
                $script:cambiado.Ruta = $true
                Cambiar-Ruta ('"{0}"' -f (Join-Path $carpetaServicio 'PapeleriaPos.Servicio.exe'))
            }
            Poner-Configuracion
        }
        if ($cajaInstalada) {
            Escribir 'Poniendo la caja nueva...'
            $script:cambiado.Caja = $true
            Reemplazar-Carpeta $carpetaCaja (Join-Path $paquete 'caja')
        }
    } catch {
        Regresar "no se pudieron poner los programas nuevos ($(Mensaje $_))"
    }

    if ($servicio) {
        Escribir "Arrancando el servicio y esperando a que conteste con la version $nueva..."
        Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo)
        $script:arranque = Get-Date
        $fallo = Arrancar-Servicio
        if ($fallo) {
            Regresar "Windows no lo pudo arrancar ($fallo)"
        }
        $resultado = Esperar-Servicio $nueva ($MinutosDeEspera * 60)
        if (-not $resultado.Bien) {
            Regresar "no contesto con la version $nueva ($($resultado.Porque))"
        }
        if ($resultado.Estado.avisoBase -and -not $yaDanada) {
            Regresar "la revision de la base al arrancar aviso: $($resultado.Estado.avisoBase)"
        }
    }

    Escribir ''
    Escribir "Actualizado de $($script:anterior) a $nueva." 'Green'
    if ($script:cambiado.Ruta) { Escribir "El servicio estaba en $($servicio.Carpeta): ahora va en $carpetaServicio. La carpeta de antes ya no se usa (se puede borrar)." }
    Escribir "El respaldo de antes (la base, el servicio y la caja) quedo en $($script:respaldo)."
    if ($cajaInstalada) { Escribir 'Ya se puede abrir la caja.' }
    Limpiar-Actualizaciones
    Salir 0
}

try {
    Actualizar
} finally {
    # Ctrl+C o un error que no se esperaba, ya empezado el cambio: el servicio no se queda deshabilitado sin decirlo.
    if (-not $script:terminado -and $servicio) {
        try { Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo) } catch { }
        Escribir 'Se interrumpio a la mitad. Vuelve a correr actualizar.ps1 con este paquete (con -Forzar si dice que ya esta instalada): recupera lo que quedo a medias.' 'Red'
        if ($script:respaldo) { Escribir "Lo de antes esta en $($script:respaldo)." 'Red' }
    }
}
