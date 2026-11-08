#Requires -RunAsAdministrator
<#
    Restaura un respaldo de la base cuando el servicio ni siquiera arranca (la base está tan dañada que no se puede migrar)
    (docs/pruebas-en-tienda.md, «Si la base se daña»). Si el servicio arranca, se restaura desde la caja: Configuración ›
    Respaldos › «Restaurar un respaldo». Se corre como administrador en la PC del servicio (la caja principal, o la caja 2 en el
    piloto); queda junto al servicio, en C:\Program Files\PapeleriaPos\Servicio:

        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\restaurar.ps1"
        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\restaurar.ps1" -Respaldo pos-2026-10-22-z-2131.db

    Lo que hace, en orden:
    1. Enseña los respaldos, el más reciente primero, con cuándo, de qué (diario, corte Z, «Respaldar ahora» o el que hace el
       servicio al restaurar) y cuánto miden, y pregunta cuál. Están en C:\ProgramData\PapeleriaPos\respaldos (o en
       Respaldos:Carpeta de servicio.json, o en -Carpeta).
    2. Dice lo que se pierde y pide escribir SI. Antes de tocar nada revisa que quepa en el disco (la copia del respaldo y lugar para
       que el servicio migre y siga escribiendo, lo mismo que pide el servicio): si no, lo dice y no hace nada.
    3. Deshabilita el servicio y lo detiene (con calma; si no, a la fuerza): así Windows no lo vuelve a arrancar a la mitad.
    4. Mueve la base de ahora (pos.db con su -wal, su -shm y sus marcas) a C:\ProgramData\PapeleriaPos\antes-de-restaurar-<fecha>:
       nunca la borra. Ahí queda también el registro de esto (restaurar.log).
    5. Copia el respaldo junto a la base (pos.db.restaurando), deja la marca para que el servicio lo revise al arrancar
       (pos.db.danada: si pasa la revisión rápida, la quita) y el pedido que termina el servicio después de migrar (pos.db.restaurar:
       lo anota en la bitácora y deja lo pendiente del puente con eleventa como «ya se intentó», para que no se mande dos veces), y al
       final la copia pasa a ser pos.db.
    6. Lo vuelve a habilitar, lo arranca y espera a que http://localhost:5080/api/estado conteste.

    Si algo falla al mover la base o al poner el respaldo (un archivo ocupado, el disco lleno), la copia a medias se borra, la base de
    antes regresa a su lugar (se mueve, no se copia: no hace falta espacio) y el servicio queda como estaba (revisión del PR #136).

    Termina con 0 si quedó (el servicio contesta y la base pasó la revisión), con 1 si la base se quedó como estaba (se canceló, no
    hay respaldos, no cabe, una revisión, o algo falló y la base regresó a su lugar) y con 2 si ya se movió la base y algo no quedó:
    la base de antes está completa en su carpeta, y lo dice.

    -Respaldo         El nombre del respaldo (pos-AAAA-MM-DD….db) o su número en la lista: no pregunta cuál.
    -Si               No pide escribir SI (para la prueba de los scripts; en la tienda, mejor leer la advertencia).
    -Carpeta          Dónde están los respaldos, si no es la de siempre.
    -Puerto           El del servicio, si se cambió en servicio.json (Urls). Por omisión, 5080.
    -MinutosDeEspera  Lo más que se espera a que el servicio conteste (migrar una base grande). Por omisión, 5.

    Las ayudas para detener, arrancar y esperar al servicio son copias de las de actualizar.ps1 (cada script se corre solo, sin
    los demás): si se cambia una, cambiar también la otra. Los mensajes van sin acentos: así se leen igual en cualquier consola.
#>
param(
    [string]$Respaldo,
    [switch]$Si,
    [string]$Carpeta,
    [int]$Puerto = 5080,
    [int]$MinutosDeEspera = 5
)

$ErrorActionPreference = 'Stop'
# Sin la barra de avance de Windows PowerShell en cada consulta a api/estado (la hace lenta).
$ProgressPreference = 'SilentlyContinue'

$nombre = 'PapeleriaPos'
$datos = Join-Path $env:ProgramData 'PapeleriaPos'
$base = Join-Path $datos 'pos.db'
$configuracion = Join-Path $datos 'servicio.json'
$estadoUrl = "http://localhost:$Puerto/api/estado"
# La base con lo que la acompaña: se mueve junta, tal como estaba (también el pedido de restaurar que quedara de la pantalla).
$archivosDeLaBase = @('pos.db', 'pos.db-wal', 'pos.db-shm', 'pos.db-journal', 'pos.db.en-uso', 'pos.db.danada', 'pos.db.restaurar')
$segundosParaDetener = 90
$utf8 = New-Object System.Text.UTF8Encoding $false

# ── El registro: en la consola y, en cuanto existe la carpeta de la base de antes, en su restaurar.log ──

$script:bitacora = $null
$script:pendiente = New-Object System.Collections.Generic.List[string]

function Escribir([string]$texto = '', [string]$color = '') {
    if ($color) { Write-Host $texto -ForegroundColor $color } else { Write-Host $texto }
    $linea = '{0:HH:mm:ss} {1}' -f (Get-Date), $texto
    if ($script:bitacora) { [IO.File]::AppendAllText($script:bitacora, $linea + "`r`n", $utf8) } else { $script:pendiente.Add($linea) }
}

# Sale sin haber tocado la base.
function Terminar([string]$porque) {
    $script:terminado = $true
    Escribir $porque 'Red'
    Escribir 'No se cambio nada.'
    exit 1
}

function Mensaje([System.Management.Automation.ErrorRecord]$registro) {
    $ex = $registro.Exception
    while ($ex.InnerException) { $ex = $ex.InnerException }
    $ex.Message
}

# ── El servicio (copias de las ayudas de actualizar.ps1) ──

function Servicio-Instalado {
    $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nombre'"
    if (-not $cim) { return $null }
    [pscustomobject]@{ Modo = [string]$cim.StartMode; Estado = [string]$cim.State }
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
            Escribir "  No se detuvo en $segundosParaDetener s: se cierra a la fuerza." 'Yellow'
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

function Puede-Preguntar {
    if ($Host.Name -ne 'ConsoleHost') { return $false }
    try { [void][Console]::KeyAvailable; $true } catch { $false }
}

# ── Los respaldos ──

# La carpeta: -Carpeta; si no, Respaldos:Carpeta de servicio.json (admite renglones de comentario //); si no, la de siempre.
function Carpeta-De-Respaldos {
    if ($Carpeta) { return $Carpeta }
    $porOmision = Join-Path $datos 'respaldos'
    if (-not (Test-Path -LiteralPath $configuracion)) { return $porOmision }
    try {
        $texto = (Get-Content -LiteralPath $configuracion -Encoding UTF8 | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
        $json = $texto | ConvertFrom-Json
        if ($json.Respaldos -and $json.Respaldos.Carpeta) { return [string]$json.Respaldos.Carpeta }
    } catch {
        Escribir "  (No se pudo leer $configuracion ($(Mensaje $_)): se buscan en $porOmision. Si estan en otra, -Carpeta.)" 'Yellow'
    }
    $porOmision
}

# Como los nombra el servicio: pos-AAAA-MM-DD.db (el del dia), ...-z-HHmm.db (un corte Z), ...-ahora-HHmm.db («Respaldar ahora») y
# ...-restaurado-HHmm.db (el que hace al arrancar con un respaldo restaurado).
function Tipo-De([string]$archivo) {
    if ($archivo -match '^pos-\d{4}-\d{2}-\d{2}\.db$') { return 'Diario' }
    if ($archivo -match '^pos-\d{4}-\d{2}-\d{2}-z-\d{4}\.db$') { return 'CorteZ' }
    if ($archivo -match '^pos-\d{4}-\d{2}-\d{2}-ahora-\d{4}\.db$') { return 'Ahora' }
    if ($archivo -match '^pos-\d{4}-\d{2}-\d{2}-restaurado-\d{4}\.db$') { return 'Restaurado' }
    'Otro'
}

function Texto-Del-Tipo([string]$tipo) {
    switch ($tipo) { 'Diario' { 'diario' } 'CorteZ' { 'corte Z' } 'Ahora' { 'respaldar ahora' } 'Restaurado' { 'al restaurar' } default { 'otro' } }
}

# Lo que tiene que haber libre para restaurar un respaldo de $bytes: la copia, y lugar para que el servicio migre y siga escribiendo
# (la mitad del respaldo, nunca menos de 64 MB). Lo mismo que pide el servicio (RestauracionDeRespaldos.EspacioNecesario).
function Espacio-Necesario([long]$bytes) { $bytes + [math]::Max([long]64MB, [long][math]::Floor($bytes / 2)) }

# Los bytes libres en el disco de una carpeta; nada si Windows no lo dice.
function Espacio-Libre([string]$carpeta) {
    try { [long](New-Object System.IO.DriveInfo ([IO.Path]::GetPathRoot($carpeta))).AvailableFreeSpace } catch { $null }
}

function Tamano([long]$bytes) {
    if ($bytes -lt 1MB) { return ('{0:N0} KB' -f [math]::Max(1, [math]::Round($bytes / 1KB))) }
    '{0:N1} MB' -f ($bytes / 1MB)
}

function Respaldos([string]$carpeta) {
    if (-not (Test-Path -LiteralPath $carpeta)) { return @() }
    @(Get-ChildItem -LiteralPath $carpeta -Filter 'pos-*.db' -File | Where-Object { $_.Name -match '^pos-\d{4}-\d{2}-\d{2}.*\.db$' } |
            Sort-Object LastWriteTimeUtc -Descending)
}

# ══ 1. Qué respaldo ══

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    Terminar 'Esta es la ventana de PowerShell de 32 bits (x86): abre Windows PowerShell (sin x86) como administrador.'
}
if ($PSVersionTable.PSVersion.Major -lt 6) { [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy }

Escribir 'Papeleria POS: restaurar un respaldo de la base en esta PC'
$servicio = Servicio-Instalado
if (-not $servicio) { Terminar "El servicio $nombre no esta instalado en esta PC: aqui no hay base que restaurar." }
$carpetaRespaldos = Carpeta-De-Respaldos
$lista = @(Respaldos $carpetaRespaldos)
if ($lista.Count -eq 0) { Terminar "No hay respaldos en $carpetaRespaldos. Si estan en otra carpeta (una USB): restaurar.ps1 -Carpeta <carpeta>." }
$danada = Test-Path -LiteralPath (Join-Path $datos 'pos.db.danada')

Escribir "Respaldos en ${carpetaRespaldos}, el mas reciente primero:"
for ($i = 0; $i -lt $lista.Count; $i++) {
    $r = $lista[$i]
    $nota = ''
    # El de antes del apagón: el más reciente, cuando la base tiene el aviso de dañada (el servicio deja de respaldar entonces).
    if ($danada -and $i -eq 0) { $nota = '   <- el de antes del apagon' }
    Escribir ('  {0,3}. {1:dd/MM/yyyy HH:mm}  {2,-16} {3,9}  {4}{5}' -f ($i + 1), $r.LastWriteTime, (Texto-Del-Tipo (Tipo-De $r.Name)), (Tamano $r.Length), $r.Name, $nota)
}

$elegido = $null
if ($Respaldo) {
    $numero = 0
    if ([int]::TryParse($Respaldo, [ref]$numero) -and $numero -ge 1 -and $numero -le $lista.Count) { $elegido = $lista[$numero - 1] }
    else { $elegido = $lista | Where-Object { $_.Name -eq $Respaldo } | Select-Object -First 1 }
    if (-not $elegido) { Terminar "No esta el respaldo '$Respaldo' en $carpetaRespaldos." }
} else {
    if (-not (Puede-Preguntar)) { Terminar 'Sin ventana para preguntar: di cual con -Respaldo <nombre o numero>.' }
    $respuesta = Read-Host 'Numero del respaldo que se restaura (Enter: ninguno)'
    $numero = 0
    if (-not [int]::TryParse($respuesta, [ref]$numero) -or $numero -lt 1 -or $numero -gt $lista.Count) { Terminar 'No se eligio ninguno.' }
    $elegido = $lista[$numero - 1]
}
# Uno que todavia tiene su -wal al lado no termino de escribirse (una base recien armada que no se cerro bien): sin el se perderia lo
# ultimo que se le escribio (segunda revision del PR #145: asi se pone tambien la base nueva de la caja 2 del piloto).
if (Test-Path -LiteralPath "$($elegido.FullName)-wal") {
    Terminar "Junto a $($elegido.Name) esta $($elegido.Name)-wal: no termino de escribirse. Vuelve a armarlo y corre restaurar.ps1 otra vez."
}
$tipo = Tipo-De $elegido.Name
$descripcion = 'el respaldo del {0:dd/MM/yyyy} a las {0:HH:mm} ({1})' -f $elegido.LastWriteTime, (Texto-Del-Tipo $tipo)

# ══ 2. Lo que se pierde ══

Escribir ''
Escribir "Se va a restaurar $descripcion ($($elegido.Name))." 'Yellow'
Escribir 'Lo que se vendio o se cambio despues de ese respaldo se pierde: ventas, cortes, entradas y salidas, precios, inventario,' 'Yellow'
Escribir 'clientes y usuarios. La base de ahora no se borra: se mueve a una carpeta aparte. Mientras, las cajas venden sin conexion,' 'Yellow'
Escribir 'y lo que tengan por subir sube despues, al respaldo. En el piloto, eleventa no se toca y lo ya mandado no se manda otra vez.' 'Yellow'
if (-not $Si) {
    if (-not (Puede-Preguntar)) { Terminar 'Sin ventana para preguntar: para restaurarlo sin preguntar, -Si.' }
    $confirmacion = Read-Host 'Escribe SI para restaurarlo'
    if ($confirmacion -notmatch '^\s*s[ií]\s*$') { Terminar 'Se cancelo.' }
}

# Que quepa, antes de tocar nada: con el disco lleno a media copia, ni la base de ahora se podria escribir.
$necesario = Espacio-Necesario $elegido.Length
$libre = Espacio-Libre $datos
if ($null -ne $libre -and $libre -lt $necesario) {
    $porque = "No hay espacio en el disco para restaurarlo: hacen falta $(Tamano $necesario) libres (la copia del respaldo y lugar para seguir trabajando) y hay $(Tamano $libre)."
    Terminar "$porque Libera espacio (o lleva respaldos viejos a una USB) y corre restaurar.ps1 otra vez."
}

# ══ 3. a 6.: detener, apartar, poner y arrancar (en una función: si se interrumpe, el final del script lo dice) ══

$script:apartada = $false
$script:terminado = $false

function Restaurar {
    $sello = Get-Date -Format 'yyyy-MM-dd_HH.mm.ss'
    $script:carpetaAntes = Join-Path $datos "antes-de-restaurar-$sello"
    for ($n = 2; Test-Path -LiteralPath $script:carpetaAntes; $n++) { $script:carpetaAntes = Join-Path $datos "antes-de-restaurar-$sello-$n" }

    # ── 3. Detener ──
    Escribir 'Deteniendo el servicio...'
    try {
        Set-Service -Name $nombre -StartupType Disabled
        Detener-Servicio
    } catch {
        try { Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo) } catch { }
        Terminar "No se pudo detener el servicio: $(Mensaje $_)"
    }

    # ── 4. La base de ahora, a su carpeta ──
    [IO.Directory]::CreateDirectory($script:carpetaAntes) | Out-Null
    $script:bitacora = Join-Path $script:carpetaAntes 'restaurar.log'
    [IO.File]::WriteAllLines($script:bitacora, $script:pendiente, $utf8)
    Escribir "La base de ahora va a $($script:carpetaAntes)."
    try {
        foreach ($archivo in $archivosDeLaBase) {
            $origen = Join-Path $datos $archivo
            if (-not (Test-Path -LiteralPath $origen)) { continue }
            # Unos intentos: el antivirus puede tener abierto un momento el archivo.
            for ($intento = 1; ; $intento++) {
                try { Move-Item -LiteralPath $origen -Destination (Join-Path $script:carpetaAntes $archivo); break }
                catch { if ($intento -ge 5) { throw }; Start-Sleep -Seconds 1 }
            }
            $script:apartada = $true
            Escribir "  $archivo"
        }
    } catch {
        Escribir "No se pudo mover la base de ahora: $(Mensaje $_)" 'Red'
        Regresar-O-Salir
    }

    # ── 5. El respaldo como pos.db: la copia junto a la base, la marca y el pedido, y al final la copia pasa a ser la base ──
    Escribir "Copiando $($elegido.Name)..."
    try {
        $copia = "$base.restaurando"
        if (Test-Path -LiteralPath $copia) { Remove-Item -LiteralPath $copia -Force }
        Copy-Item -LiteralPath $elegido.FullName -Destination $copia
        if ((Get-Item -LiteralPath $copia).Length -ne $elegido.Length) { throw 'la copia no salio completa' }
        # Que el servicio revise la base al arrancar: si pasa la revisión rápida, quita esta marca él solo.
        [IO.File]::WriteAllText("$base.danada", "restaurar.ps1 puso $descripcion ($($elegido.Name)) el $(Get-Date -Format 'dd/MM/yyyy HH:mm'): se revisa al arrancar.", $utf8)
        # Lo que el servicio termina después de migrar: la bitácora, y lo pendiente del puente con eleventa como «ya se intentó».
        $ahora = (Get-Date).ToUniversalTime().ToString('o')
        $pedido = [ordered]@{
            id = [guid]::NewGuid().ToString(); archivo = $elegido.Name; tipo = $tipo; fechaUtc = $elegido.LastWriteTimeUtc.ToString('o')
            usuarioId = [guid]::Empty.ToString(); cajaId = [guid]::Empty.ToString(); sucursalId = [guid]::Empty.ToString()
            pedidaUtc = $ahora; reinicioAutomatico = $true; paso = 'Puesta'; carpeta = (Split-Path -Leaf $script:carpetaAntes); hechaUtc = $ahora
            fallo = $null; porElScript = $true
        }
        [IO.File]::WriteAllText("$base.restaurar", ($pedido | ConvertTo-Json), $utf8)
        # Lo último: si algo de arriba falló, la base de antes regresa sin que nada ocupe su lugar.
        Move-Item -LiteralPath $copia -Destination $base
    } catch {
        Escribir "No se pudo poner el respaldo: $(Mensaje $_)" 'Red'
        # Lo que se alcanzó a poner es de este intento (lo de antes ya está en su carpeta): se quita, y la base regresa.
        foreach ($nuestro in @("$base.restaurando", "$base.danada", "$base.restaurar")) {
            if (Test-Path -LiteralPath $nuestro) { Remove-Item -LiteralPath $nuestro -Force -ErrorAction SilentlyContinue }
        }
        Regresar-O-Salir
    }

    # ── 6. Arrancar y esperar ──
    Escribir 'Arrancando el servicio...'
    Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo)
    $arranque = Get-Date
    $fallo = Arrancar-Servicio
    if ($fallo) {
        Escribir "Windows no lo pudo arrancar: $fallo" 'Red'
        Mostrar-Eventos $arranque
        Salir-Con-La-Base-Aparte
    }
    $limite = (Get-Date).AddMinutes($MinutosDeEspera)
    $estado = $null
    while (-not $estado -and (Get-Date) -lt $limite) {
        Start-Sleep -Seconds 2
        $estado = Leer-Estado
    }
    if (-not $estado) {
        Escribir "El servicio no contesto en $MinutosDeEspera minutos ($estadoUrl)." 'Red'
        Mostrar-Eventos $arranque
        Salir-Con-La-Base-Aparte
    }
    if ($estado.avisoBase) {
        Escribir "El servicio arranco, pero el respaldo tampoco paso la revision: $($estado.avisoBase)" 'Red'
        Escribir 'Corre restaurar.ps1 otra vez y elige otro respaldo (el de antes de este).' 'Red'
        Salir-Con-La-Base-Aparte
    }

    Escribir ''
    Escribir "Se restauro $descripcion. El servicio contesta (version $($estado.version))." 'Green'
    Escribir "La base de antes quedo en $($script:carpetaAntes): no se borra (cuando ya no haga falta, se puede borrar a mano)."
    Escribir 'Las cajas bajan su copia completa y lo que tenian por subir sube a esta base. Ya se puede vender.'
    $script:terminado = $true
    exit 0
}

# No se pudo mover la base o poner el respaldo: lo que ya se movió regresa a su lugar (moverlo, no copiarlo: con el disco lleno
# también se puede) y el servicio queda como estaba; sale con 1, sin cambios. Si algo no se pudo regresar, sale como siempre que la
# base quedó aparte, con el servicio deshabilitado.
function Regresar-O-Salir {
    Escribir 'La base de antes regresa a su lugar...'
    $todo = $true
    # Primero lo que acompaña a la base y al final la base: si se corta a la mitad, la base nunca queda sin su -wal.
    foreach ($archivo in @(@($archivosDeLaBase | Where-Object { $_ -ne 'pos.db' }) + 'pos.db')) {
        $aparte = Join-Path $script:carpetaAntes $archivo
        if (-not (Test-Path -LiteralPath $aparte)) { continue }
        $lugar = Join-Path $datos $archivo
        if (Test-Path -LiteralPath $lugar) { Escribir "  No se regresa ${archivo}: ya hay uno en $datos." 'Red'; $todo = $false; continue }
        try { Move-Item -LiteralPath $aparte -Destination $lugar; Escribir "  $archivo" }
        catch { Escribir "  No se pudo regresar ${archivo}: $(Mensaje $_)" 'Red'; $todo = $false }
    }
    if (-not $todo) { Salir-Con-La-Base-Aparte -Deshabilitado }
    $script:apartada = $false
    try {
        Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo)
        if ($servicio.Estado -eq 'Running') { $fallo = Arrancar-Servicio; if ($fallo) { Escribir "  Windows no lo pudo arrancar: $fallo" 'Yellow' } }
    } catch { Escribir "  No se pudo dejar el servicio como estaba: $(Mensaje $_)" 'Yellow' }
    if ($script:bitacora) { Escribir "El registro de esto: $($script:bitacora)" }
    Terminar 'La base de antes regreso a su lugar, como estaba.'
}

