#Requires -RunAsAdministrator
<#
    Lo que hace en esta PC el instalador de Papeleria POS (instalacion/papeleria-pos.iss; docs/instalacion.md), sin preguntar nada: el
    asistente (Inno Setup) hace las preguntas y deja las respuestas en un archivo («clave=valor»). Reusa los scripts de siempre, los que
    se corren a mano en la tienda (docs/pruebas-en-tienda.md) y que el flujo «Instalación» prueba en Windows. Nunca recibe ni escribe una
    clave: la de eleventa y el codigo de Control Print los pone el asistente directo en las variables del servicio.

        powershell -ExecutionPolicy Bypass -File instalar-pc.ps1 -Paso instalar -Respuestas <archivo> -Paquete <carpeta> -Ayudante <exe> -Registro <log> -Resultado <archivo>
        powershell -ExecutionPolicy Bypass -File instalar-pc.ps1 -Paso revisar -Respuestas <archivo> -Paquete <carpeta> -Ayudante <exe> -Registro <log> -Resultado <archivo> [-Reiniciar]
        powershell -ExecutionPolicy Bypass -File instalar-pc.ps1 -Paso quitar -Registro <log> -Resultado <archivo> [-BorrarDatos]

    -Paso instalar  Pone o actualiza el programa: la primera vez, instalar-servicio.ps1 (sin el 5081 en «La unica caja») e
                    instalar-caja.ps1; si ya estaba (con los scripts o con el instalador), actualizar.ps1, con su respaldo de la base y
                    su vuelta atras. Despues abre el firewall segun lo que es la PC, deja «Conservar documentos impresos» en la cola que
                    mide la caja y escribe servicio.json y caja.json con PapeleriaPos.Instalacion.exe (solo lo que cambio).
    -Paso revisar   Reinicia el servicio si se pide (-Reiniciar: cambio servicio.json o una variable) y dice si contestan el servicio,
                    eleventa y Control Print (poner-clave-eleventa.ps1 y poner-codigo-controlprint.ps1 con -SoloRevisar); en «Otra
                    caja», si contesta la caja principal.
    -Paso quitar    Desinstala: instalar-caja.ps1 -Quitar e instalar-servicio.ps1 -Quitar (el servicio, sus variables, el firewall, el
                    programa y el acceso del escritorio). La base, los respaldos, servicio.json y lo de la caja se quedan en
                    C:\ProgramData\PapeleriaPos, salvo con -BorrarDatos. Ni con -BorrarDatos se borra lo de eleventa que deja ahi el
                    instructivo (respaldos-eleventa, firebird20 y ensayo, y la carpeta de respaldos que diga servicio.json si esta
                    adentro), ni se sigue un enlace: de un enlace se quita el enlace. eleventa y Control Print no se tocan.

    Lo que pasa va a -Registro; para el asistente, «clave=valor» en -Resultado. Termina con 0 si quedo, con 1 si no (lo dice
    «motivo»; lo de antes se queda) y con 2 si actualizar.ps1 no pudo regresar solo a la version de antes (lo de antes esta completo en su
    respaldo). Los mensajes van sin acentos: asi se leen igual en el registro y en cualquier consola.
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('instalar', 'revisar', 'quitar')][string]$Paso,
    [string]$Respuestas,
    [string]$Paquete,
    [string]$Ayudante,
    [string]$Registro,
    [string]$Resultado,
    [switch]$Reiniciar,
    [switch]$BorrarDatos
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$nombre = 'PapeleriaPos'
$datos = Join-Path $env:ProgramData 'PapeleriaPos'
$programa = Join-Path $env:ProgramFiles 'PapeleriaPos'
$exeCaja = Join-Path $programa 'Caja\PapeleriaPos.Caja.exe'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$reglaCajas = 'Papeleria POS - cajas'
$reglaBuscar = 'Papeleria POS - buscar en la red'
$estadoUrl = 'http://localhost:5080/api/estado'
$utf8 = New-Object System.Text.UTF8Encoding $true
$script:resultado = New-Object System.Collections.Generic.List[string]

# ── El registro y el resultado ──

