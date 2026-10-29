<#
    Junta en un .zip los datos del piloto de la caja 2, para revisarlos fuera de la tienda (docs/pruebas-en-tienda.md, «Juntar los
    datos del piloto»). Se corre en la caja 2, con la cuenta de Windows de todos los días o como administrador (como
    administrador junta también el registro de la caja de las otras cuentas de esa PC):

        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\juntar-datos-piloto.ps1"
        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\juntar-datos-piloto.ps1" -Dias 3

    Pregunta dónde dejarlo (Enter: la memoria USB si hay una conectada; si no, el Escritorio, salvo que esté en OneDrive: entonces
    C:\ProgramData\PapeleriaPos\datos-del-piloto; nunca en OneDrive) y junta, de los días del piloto (desde el primer día que la
    caja 2 mandó algo a eleventa; con -Dias, los últimos N días con hoy; con -Desde, desde ese día):
    - el registro del servicio (servicio-AAAA-MM-DD.log: cada envío a eleventa, cada lectura, el respaldo de eleventa del día,
      los turnos del piloto), el de la caja (caja-AAAA-MM-DD.log: cada venta con sus tiempos y los escaneos de cada hora) y las
      bitácoras de red (red-servicio-…, red-caja-…);
    - una copia de la base sin secretos (sin los PIN, las llaves de las cajas ni los códigos de Telegram): si se escribe un
      usuario de la caja con «Configuración de la tienda» y su PIN, una de ese momento (se la pide al servicio, como «Respaldar
      ahora»); con Enter, del respaldo más reciente que ya hizo el servicio. El respaldo mismo no se toca;
    - en CSV, sacados de esa copia (nunca de la base en uso): la bandeja hacia eleventa y la bitácora de esos días;
    - servicio.json y caja.json de esta PC, sin claves, códigos ni llaves;
    - lo que haya anotado observar-eleventa esos días;
    - un LEEME.txt con lo que trae.
    Todo en un solo .zip con la fecha: datos-piloto-<PC>-<AAAA-MM-DD-HHmm>.zip. No cambia nada de la caja ni de eleventa; el PIN
    no se ve al escribirlo y no se guarda.

    -Destino  La carpeta donde dejar el .zip, sin preguntar (si está en OneDrive, va a C:\ProgramData\PapeleriaPos\datos-del-piloto).
    -Dias     Los últimos N días (con hoy).
    -Desde    Desde ese día (AAAA-MM-DD).
    -Usuario  El usuario para la copia de ese momento, sin preguntarlo (el PIN sí se pregunta, sin que se vea).
    -SinPin   Sin preguntar nada de la copia: el respaldo más reciente.
    -Puerto   El del servicio, si se cambió en servicio.json (Urls). Por omisión, 5080.
    -Pin      (Una cadena segura) Para correrlo desde otro script, como las pruebas; en la tienda se pregunta.

    Termina con 0 si dejó el .zip (lo que no pudo juntar lo dice como aviso, también en LEEME.txt) y con 1 si no.
    Los mensajes van sin acentos: así se leen igual en cualquier consola.
#>
param(
    [string]$Destino,
    [int]$Dias = 0,
    [string]$Desde,
    [string]$Usuario,
    [switch]$SinPin,
    [int]$Puerto = 5080,
    [securestring]$Pin
)

$ErrorActionPreference = 'Stop'
# Sin la barra de avance de Windows PowerShell en cada consulta al servicio (la hace lenta).
$ProgressPreference = 'SilentlyContinue'

$datos = Join-Path $env:ProgramData 'PapeleriaPos'
$registrosDelServicio = Join-Path $datos 'registros'
$respaldos = Join-Path $datos 'respaldos'
$servicioJson = Join-Path $datos 'servicio.json'
$cajaJson = Join-Path $datos 'Caja\caja.json'
if (-not (Test-Path -LiteralPath $cajaJson)) { $cajaJson = Join-Path $env:ProgramFiles 'PapeleriaPos\Caja\caja.json' }
# El programa del servicio saca los CSV de la copia y la configuración sin secretos: el de junto a este script, o el instalado.
$exe = Join-Path $PSScriptRoot 'PapeleriaPos.Servicio.exe'
if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $env:ProgramFiles 'PapeleriaPos\Servicio\PapeleriaPos.Servicio.exe' }
$api = "http://localhost:$Puerto/api"
$hoy = (Get-Date).Date
$invariante = [Globalization.CultureInfo]::InvariantCulture
$utf8 = New-Object System.Text.UTF8Encoding $false
$utf8ConMarca = New-Object System.Text.UTF8Encoding $true
$avisos = New-Object System.Collections.Generic.List[string]