# Ya se tocó la base y algo no quedó: lo de antes está completo en su carpeta. -Deshabilitado: la base quedó a medio mover (o sin
# el respaldo puesto), así que el servicio se queda detenido y deshabilitado, para que nada la abra así (al arrancar haría una vacía).
function Salir-Con-La-Base-Aparte([switch]$Deshabilitado) {
    $script:terminado = $true
    if ($script:apartada) {
        Escribir "La base de antes esta completa en $($script:carpetaAntes). Para regresarla a mano, como administrador: Stop-Service $nombre;" 'Red'
        Escribir "mover a otra carpeta lo que haya en $datos con el nombre pos.db* (no borrarlo) y mover ahi lo de esa carpeta (moverlo, no copiarlo:" 'Red'
        Escribir "en el mismo disco no ocupa lugar, y con el disco lleno tambien se puede); Set-Service $nombre -StartupType Automatic; Start-Service $nombre." 'Red'
        Escribir 'O correr restaurar.ps1 otra vez y elegir otro respaldo.' 'Red'
    }
    if ($Deshabilitado) {
        Escribir "El servicio quedo detenido y deshabilitado, para que nada abra la base a medias." 'Red'
    } else {
        try { if ((Get-Service -Name $nombre).StartType -eq 'Disabled') { Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo) } } catch { }
    }
    if ($script:bitacora) { Escribir "El registro de esto: $($script:bitacora)" }
    exit 2
}

try {
    Restaurar
} finally {
    # Ctrl+C o un error que no se esperaba: el servicio no se queda deshabilitado sin decirlo; con la base a medio mover, sí.
    if (-not $script:terminado) {
        Escribir 'Se interrumpio a la mitad.' 'Red'
        if ($script:apartada -and -not (Test-Path -LiteralPath "$base.restaurar")) {
            Escribir "La base de antes esta completa en $($script:carpetaAntes). El servicio queda deshabilitado: corre restaurar.ps1 otra vez." 'Red'
        } else {
            try { Set-Service -Name $nombre -StartupType (Modo-De-Arranque $servicio.Modo) } catch { }
        }
    }
}