function Escribir([string]$texto) {
    Write-Host $texto
    if ($Registro) { [IO.File]::AppendAllText($Registro, ('{0:HH:mm:ss} {1}' -f (Get-Date), $texto) + "`r`n", $utf8) }
}

function Resultado([string]$clave, [string]$valor) {
    $script:resultado.Add("$clave=$($valor -replace '[\r\n]+', ' ')")
}

function Terminar([int]$codigo) {
    if ($Resultado) {
        $carpeta = Split-Path -Parent $Resultado
        if ($carpeta) { [IO.Directory]::CreateDirectory($carpeta) | Out-Null }
        [IO.File]::WriteAllLines($Resultado, [string[]](@('# Papeleria POS: lo que hizo instalar-pc.ps1') + $script:resultado), $utf8)
    }
    exit $codigo
}

function Fallo([string]$porque, [int]$codigo = 1) {
    Escribir "NO QUEDO: $porque"
    Resultado 'motivo' $porque
    Terminar $codigo
}

function Leer-Claves([string]$ruta) {
    $claves = @{}
    foreach ($renglon in [IO.File]::ReadAllLines($ruta, [Text.Encoding]::UTF8)) {
        $renglon = $renglon.TrimStart([char]0xFEFF)
        if (-not $renglon -or $renglon.StartsWith('#')) { continue }
        $igual = $renglon.IndexOf('=')
        if ($igual -le 0) { continue }
        $claves[$renglon.Substring(0, $igual).Trim()] = $renglon.Substring($igual + 1)
    }
    $claves
}

# ── Los scripts de siempre y el ayudante ──

# Un script como en la tienda: otra ventana de Windows PowerShell. Lo que escribe va al registro; devuelve su codigo y sus renglones.
function Correr([string]$archivo, [string[]]$argumentos = @()) {
    Escribir ">> $(Split-Path -Leaf $archivo) $($argumentos -join ' ')"
    $antes = $ErrorActionPreference
    # Lo que escribe en la salida de errores no detiene esto: se revisa el codigo.
    $ErrorActionPreference = 'Continue'
    try {
        $salida = @(& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $archivo @argumentos 2>&1 | ForEach-Object { "$_" })
        $codigo = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $antes
    }
    foreach ($linea in $salida) { Escribir "   $linea" }
    Escribir "<< termino con $codigo"
    $global:LASTEXITCODE = 0
    [pscustomobject]@{ Codigo = $codigo; Lineas = $salida }
}

# Lo que dijo al final un script (sus ultimos renglones con algo), para que el asistente lo ensene.
function Lo-Que-Dijo($corrida, [int]$cuantos = 3) {
    $lineas = @($corrida.Lineas | Where-Object { $_ -and $_.Trim() -and $_ -notmatch '^\s*(Esperando|Reiniciando)' } | ForEach-Object { $_.Trim() })
    if ($lineas.Count -eq 0) { return "termino con $($corrida.Codigo)" }
    ($lineas | Select-Object -Last $cuantos) -join ' '
}

# PapeleriaPos.Instalacion.exe: deja lo suyo en un archivo aparte, que se lee y se borra.
function Ayudante([string[]]$argumentos) {
    $salida = Join-Path ([IO.Path]::GetTempPath()) ('papeleriapos-' + [guid]::NewGuid().ToString('N') + '.txt')
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Ayudante @argumentos --salida $salida 2>&1 | ForEach-Object { Escribir "   $_" }
        $codigo = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        $claves = if (Test-Path -LiteralPath $salida) { Leer-Claves $salida } else { @{} }
        [pscustomobject]@{ Codigo = $codigo; Claves = $claves }
    } finally {
        $ErrorActionPreference = $antes
        Remove-Item -LiteralPath $salida -Force -ErrorAction SilentlyContinue
    }
}

# ── Lo que hay en esta PC ──

function Exe-Del-Servicio {
    $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nombre'"
    if (-not $cim) { return $null }
    $ruta = [string]$cim.PathName
    if ($ruta -match '^\s*"([^"]+)"') { $Matches[1] } elseif ($ruta -match '^\s*(.+?\.exe)') { $Matches[1] } else { $ruta.Trim() }
}