function Aviso([string]$texto) {
    $avisos.Add($texto)
    Write-Host "  Aviso: $texto" -ForegroundColor Yellow
}

# Copia un archivo aunque otro programa lo tenga abierto (el servicio o la caja escribiendo su registro).
function Copiar-Archivo([string]$origen, [string]$destino) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destino))
    $entrada = [IO.File]::Open($origen, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try {
        $salida = [IO.File]::Create($destino)
        try { $entrada.CopyTo($salida) } finally { $salida.Dispose() }
    } finally {
        $entrada.Dispose()
    }
}

# Lo que dijo el servicio cuando no pudo (su ErrorDto), o el error de la conexión.
function Mensaje-De($registro) {
    $detalle = $registro.ErrorDetails.Message
    if ($detalle) {
        try {
            $json = $detalle | ConvertFrom-Json
            if ($json.mensaje) { return [string]$json.mensaje }
        } catch { }
    }
    [string]$registro.Exception.Message
}

# Una llamada a la API del servicio de esta PC, en JSON (desde la misma PC no hace falta la llave de una caja).
function Api([string]$ruta, $cuerpo = $null) {
    $parametros = @{ Uri = "$api/$ruta"; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($null -ne $cuerpo) {
        $parametros.Method = 'Post'
        $parametros.ContentType = 'application/json; charset=utf-8'
        $parametros.Body = [Text.Encoding]::UTF8.GetBytes(($cuerpo | ConvertTo-Json -Compress))
    }
    Invoke-RestMethod @parametros
}

function Texto-De([securestring]$seguro) {
    $puntero = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($puntero) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($puntero) }
}

# «Respaldar ahora», como en Configuración › Respaldos: la ruta del respaldo, o nada si no se pudo (y dice por qué).
function Respaldar-Ahora([string]$usuario, [securestring]$pin) {
    try {
        $cajas = @(Api 'cajas')
        if ($cajas.Count -eq 0) {
            Aviso 'El servicio no tiene cajas dadas de alta: se usa el respaldo mas reciente.'
            return $null
        }
        $sesion = Api 'sesion' @{ usuario = $usuario; pin = (Texto-De $pin) }
        $ruta = "cajas/$($cajas[0].id)/configuracion/respaldos"
        Write-Host 'Pidiendo una copia de la base al servicio (Respaldar ahora)...'
        $estado = Api $ruta @{ usuarioId = $sesion.usuarioId }
        $limite = (Get-Date).AddMinutes(10)
        while ($estado.ahora -and $estado.ahora.haciendo -and (Get-Date) -lt $limite) {
            Start-Sleep -Seconds 1
            $estado = Api "$ruta`?usuarioId=$($sesion.usuarioId)"
        }
        if ($estado.ahora -and $estado.ahora.hecho) {
            $script:deDonde = "una copia de este momento, que hizo el servicio al pedirla $usuario (Respaldar ahora)"
            return [string]$estado.ahora.hecho.ruta
        }
        if ($estado.ahora -and $estado.ahora.fallo) {
            Aviso "El servicio no pudo respaldar: $($estado.ahora.fallo) Se usa el respaldo mas reciente."
        } else {
            Aviso 'El respaldo no termino en 10 minutos: se usa el respaldo mas reciente.'
        }
    } catch {
        Aviso "No se pudo pedir la copia al servicio ($(Mensaje-De $_)): se usa el respaldo mas reciente."
    }
    $null
}

# Los registros de una carpeta de esos días (su nombre lleva la fecha): servicio-2026-10-31.log, red-caja-2026-10-31.log…
function Copiar-Registros([string]$carpeta, [string[]]$nombres, [string]$destino) {
    $copiados = 0
    if (-not (Test-Path -LiteralPath $carpeta -ErrorAction SilentlyContinue)) { return 0 }
    foreach ($archivo in @(Get-ChildItem -LiteralPath $carpeta -File -Filter '*.log' -ErrorAction SilentlyContinue)) {
        if ($archivo.Name -notmatch '^(?<nombre>[a-z-]+)-(?<dia>\d{4}-\d{2}-\d{2})\.log$') { continue }
        $nombre = $Matches['nombre']
        $dia = [datetime]::ParseExact($Matches['dia'], 'yyyy-MM-dd', $invariante)
        if ($nombres -notcontains $nombre -or $dia -lt $script:desdeFecha -or $dia -gt $hoy) { continue }
        try {
            Copiar-Archivo $archivo.FullName (Join-Path $destino $archivo.Name)
            $copiados++
        } catch {
            Aviso "No se pudo copiar $($archivo.FullName): $($_.Exception.Message)"
        }
    }
    $copiados
}

# Si el nombre de una variable es el de un secreto: termina en clave, codigo, token, password, pass, pwd, pin, llave, secreto o
# contrasena, o una de sus palabras es una de esas (menos codigo). La misma regla que el servicio (LoQueEsSecreto.PorSuNombre):
# Eleventa__Clave, ControlPrint__Codigo, Telegram__Token, ISC_PASSWORD.
function Es-Secreto([string]$nombre) {
    $minusculas = $nombre.ToLowerInvariant()
    foreach ($final in @('clave', 'codigo', 'token', 'password', 'passwd', 'pass', 'pwd', 'pin', 'llave', 'llaves', 'secreto', 'secret', 'contrasena')) {
        if ($minusculas.EndsWith($final)) { return $true }
    }
    $palabras = @([regex]::Matches($nombre, '[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+') | ForEach-Object { $_.Value.ToLowerInvariant() })
    foreach ($palabra in @('clave', 'claves', 'password', 'passwd', 'pwd', 'pass', 'contrasena', 'token', 'pin', 'secret', 'secreto', 'llave', 'llaves')) {
        if ($palabras -contains $palabra) { return $true }
    }
    $false
}