# Como la lee actualizar.ps1: la del producto, sin lo de despues del «+».
function Version-De([string]$exe) {
    if (-not $exe -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $null }
    $info = (Get-Item -LiteralPath $exe).VersionInfo
    $dll = [IO.Path]::ChangeExtension($exe, '.dll')
    if (-not $info.ProductVersion -and (Test-Path -LiteralPath $dll -PathType Leaf)) { $info = (Get-Item -LiteralPath $dll).VersionInfo }
    if ($info.ProductVersion) { ($info.ProductVersion -split '\+')[0].Trim() } else { $null }
}

# Solo la de esta instalacion: la de la prueba con una persona real (PapeleriaPos-Prueba) usa otra carpeta.
function Cajas-Abiertas {
    @(Get-CimInstance -ClassName Win32_Process -Filter "Name='PapeleriaPos.Caja.exe'" | Where-Object {
            -not $_.ExecutablePath -or $_.ExecutablePath.StartsWith((Join-Path $programa 'Caja') + '\', [StringComparison]::OrdinalIgnoreCase) })
}

function Regla([string]$cual) {
    netsh advfirewall firewall show rule name="$cual" | Out-Null
    $esta = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    $esta
}

function Leer-Estado {
    try { Invoke-RestMethod -Uri $estadoUrl -UseBasicParsing -TimeoutSec 5 } catch { $null }
}

function Esperar-Estado([int]$segundos) {
    $limite = (Get-Date).AddSeconds($segundos)
    do {
        $estado = Leer-Estado
        if ($estado) { return $estado }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $limite)
    $null
}

# ══ instalar ══

function Instalar {
    $r = Leer-Claves $Respuestas
    $rol = [string]$r['rol']
    if ($rol -notin @('principal', 'unica', 'otra')) { Fallo "El asistente no dijo que es esta PC (rol '$rol')." }
    $conServicio = $rol -ne 'otra'
    Resultado 'rol' $rol
    Escribir "Papeleria POS: instalar esta PC como '$rol', con el paquete de $Paquete"
    Escribir 'Las respuestas (sin claves: esas no pasan por aqui):'
    foreach ($clave in @($r.Keys | Sort-Object)) { Escribir "  $clave=$($r[$clave])" }

    $habiaServicio = [bool](Get-Service -Name $nombre -ErrorAction SilentlyContinue)
    $habiaCaja = Test-Path -LiteralPath $exeCaja
    if (-not $conServicio -and $habiaServicio) {
        Fallo 'En esta PC esta el servicio con la base de la tienda: no puede ser «Otra caja». No se cambio nada.'
    }
    $versionAntes = if ($habiaServicio) { Version-De (Exe-Del-Servicio) } elseif ($habiaCaja) { Version-De $exeCaja } else { $null }
    Resultado 'version-antes' $versionAntes

    # La caja cerrada: el asistente ya lo pidio. Con «cerrar-caja» (en silencio), a la fuerza: la venta que se atendia puede regresar sin
    # lo ultimo que se escaneo (los tickets en espera se guardan).
    $abiertas = @(Cajas-Abiertas)
    if ($abiertas.Count -gt 0) {
        if ($r['cerrar-caja'] -ne 'si') { Fallo 'La caja esta abierta en esta PC: cobra o deja en espera la venta, cierrala y vuelve a correr el instalador. No se cambio nada.' }
        Escribir 'La caja esta abierta: se cierra a la fuerza (cerrar-caja).'
        foreach ($proceso in $abiertas) { Stop-Process -Id $proceso.ProcessId -Force -ErrorAction SilentlyContinue }
        $limite = (Get-Date).AddSeconds(15)
        while ((Cajas-Abiertas).Count -gt 0 -and (Get-Date) -lt $limite) { Start-Sleep -Milliseconds 500 }
    }

    # El programa: si ya estaba (con los scripts o con el instalador), actualizar.ps1, que respalda la base y los programas y regresa
    # solo si la version nueva no arranca. Con la misma version no hace nada.
    if ($habiaServicio -or $habiaCaja) {
        $corrida = Correr (Join-Path $Paquete 'actualizar.ps1')
        if ($corrida.Codigo -eq 2) {
            Resultado 'programa' 'no-regreso'
            Fallo "No se actualizo y no pudo regresar solo: $(Lo-Que-Dijo $corrida)" 2
        }
        if ($corrida.Codigo -ne 0) {
            Resultado 'programa' 'fallo'
            Fallo "No se actualizo: $(Lo-Que-Dijo $corrida)"
        }
    }
    if ($conServicio -and -not $habiaServicio) {
        $argumentos = @()
        if ($rol -eq 'unica') { $argumentos += '-SinBuscarEnLaRed' }
        $corrida = Correr (Join-Path $Paquete 'servicio\instalar-servicio.ps1') $argumentos
        if ($corrida.Codigo -ne 0) {
            Resultado 'programa' 'fallo'
            Fallo "No se instalo el servicio: $(Lo-Que-Dijo $corrida)"
        }
    }
    if (-not (Test-Path -LiteralPath $exeCaja)) {
        $corrida = Correr (Join-Path $Paquete 'caja\instalar-caja.ps1')
        if ($corrida.Codigo -ne 0 -or -not (Test-Path -LiteralPath $exeCaja)) {
            Resultado 'programa' 'fallo'
            Fallo "No se instalo la caja: $(Lo-Que-Dijo $corrida)"
        }
    }
    $version = if ($conServicio) { Version-De (Exe-Del-Servicio) } else { Version-De $exeCaja }
    Resultado 'version' $version
    Resultado 'programa' $(if (-not ($habiaServicio -or $habiaCaja)) { 'instalado' } elseif ($versionAntes -ne $version) { 'actualizado' } else { 'igual' })
    Resultado 'servicio-nuevo' $(if ($conServicio -and -not $habiaServicio) { 'si' } else { 'no' })

    # El firewall, segun lo que es esta PC: el 5080 para la red local siempre que tenga el servicio (las cajas y los celulares) y el
    # 5081 (buscarla en la red) solo en la caja principal. instalar-servicio.ps1 ya lo hizo la primera vez; al actualizar o al cambiar de
    # «Caja principal» a «La unica caja» (o al reves), aqui.
    if ($conServicio) {
        if (-not (Regla $reglaCajas)) {
            netsh advfirewall firewall add rule name="$reglaCajas" dir=in action=allow protocol=TCP localport=5080 remoteip=localsubnet profile=any | Out-Null
            Escribir "Firewall: se abrio el 5080 para la red local ($reglaCajas)."
        }
        if ($rol -eq 'unica') {
            if (Regla $reglaBuscar) {
                netsh advfirewall firewall delete rule name="$reglaBuscar" | Out-Null
                Escribir "Firewall: se cerro el 5081 (es la unica caja: nadie la busca en la red)."
            }
        } elseif (-not (Regla $reglaBuscar)) {
            netsh advfirewall firewall add rule name="$reglaBuscar" dir=in action=allow protocol=UDP localport=5081 remoteip=localsubnet profile=any | Out-Null
            Escribir "Firewall: se abrio el 5081 para que las otras cajas la encuentren ($reglaBuscar)."
        }
        $global:LASTEXITCODE = 0
    }

    # La cola que mide la caja: «Conservar documentos impresos», como instalar-caja.ps1 -ColaImpresion (el agente quita lo ya medido).
    $cola = [string]$r['cola']
    if ($cola) {
        try {
            Set-Printer -Name $cola -KeepPrintedJobs $true -ErrorAction Stop
            Escribir "La cola '$cola' conserva los documentos impresos."
            Resultado 'cola' 'bien'
        } catch {
            Escribir "No se pudo activar 'Conservar documentos impresos' en la cola '$cola': $($_.Exception.Message)"
            Resultado 'cola' 'no-se-pudo'
            Resultado 'cola-mensaje' "No se pudo activar «Conservar documentos impresos» en «$cola»: $($_.Exception.Message) Activalo en sus propiedades (Opciones avanzadas)."
        }
    } else {
        Resultado 'cola' 'no-aplica'
    }

    # servicio.json y caja.json: solo lo que cambio, sin tocar lo demas (el ayudante; lo de antes queda en «.anterior»).
    $escrito = Ayudante @('escribir', '--respuestas', $Respuestas)
    if ($escrito.Codigo -ne 0) {
        Resultado 'configuracion' 'fallo'
        Fallo "No se escribio la configuracion de esta PC: $($escrito.Claves['error'])"
    }
    $cambio = $escrito.Claves['servicio-json'] -eq 'cambio' -or $escrito.Claves['caja-json'] -eq 'cambio'
    Resultado 'configuracion' $(if ($cambio) { 'cambio' } else { 'igual' })
    Resultado 'servicio-json' $escrito.Claves['servicio-json']
    Resultado 'caja-json' $escrito.Claves['caja-json']
    Resultado 'cambios' $escrito.Claves['cambios']
    Escribir "La configuracion: $(if ($escrito.Claves['cambios']) { $escrito.Claves['cambios'] } else { 'sin cambios' })."
    Escribir "Listo: Papeleria POS $version en esta PC ('$rol')."
    Terminar 0
}

# ══ revisar ══

function Revisar-Con([string]$que, [string]$archivo, [string[]]$argumentos) {
    $corrida = Correr $archivo $argumentos
    Resultado $que $(if ($corrida.Codigo -eq 0) { 'contesta' } else { 'no-contesta' })
    Resultado "$que-mensaje" (Lo-Que-Dijo $corrida 4)
}

function Revisar {
    $r = Leer-Claves $Respuestas
    $rol = [string]$r['rol']
    if ($rol -eq 'otra') {
        Resultado 'servicio' 'no-aplica'
        Resultado 'eleventa' 'no-aplica'
        Resultado 'controlprint' 'no-aplica'
        if ($r['principal']) {
            $probado = Ayudante @('probar', '--direccion', [string]$r['principal'])
            Resultado 'principal' $(if ($probado.Claves['contesta'] -eq 'si') { 'contesta' } else { 'no-contesta' })
            Resultado 'principal-mensaje' $probado.Claves['texto']
            Escribir "La caja principal: $($probado.Claves['texto'])"
        } else {
            Resultado 'principal' 'no-aplica'
        }
        Terminar 0
    }
    if ($Reiniciar) {
        # Lee servicio.json y sus variables (la clave, el codigo) al arrancar.
        Escribir 'Reiniciando el servicio...'
        try { Restart-Service -Name $nombre -Force -ErrorAction Stop } catch { Escribir "  No se pudo reiniciar: $($_.Exception.Message)" }
    }
    $estado = Esperar-Estado 120
    if (-not $estado) {
        Resultado 'servicio' 'no-contesta'
        Resultado 'servicio-mensaje' 'El servicio no contesto en 2 minutos: revisa en Servicios (services.msc) que «Papeleria POS» este en ejecucion.'
        Resultado 'eleventa' $(if ($r['eleventa'] -eq 'si') { 'no-contesta' } else { 'no-aplica' })
        Resultado 'controlprint' $(if ($r['controlprint'] -eq 'si') { 'no-contesta' } else { 'no-aplica' })
        Escribir 'El servicio no contesto en 2 minutos.'
        Terminar 0
    }
    Resultado 'servicio' 'contesta'
    Resultado 'servicio-version' ([string]$estado.version)
    Escribir "El servicio contesta (version $($estado.version))."
    if ($r['eleventa'] -eq 'si') {
        Revisar-Con 'eleventa' (Join-Path $Paquete 'servicio\poner-clave-eleventa.ps1') @('-SoloRevisar', '-SegundosDeEspera', '120')
    } else {
        Resultado 'eleventa' 'no-aplica'
    }
    if ($r['controlprint'] -eq 'si') {
        Revisar-Con 'controlprint' (Join-Path $Paquete 'servicio\poner-codigo-controlprint.ps1') @('-SoloRevisar')
    } else {
        Resultado 'controlprint' 'no-aplica'
    }
    Terminar 0
}

# ══ quitar ══

# Lo que «Borrar tambien los datos» nunca borra (docs/instalacion.md, «Desinstalar»): lo de eleventa que deja el instructivo en
# C:\ProgramData\PapeleriaPos (los respaldos de su base de verdad, la copia de Firebird 2.0 con su gbak y lo del ensayo) y la carpeta de
# respaldos de eleventa que diga servicio.json, si esta adentro. Cada una con su \ al final.
function Lo-Que-Se-Queda {
    $quedan = New-Object System.Collections.Generic.List[string]
    foreach ($carpeta in @('respaldos-eleventa', 'firebird20', 'ensayo')) { $quedan.Add((Join-Path $datos $carpeta)) }
    $archivo = Join-Path $datos 'servicio.json'
    if (Test-Path -LiteralPath $archivo -PathType Leaf) {
        # Con una expresion y no con ConvertFrom-Json (servicio.json lleva comentarios): si tambien aparece en un comentario, se queda
        # de mas, nunca de menos.
        $texto = [IO.File]::ReadAllText($archivo)
        foreach ($encontrada in [regex]::Matches($texto, '"CarpetaRespaldos"\s*:\s*"((?:[^"\\]|\\.)*)"', 'IgnoreCase')) {
            try {
                $valor = ('{"v":"' + $encontrada.Groups[1].Value + '"}' | ConvertFrom-Json).v
                if (-not $valor) { continue }
                if (-not [IO.Path]::IsPathRooted($valor)) { $valor = Join-Path $datos $valor }
                $quedan.Add([IO.Path]::GetFullPath($valor))
            } catch {
                Escribir "  No se entendio la carpeta de respaldos de servicio.json ($($encontrada.Groups[1].Value)): $($_.Exception.Message)"
            }
        }
    }
    @($quedan | ForEach-Object { $_.TrimEnd('\') + '\' })
}

function Es-Enlace([IO.FileSystemInfo]$cosa) {
    ($cosa.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}

# Borra sin seguir enlaces: de un enlace («junction» o simbolico) se quita el enlace, nunca lo que hay del otro lado (Remove-Item
# -Recurse de Windows PowerShell 5.1 si entra en una junction).
function Borrar-Sin-Seguir-Enlaces([IO.FileSystemInfo]$cosa) {
    if (Es-Enlace $cosa) {
        if ($cosa -is [IO.DirectoryInfo]) { [IO.Directory]::Delete($cosa.FullName, $false) } else { [IO.File]::Delete($cosa.FullName) }
        return
    }
    if ($cosa -is [IO.DirectoryInfo]) {
        foreach ($hijo in @($cosa.GetFileSystemInfos())) { Borrar-Sin-Seguir-Enlaces $hijo }
        # Sin «solo lectura» (Normal), que no deja borrarla.
        $cosa.Attributes = [IO.FileAttributes]::Normal
        [IO.Directory]::Delete($cosa.FullName, $false)
    } else {
        $cosa.Attributes = [IO.FileAttributes]::Normal
        [IO.File]::Delete($cosa.FullName)
    }
}

# Borra lo de adentro de esa carpeta menos lo que se queda: de una carpeta que lleva a algo que se queda, solo lo demas.
function Borrar-Menos([string]$carpeta, [string[]]$quedan) {
    foreach ($hijo in @((New-Object IO.DirectoryInfo $carpeta).GetFileSystemInfos())) {
        $ruta = $hijo.FullName.TrimEnd('\') + '\'
        if (@($quedan | Where-Object { $_ -ieq $ruta }).Count -gt 0) {
            Escribir "  Se queda: $($hijo.FullName)"
            continue
        }
        if (@($quedan | Where-Object { $_.StartsWith($ruta, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
            if (Es-Enlace $hijo) {
                Escribir "  Se queda: $($hijo.FullName) (un enlace hacia lo que se queda)"
            } else {
                Borrar-Menos $hijo.FullName $quedan
            }
            continue
        }
        Borrar-Sin-Seguir-Enlaces $hijo
    }
}

function Quitar {
    Escribir 'Papeleria POS: desinstalar de esta PC.'
    $bien = $true
    foreach ($cual in @('instalar-caja.ps1', 'instalar-servicio.ps1')) {
        $corrida = Correr (Join-Path $PSScriptRoot $cual) @('-Quitar')
        if ($corrida.Codigo -ne 0) {
            $bien = $false
            Resultado 'motivo' "$cual -Quitar: $(Lo-Que-Dijo $corrida)"
        }
    }
    if ($BorrarDatos -and (Test-Path -LiteralPath $datos) -and (Es-Enlace (Get-Item -LiteralPath $datos -Force))) {
        # Nunca a traves de un enlace: lo del otro lado no es de esta instalacion.
        $bien = $false
        Escribir "$datos es un enlace a otra carpeta: no se borra nada de alli."
        Resultado 'motivo' "$datos es un enlace a otra carpeta: por seguridad no se borro nada de alli (si de verdad se quiere, a mano)."
        Resultado 'datos' 'quedaron'
    } elseif ($BorrarDatos) {
        $quedan = Lo-Que-Se-Queda
        Escribir "Tambien se borran los datos de $datos (la base, sus respaldos, servicio.json y lo de la caja). Lo de eleventa se queda."
        $borrados = $true
        for ($intento = 1; (Test-Path -LiteralPath $datos); $intento++) {
            try {
                Borrar-Menos $datos $quedan
                if (@((New-Object IO.DirectoryInfo $datos).GetFileSystemInfos()).Count -eq 0) { [IO.Directory]::Delete($datos, $false) }
                break
            } catch {
                if ($intento -ge 5) {
                    $bien = $false
                    $borrados = $false
                    Resultado 'motivo' "No se pudo borrar todo $datos`: $($_.Exception.Message)"
                    break
                }
                Start-Sleep -Seconds 2
            }
        }
        $seQuedaron = @($quedan | ForEach-Object { $_.TrimEnd('\') } | Where-Object { Test-Path -LiteralPath $_ })
        Resultado 'datos' $(if ($borrados) { 'borrados' } else { 'quedaron' })
        Resultado 'se-quedan' ($seQuedaron -join '; ')
        if ($seQuedaron.Count -gt 0) { Escribir "Se quedaron (lo de eleventa): $($seQuedaron -join '; ')" }
    } else {
        Escribir "Los datos se quedan en $datos (la base, los respaldos, servicio.json y lo de la caja)."
        Resultado 'datos' 'se-quedan'
    }
    Resultado 'quitado' $(if ($bien) { 'si' } else { 'no' })
    Terminar $(if ($bien) { 0 } else { 1 })
}

# ══ Aqui empieza ══

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    Write-Host 'Esta es la ventana de PowerShell de 32 bits (x86): el instalador corre la de 64 bits.'
    exit 1
}
# Que lo que escriben los scripts que se corren aqui llegue con sus acentos (comparten la misma consola, oculta).
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
# Sin el proxy de la PC para hablar con el servicio de esta misma PC.
if ($PSVersionTable.PSVersion.Major -lt 6) { [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy }
if ($Registro) { [IO.Directory]::CreateDirectory((Split-Path -Parent $Registro)) | Out-Null }
if ($Paso -ne 'quitar' -and (-not $Respuestas -or -not (Test-Path -LiteralPath $Respuestas))) { Fallo "No estan las respuestas del asistente ($Respuestas)." }
if ($Paso -eq 'instalar' -and (-not $Paquete -or -not (Test-Path -LiteralPath (Join-Path $Paquete 'actualizar.ps1')))) { Fallo "No esta el paquete ($Paquete)." }

# Se guardan los registros de las ultimas 10 instalaciones.
if ($Registro -and $Paso -eq 'instalar') {
    $carpeta = Split-Path -Parent $Registro
    Get-ChildItem -LiteralPath $carpeta -Filter 'instalador-*.log' -File -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
        Select-Object -Skip 10 | Remove-Item -Force -ErrorAction SilentlyContinue
}

try {
    switch ($Paso) {
        'instalar' { Instalar }
        'revisar' { Revisar }
        'quitar' { Quitar }
    }
} catch {
    Fallo "Algo que no se esperaba: $($_.Exception.Message)"
}