# Los secretos que tiene el servicio en sus variables de entorno (si esta cuenta las puede leer): la clave de eleventa, el codigo
# de Control Print y lo que venga, para revisar que no vayan en nada de lo que se junta. Solo los nombres, nunca se escriben.
function Secretos-Del-Servicio {
    $secretos = New-Object System.Collections.Generic.List[object]
    try {
        $variables = @((Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PapeleriaPos' -Name Environment -ErrorAction Stop).Environment)
        foreach ($variable in $variables) {
            $igual = $variable.IndexOf('=')
            if ($igual -le 0) { continue }
            $nombre = $variable.Substring(0, $igual)
            $valor = $variable.Substring($igual + 1)
            if ($valor.Length -ge 4 -and $valor -notmatch '^(true|false)$' -and (Es-Secreto $nombre)) {
                $secretos.Add([pscustomobject]@{ Nombre = $nombre; Valor = $valor })
            }
        }
    } catch { }
    ,$secretos
}

# Un secreto de 8 o mas, donde aparezca; uno mas corto, solo suelto (entre lo que no es letra ni numero), como el servicio
# (LoQueEsSecreto.TacharValor): una clave 1234 no destroza B-012345 ni 41234, y en password=1234; si se tacha.
function Tachar-Valor([string]$texto, [string]$valor) {
    if ($valor.Length -ge 8) { return $texto.Replace($valor, '***') }
    [regex]::Replace($texto, '(?<![\p{L}\p{N}])' + [regex]::Escape($valor) + '(?![\p{L}\p{N}])', '***')
}

# Fuera de OneDrive: lo que se junta son datos de la tienda y no se suben a la nube (como los respaldos de eleventa).
function En-OneDrive([string]$carpeta) {
    if (-not $carpeta) { return $false }
    if ($carpeta -match '(^|[\\/])OneDrive') { return $true }
    try {
        $completa = [IO.Path]::GetFullPath($carpeta)
        foreach ($raiz in @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial)) {
            if ($raiz -and $completa.StartsWith([IO.Path]::GetFullPath($raiz), [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    } catch { }
    $false
}

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    Write-Host 'Esta es la ventana de PowerShell de 32 bits (x86): abre Windows PowerShell (sin x86).' -ForegroundColor Red
    exit 1
}

# ── Dónde y de qué días ──

$carpetaLocal = Join-Path $datos 'datos-del-piloto'
if (-not $Destino) {
    $usb = @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=2' -ErrorAction SilentlyContinue | Where-Object { $_.Size -gt 0 } | Sort-Object DeviceID)
    $porOmision = if ($usb.Count -gt 0) { "$($usb[0].DeviceID)\" } else { [Environment]::GetFolderPath('Desktop') }
    if (En-OneDrive $porOmision) {
        Write-Host "El Escritorio de esta cuenta se sube a OneDrive: el .zip va a $carpetaLocal." -ForegroundColor Yellow
        $porOmision = $carpetaLocal
    }
    $respuesta = Read-Host "Donde dejo el .zip? (Enter: $porOmision)"
    $Destino = if ($respuesta) { $respuesta.Trim().Trim('"') } else { $porOmision }
}
if (En-OneDrive $Destino) {
    Write-Host "$Destino esta en OneDrive (se sube a internet): el .zip va a $carpetaLocal." -ForegroundColor Yellow
    $Destino = $carpetaLocal
}
try {
    [void][IO.Directory]::CreateDirectory($Destino)
} catch {
    Write-Host "No se puede usar la carpeta $Destino ($($_.Exception.Message))." -ForegroundColor Red
    exit 1
}

$script:desdeFecha = $null
if ($Desde) {
    try {
        $script:desdeFecha = [datetime]::ParseExact($Desde, 'yyyy-MM-dd', $invariante)
    } catch {
        Write-Host "-Desde va como AAAA-MM-DD (por ejemplo 2026-10-31), y llego '$Desde'." -ForegroundColor Red
        exit 1
    }
} elseif ($Dias -gt 0) {
    $script:desdeFecha = $hoy.AddDays(1 - $Dias)
}

$nombreZip = 'datos-piloto-{0}-{1:yyyy-MM-dd-HHmm}' -f $env:COMPUTERNAME, (Get-Date)
$trabajo = Join-Path ([IO.Path]::GetTempPath()) $nombreZip
# La copia tal cual (con los PIN) va aparte, fuera de lo que se mete al .zip, y se borra al final.
$aparte = "$trabajo-copia"
foreach ($carpeta in @($trabajo, $aparte)) {
    if (Test-Path -LiteralPath $carpeta) { Remove-Item -LiteralPath $carpeta -Recurse -Force }
    [void][IO.Directory]::CreateDirectory($carpeta)
}

try {
    # ── La copia de la base ──

    $script:deDonde = $null
    $respaldo = $null
    if (-not $SinPin) {
        if (-not $Usuario) {
            $Usuario = Read-Host 'Para una copia de la base de este momento, escribe tu usuario de la caja (con "Configuracion de la tienda"); con Enter se usa el respaldo mas reciente'
        }
        if ($Usuario) {
            if (-not $Pin) { $Pin = Read-Host "PIN de $Usuario (no se ve al escribirlo)" -AsSecureString }
            $respaldo = Respaldar-Ahora $Usuario.Trim() $Pin
        }
    }
    if (-not $respaldo) {
        $ultimo = @(Get-ChildItem -LiteralPath $respaldos -Filter 'pos-*.db' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($ultimo.Count -gt 0) {
            $respaldo = $ultimo[0].FullName
            $script:deDonde = 'el respaldo mas reciente que ya habia hecho el servicio (del {0:dd/MM/yyyy HH:mm}): lo de despues no esta en la copia' -f $ultimo[0].LastWriteTime
        } else {
            Aviso "No hay respaldos en $respaldos`: el .zip va sin la copia de la base ni los CSV."
        }
    }
    $copiaBase = $null
    $baseSinSecretos = $null
    if ($respaldo) {
        $copiaBase = Join-Path $aparte (Split-Path -Leaf $respaldo)
        $baseSinSecretos = Join-Path $trabajo ('base\' + (Split-Path -Leaf $respaldo))
        try {
            Copiar-Archivo $respaldo $copiaBase
            Write-Host "Copia de la base: $(Split-Path -Leaf $respaldo)"
        } catch {
            Aviso "No se pudo copiar el respaldo $respaldo ($($_.Exception.Message))."
            $copiaBase = $null
        }
    }

    # ── Los CSV de la copia, la base y la configuración sin secretos (el programa del servicio, sin arrancarlo) ──

    $csv = Join-Path $trabajo 'csv'
    $configuracion = Join-Path $trabajo 'configuracion'
    $primerDia = $null
    if (-not (Test-Path -LiteralPath $exe)) {
        Aviso "No esta el programa del servicio ($exe): el .zip va sin la base, los CSV ni la configuracion."
    } elseif ($copiaBase -or (Test-Path -LiteralPath $servicioJson) -or (Test-Path -LiteralPath $cajaJson)) {
        $argumentos = @('datos-del-piloto', '--salida', $csv, '--hasta', $hoy.ToString('yyyy-MM-dd', $invariante))
        # Sin copia, con una que no existe: el programa lo dice, y deja la configuración.
        if ($copiaBase) { $argumentos += @('--copia', $copiaBase, '--base-sin-secretos', $baseSinSecretos) } else { $argumentos += @('--copia', (Join-Path $aparte 'sin-copia.db')) }
        if ($script:desdeFecha) { $argumentos += @('--desde', $script:desdeFecha.ToString('yyyy-MM-dd', $invariante)) }
        if (Test-Path -LiteralPath $servicioJson) { $argumentos += @('--configuracion', $servicioJson) }
        if (Test-Path -LiteralPath $cajaJson) { $argumentos += @('--caja', $cajaJson) }
        $antes = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $salida = @(& $exe @argumentos 2>&1 | ForEach-Object { "$_" })
            $codigo = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $antes
        }
        foreach ($linea in $salida) { Write-Host "  $linea" }
        if ($codigo -ne 0 -and $copiaBase) { Aviso "El programa del servicio no pudo sacar todo de la copia (codigo $codigo)." }
        if ($copiaBase -and -not (Test-Path -LiteralPath $baseSinSecretos)) { Aviso 'La copia de la base no se pudo dejar sin secretos: el .zip va sin ella.' }
        foreach ($nombre in @('servicio.json', 'caja.json', 'servicio.json.txt', 'caja.json.txt')) {
            $archivo = Join-Path $csv $nombre
            if (Test-Path -LiteralPath $archivo) {
                [void][IO.Directory]::CreateDirectory($configuracion)
                Move-Item -LiteralPath $archivo -Destination (Join-Path $configuracion $nombre) -Force
            }
        }
        $periodo = Join-Path $csv 'periodo.txt'
        if (Test-Path -LiteralPath $periodo) {
            foreach ($linea in @(Get-Content -LiteralPath $periodo)) {
                if ($linea -match '^desde=(\d{4}-\d{2}-\d{2})$' -and -not $script:desdeFecha) { $script:desdeFecha = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd', $invariante) }
                if ($linea -match '^primer-dia=(\d{4}-\d{2}-\d{2})$') { $primerDia = $Matches[1] }
            }
        }
    }
    if (-not $script:desdeFecha) { $script:desdeFecha = $hoy.AddDays(-29) }
    Write-Host ('Dias: del {0:yyyy-MM-dd} al {1:yyyy-MM-dd}' -f $script:desdeFecha, $hoy)

    # ── Los registros de esos días ──

    $delServicio = Copiar-Registros $registrosDelServicio @('servicio', 'red-servicio') (Join-Path $trabajo 'registros\servicio')
    Write-Host "Registros del servicio: $delServicio"
    if ($delServicio -eq 0) { Aviso "No hay registros del servicio de esos dias en $registrosDelServicio." }
    # La caja escribe el suyo en la carpeta de la cuenta de Windows con que se usa: se buscan en todas las que se dejen leer.
    $perfiles = @(Get-ChildItem -LiteralPath (Split-Path -Parent $env:USERPROFILE) -Directory -ErrorAction SilentlyContinue)
    $deLaCaja = 0
    $observaciones = 0
    foreach ($perfil in $perfiles) {
        $deLaCaja += Copiar-Registros (Join-Path $perfil.FullName 'AppData\Local\PapeleriaPos\registros') @('caja', 'red-caja') (Join-Path $trabajo "registros\caja\$($perfil.Name)")
        foreach ($sub in @('Documents\TPV\observaciones-eleventa', 'AppData\Local\PapeleriaPos\observaciones-eleventa')) {
            $carpeta = Join-Path $perfil.FullName $sub
            if (-not (Test-Path -LiteralPath $carpeta -ErrorAction SilentlyContinue)) { continue }
            foreach ($archivo in @(Get-ChildItem -LiteralPath $carpeta -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $script:desdeFecha })) {
                try {
                    Copiar-Archivo $archivo.FullName (Join-Path $trabajo "observaciones-eleventa\$($perfil.Name)\$($archivo.Name)")
                    $observaciones++
                } catch {
                    Aviso "No se pudo copiar $($archivo.FullName): $($_.Exception.Message)"
                }
            }
        }
    }
    Write-Host "Registros de la caja: $deLaCaja"
    if ($deLaCaja -eq 0) { Aviso 'No se encontraron registros de la caja de esos dias (como administrador se buscan en todas las cuentas de Windows de esta PC).' }

    # ── Que no vaya ningún secreto del servicio en nada (no debería: el servicio los tacha; aquí se revisa otra vez) ──

    $secretos = Secretos-Del-Servicio
    if ($secretos.Count -gt 0) {
        foreach ($archivo in @(Get-ChildItem -LiteralPath $trabajo -Recurse -File | Where-Object { @('.log', '.csv', '.json', '.txt') -contains $_.Extension })) {
            $texto = [IO.File]::ReadAllText($archivo.FullName)
            $tachados = @()
            foreach ($secreto in $secretos) {
                if (-not $texto.Contains($secreto.Valor)) { continue }
                $limpio = Tachar-Valor $texto $secreto.Valor
                if ($limpio -ne $texto) {
                    $texto = $limpio
                    $tachados += $secreto.Nombre
                }
            }
            if ($tachados.Count -gt 0) {
                $codificacion = if ($archivo.Extension -eq '.csv') { $utf8ConMarca } else { $utf8 }
                [IO.File]::WriteAllText($archivo.FullName, $texto, $codificacion)
                Aviso "$($tachados -join ', ') estaba en $($archivo.Name): se tacho en la copia."
            }
        }
    }
    $secretos = $null

    # En base\ solo la copia sin secretos: lo que el programa no termino (su .tmp, o lo de SQLite junto a ella) no se lleva.
    $base = Join-Path $trabajo 'base'
    if (Test-Path -LiteralPath $base) {
        foreach ($archivo in @(Get-ChildItem -LiteralPath $base -File | Where-Object { $_.FullName -ne $baseSinSecretos })) {
            Remove-Item -LiteralPath $archivo.FullName -Force
        }
        if (@(Get-ChildItem -LiteralPath $base -Force).Count -eq 0) { Remove-Item -LiteralPath $base -Force }
    }

    # ── LEEME.txt ──

    $version = $null
    try { $version = [string](Invoke-RestMethod -Uri "$api/estado" -UseBasicParsing -TimeoutSec 10).version } catch { }
    if (-not $version -and (Test-Path -LiteralPath $exe)) { $version = ([string](Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -split '\+')[0] }
    $leeme = New-Object System.Text.StringBuilder
    [void]$leeme.AppendLine('Datos del piloto de Papelería POS (la caja 2)')
    [void]$leeme.AppendLine('=============================================')
    [void]$leeme.AppendLine(('PC: {0} · se juntaron el {1:dd/MM/yyyy} a las {1:HH:mm} con la cuenta {2} · versión del POS: {3}' -f $env:COMPUTERNAME, (Get-Date), $env:USERNAME, $(if ($version) { $version } else { 'no se supo' })))
    $deDias = if ($primerDia -and -not $Desde -and $Dias -le 0) { " (el piloto empezó el $primerDia)" } else { '' }
    [void]$leeme.AppendLine(('Días: del {0:dd/MM/yyyy} al {1:dd/MM/yyyy}{2}' -f $script:desdeFecha, $hoy, $deDias))
    [void]$leeme.AppendLine('')
    [void]$leeme.AppendLine('Qué trae:')
    if ($baseSinSecretos -and (Test-Path -LiteralPath $baseSinSecretos)) { [void]$leeme.AppendLine("- base\$(Split-Path -Leaf $baseSinSecretos): la base de la caja 2, $($script:deDonde), sin los PIN, las llaves de las cajas ni los códigos de Telegram. Se abre con cualquier visor de SQLite.") }
    if (Test-Path -LiteralPath (Join-Path $csv 'envios-a-eleventa.csv')) {
        [void]$leeme.AppendLine('- csv\envios-a-eleventa.csv: la bandeja hacia eleventa de esos días (y lo que sigue pendiente o por revisar, aunque sea de antes), con el último error de cada envío. Se abre con Excel.')
        [void]$leeme.AppendLine('- csv\bitacora.csv: la bitácora de esos días.')
    }
    if (Test-Path -LiteralPath $configuracion) { [void]$leeme.AppendLine('- configuracion\: servicio.json y caja.json de esta PC, sin claves, códigos ni llaves (dicen "(quitado)").') }
    [void]$leeme.AppendLine("- registros\servicio\: el registro del servicio (servicio-AAAA-MM-DD.log: cada envío a eleventa con lo que tardó y cómo quedó, cada lectura, el respaldo de eleventa del día, los turnos del piloto) y su bitácora de red (red-servicio-…). $delServicio archivos.")
    [void]$leeme.AppendLine("- registros\caja\<cuenta de Windows>\: el registro de la caja (caja-AAAA-MM-DD.log: cada venta con sus tiempos y el folio con que salió, los escaneos de cada hora y de cada turno, lo que falló) y su bitácora de red (red-caja-…). $deLaCaja archivos.")
    if ($observaciones -gt 0) { [void]$leeme.AppendLine("- observaciones-eleventa\: lo que anotó observar-eleventa esos días. $observaciones archivos.") }
    if ($avisos.Count -gt 0) {
        [void]$leeme.AppendLine('')
        [void]$leeme.AppendLine('Lo que no se pudo juntar:')
        foreach ($aviso in $avisos) { [void]$leeme.AppendLine("- $aviso") }
    }
    [void]$leeme.AppendLine('')
    [void]$leeme.AppendLine('Son datos reales de la tienda (las ventas, la bitácora, los usuarios y los clientes; la base va sin los PIN): guárdalo como los demás datos de la tienda, fuera de OneDrive, y no lo subas a internet ni al repositorio.')
    [IO.File]::WriteAllText((Join-Path $trabajo 'LEEME.txt'), $leeme.ToString(), $utf8ConMarca)

    # ── El .zip ──

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $Destino "$nombreZip.zip"
    if (Test-Path -LiteralPath $zip) { $zip = Join-Path $Destino ('{0}-{1:ss}.zip' -f $nombreZip, (Get-Date)) }
    [IO.Compression.ZipFile]::CreateFromDirectory($trabajo, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $tamano = (Get-Item -LiteralPath $zip).Length
    Write-Host ('Listo: {0} ({1:0.0} MB)' -f $zip, ($tamano / 1MB)) -ForegroundColor Green
    if ($avisos.Count -gt 0) { Write-Host "Con $($avisos.Count) avisos (tambien van en LEEME.txt)." -ForegroundColor Yellow }
    $codigoFinal = 0
} catch {
    Write-Host "No se pudo armar el .zip: $($_.Exception.Message)" -ForegroundColor Red
    $codigoFinal = 1
} finally {
    $Pin = $null
    foreach ($carpeta in @($trabajo, $aparte)) {
        if (Test-Path -LiteralPath $carpeta) { Remove-Item -LiteralPath $carpeta -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
exit $codigoFinal
