<#
    Lo que comparten los pasos del trabajo «mudanza-en-windows» de .github/workflows/prueba.yml: mudar la base de la tienda de una PC a otra
    con el instalador de Papeleria POS, sin preguntar (docs/instalacion.md), y revisar lo que deja cada corrida. Cada paso carga antes las
    otras dos ayudas y despues esta:

        . .github\instalacion\ayudas.ps1
        . .github\instalacion\instalador.ps1
        . .github\instalacion\mudanza.ps1

    Un solo Windows hace de las dos PC de la tienda, una despues de la otra: primero es «la caja 2» (la que tiene la base) y, ya sin el
    programa ni sus datos, «la principal». Lo unico que pasa de una a otra es el paquete de mudanza, en la carpeta «mudanza» junto al
    instalador, como en la USB. Mientras esta PC es la caja 2, la principal es un servicio suelto (el del paquete para actualizar, sin
    instalar y en otro puerto) al que se le habla por la IP de red de esta PC: por localhost seria «esta misma PC».

    La llave de la caja nunca va a un archivo nuevo, a una linea de ordenes ni al registro del paso: se abre de caja.json cada vez, como
    la abre la caja, y se queda en la memoria de ese paso.

    Corre en PowerShell 7 (pwsh), como instalador.ps1; aun asi se escribe para que Windows PowerShell 5.1 lo pueda leer (el flujo
    «Instalacion» revisa asi la sintaxis de todos los .ps1): sin ?., sin ?? y sin operador ternario.
#>

# La USB: la carpeta «mudanza» junto al instalador, donde el asistente propone dejar el paquete y donde lo busca en la otra PC.
$PaqueteDeMudanza = Join-Path (Split-Path -Parent $Instalador) 'mudanza'
# El paquete completo, de antes de traerlo (al quedar bien la principal, el instalador le quita la base al de la USB): para traerlo otra vez.
$CopiaDelPaquete = Join-Path $env:RUNNER_TEMP 'copia-del-paquete-de-mudanza'
$MarcaDeMudanza = Join-Path $Datos 'pos.db.mudada'
$MudanzaPuesta = Join-Path $Datos 'mudanza-puesta.txt'
$ServicioDeEstaPc = 'http://localhost:5080'
# La principal de mentiras: el servicio del paquete para actualizar de esta corrida (el artefacto PapeleriaPos-Actualizacion, en paquete\).
$ExeDelSuelto = Join-Path $env:GITHUB_WORKSPACE 'paquete\servicio\PapeleriaPos.Servicio.exe'
$CarpetaDelSuelto = Join-Path $env:RUNNER_TEMP 'servicio-suelto'
$PuertoDelSuelto = 5090
$ReglaDelSuelto = 'Papeleria POS - servicio suelto de la prueba'
# eleventa de mentiras: una base que no existe y, para traer, un gbak que solo tiene que estar ahi (vacio y sin version: asi el asistente
# no pregunta si es el de Firebird 2.0).
$BaseDeEleventa = 'C:\eleventa-de-mentiras\PDVDATA.FDB'
$GbakDeMentiras = Join-Path $env:RUNNER_TEMP 'firebird20-de-mentiras\bin\gbak.exe'
# Las marcas en la base (marca.py): la de la caja 2, de antes de sacarla, que tiene que llegar a la principal; y la que se le pone a la
# base ya traida, que no se puede perder si el instalador se corre otra vez.
$TextoDeLaMarca = 'de la caja 2'
$TextoDespuesDeTraer = 'despues de traer'
# Entre pasos (cada uno es otra ventana de PowerShell), lo que no es secreto: el turno, el folio, las huellas de los .json. Nunca la llave.
$NotasDeLaMudanza = Join-Path $env:RUNNER_TEMP 'notas-de-la-mudanza'

# ── Lo de siempre ──

function Anotar([string]$que, [string]$valor) {
    New-Item -ItemType Directory -Force -Path $NotasDeLaMudanza | Out-Null
    [IO.File]::WriteAllText((Join-Path $NotasDeLaMudanza "$que.txt"), $valor)
}

function Anotado([string]$que) {
    $archivo = Join-Path $NotasDeLaMudanza "$que.txt"
    if (-not (Test-Path -LiteralPath $archivo)) { throw "Falta lo que dejo anotado un paso de antes ($que): ese paso no termino." }
    [IO.File]::ReadAllText($archivo).Trim()
}

# Lo que viene separado con «;» en mudanza.txt y traida.txt (las IP, los ajustes del puente), ya sin espacios ni vacios.
function Lista([string]$texto) {
    @("$texto" -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Misma-Carpeta([string]$una, [string]$otra) {
    if (-not $una -or -not $otra) { return $false }
    try {
        [IO.Path]::GetFullPath($una).TrimEnd('\') -ieq [IO.Path]::GetFullPath($otra).TrimEnd('\')
    } catch {
        $false
    }
}

function Huella-De([string]$archivo) { (Get-FileHash -LiteralPath $archivo -Algorithm SHA256).Hash }

# El texto de un archivo que otro programa puede tener abierto para escribir (el registro del servicio).
function Leer-Compartido([string]$archivo) {
    $flujo = New-Object System.IO.FileStream($archivo, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $lector = New-Object System.IO.StreamReader($flujo, [Text.Encoding]::UTF8)
        $lector.ReadToEnd()
    } finally {
        $flujo.Dispose()
    }
}

# ── El instalador ──

# El instalador sin preguntar (Instalar, de instalador.ps1) y, para subirlas si algo falla, una copia de su resultado.txt y otra del
# registro de instalar-pc.ps1, con el nombre de esta corrida: resultado.txt es uno solo y cada corrida lo vuelve a escribir, y «Borrar
# tambien los datos» se lleva la carpeta de esos registros. El resultado de la corrida anterior se quita antes: si esta no llega a
# escribir el suyo, no se revisa el viejo como si fuera nuevo.
function Instalar-La-Mudanza([string]$nombre, [string[]]$parametros = @(), [hashtable]$secretos = @{}) {
    Remove-Item -LiteralPath $ResultadoDelInstalador -Force -ErrorAction SilentlyContinue
    $desde = Get-Date
    $corrida = Instalar $nombre $parametros $secretos
    if (Test-Path -LiteralPath $ResultadoDelInstalador) {
        Copy-Item -LiteralPath $ResultadoDelInstalador -Destination (Join-Path $RegistrosDelInstalador "$nombre-resultado.txt") -Force
    }
    $registro = Get-ChildItem -LiteralPath (Join-Path $Datos 'instalador') -Filter 'instalador-*.log' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $desde } | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($registro) { Copy-Item -LiteralPath $registro.FullName -Destination (Join-Path $RegistrosDelInstalador "$nombre-instalar-pc.log") -Force }
    $corrida
}

# Lo comun de cada corrida de la mudanza: que el instalador haya terminado (con 0, o con 10 «con avisos»: aqui eleventa es de mentiras
# y no contesta) y que su resultado.txt diga la mudanza que se esperaba. Si no, dice que hubo: su codigo, su «mudanza-mensaje» y su
# «motivo». Devuelve lo que dice resultado.txt.
function Revisar-La-Mudanza($corrida, [string]$esperada) {
    # Sin su resultado.txt ni empezo (otra copia corriendo, o no abrio): lo que se sabe es su codigo y lo que diga su registro.
    Que (Test-Path -LiteralPath $ResultadoDelInstalador) "el instalador dejo su resultado.txt (termino con $($corrida.Codigo); si falta, su registro es $($corrida.Registro))"
    $resultado = Resultado-Del-Instalador
    $hubo = "termino con $($corrida.Codigo); mudanza='$($resultado['mudanza'])'; mudanza-mensaje='$($resultado['mudanza-mensaje'])'; motivo='$($resultado['motivo'])'"
    Que ($corrida.Codigo -in @(0, 10)) "el instalador termino bien o con avisos (se esperaba 0 o 10; $hubo)"
    Que ($resultado['mudanza'] -eq $esperada) "resultado.txt dice mudanza=$esperada ($hubo)"
    $resultado
}

# Una corrida que instalar-pc.ps1 tiene que negar antes de cambiar nada: el instalador termina con 11 («no se instalo ni se actualizo:
# lo de antes se queda»), su resultado.txt no dice ninguna mudanza (asi el asistente ensena «Lo de antes sigue como estaba») y su
# «motivo» dice por que ($porque, una expresion regular). Que de verdad no cambio nada lo revisa quien la llama. Devuelve lo que dice
# resultado.txt.
function Revisar-La-Negativa($corrida, [string]$porque) {
    Que (Test-Path -LiteralPath $ResultadoDelInstalador) "el instalador dejo su resultado.txt (termino con $($corrida.Codigo); si falta, su registro es $($corrida.Registro))"
    $resultado = Resultado-Del-Instalador
    $hubo = "termino con $($corrida.Codigo); mudanza='$($resultado['mudanza'])'; motivo='$($resultado['motivo'])'"
    Que ($corrida.Codigo -eq 11) "el instalador se nego: termino con 11, sin instalar ni actualizar ($hubo)"
    Que (-not $resultado['mudanza']) "resultado.txt no dice ninguna mudanza: no se hizo (mudanza='$($resultado['mudanza'])')"
    Que ([string]$resultado['motivo'] -match $porque) "y su motivo es el que se esperaba (se buscaba '$porque'; motivo='$($resultado['motivo'])')"
    $resultado
}

# El desinstalador sin preguntar cuando lo que se espera es que se niegue (a media mudanza, con «Borrar tambien los datos»). Desinstalar
# (instalador.ps1) aqui no sirve: espera a que se quite de Programas y caracteristicas, y esta vez no se tiene que quitar. Solo se espera
# a que termine su copia de la carpeta temporal, la que hace el trabajo. Devuelve su codigo de salida y su registro (/LOG), y ensena lo
# ultimo que dijo.
function Intentar-Desinstalar([string]$nombre, [string[]]$parametros = @()) {
    New-Item -ItemType Directory -Force -Path $RegistrosDelInstalador | Out-Null
    $registro = Join-Path $RegistrosDelInstalador "$nombre.log"
    $argumentos = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (Argumento "/LOG=$registro")) + @($parametros | ForEach-Object { Argumento $_ })
    Write-Host ">>> unins000.exe $($argumentos -join ' ')" -ForegroundColor Cyan
    $proceso = Start-Process -FilePath $Desinstalador -ArgumentList $argumentos -Wait -PassThru
    $limite = (Get-Date).AddMinutes(2)
    while (@(Get-Process | Where-Object { $_.ProcessName -like '_iu*' }).Count -gt 0 -and (Get-Date) -lt $limite) { Start-Sleep -Seconds 1 }
    Write-Host "<<< termino con $($proceso.ExitCode)" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $registro) { Get-Content -LiteralPath $registro -Tail 12 | ForEach-Object { Write-Host "    $_" } }
    [pscustomobject]@{ Codigo = $proceso.ExitCode; Registro = $registro }
}

# Las respuestas con que la principal trae la base: «Caja principal», el paquete de la USB y eleventa como esta en esa PC (el servidor,
# la base y un gbak que existe). El numero de su caja en eleventa no se da: queda vacio, como lo va a dejar el dueno.
function Parametros-De-Traer {
    @('/ROL=principal', '/MUDANZA=traer', "/MUDANZAPAQUETE=$PaqueteDeMudanza", '/ELEVENTA=si', '/ELEVENTASERVIDOR=127.0.0.1',
        "/ELEVENTABASE=$BaseDeEleventa", "/ELEVENTAGBAK=$GbakDeMentiras")
}

# ── La API de un servicio: el de esta PC o el suelto ──

# Lo comun de un pedido a un servicio: directo, sin el proxy que tuviera la PC (como los scripts de instalacion/), tambien cuando se le
# habla por la IP de red de esta misma PC; con la llave de una caja en su encabezado si se da.
function Pedido([string]$url, [int]$segundos, [string]$llave = '') {
    $parametros = @{ Uri = $url; UseBasicParsing = $true; TimeoutSec = $segundos }
    if ($PSVersionTable.PSVersion.Major -ge 6) { $parametros.NoProxy = $true }
    if ($llave) { $parametros.Headers = @{ 'X-Caja-Llave' = $llave } }
    $parametros
}

function Estado-De([string]$servicio) {
    $parametros = Pedido "$servicio/api/estado" 10
    try { Invoke-RestMethod @parametros } catch { $null }
}

# Una llamada a la API, en JSON; con la llave de una caja si se da. Como Api (ayudas.ps1), pero a cualquier direccion y con cuerpos de mas
# niveles (una venta lleva sus renglones y sus cobros). Si el servicio la rechaza, dice con que estado y que contesto, y nunca lo que se
# le mando (el alta lleva el PIN) ni lo que contesta cuando sale bien (el alta contesta la llave).
function Api-En([string]$servicio, [string]$ruta, $cuerpo = $null, [string]$llave = '') {
    $parametros = Pedido "$servicio/api/$ruta" 60 $llave
    if ($null -ne $cuerpo) {
        $parametros.Method = 'Post'
        $parametros.ContentType = 'application/json; charset=utf-8'
        $parametros.Body = [Text.Encoding]::UTF8.GetBytes(($cuerpo | ConvertTo-Json -Compress -Depth 8))
    }
    try {
        Invoke-RestMethod @parametros
    } catch {
        $estado = 'sin respuesta'
        if ($_.Exception.Response) { $estado = [int]$_.Exception.Response.StatusCode }
        $dijo = $_.Exception.Message
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $dijo = $_.ErrorDetails.Message }
        Write-Host "::error::$servicio/api/$($ruta -replace '\?.*$', '') no salio ($estado): $dijo"
        throw
    }
}

# Con que estado de HTTP contesta esa direccion (200, 401…); 0 si no contesta.
function Codigo-Http([string]$url, [string]$llave = '') {
    $parametros = Pedido $url 15 $llave
    try {
        [int](Invoke-WebRequest @parametros).StatusCode
    } catch {
        if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    }
}

# ── La llave de la caja y su caja.json ──

# DPAPI de la maquina, como la caja guarda su llave en caja.json (ConfiguracionCaja.Guardar: ProtectedData, LocalMachine, sin entropia) y
# como la abre el ayudante del instalador. Se hace en otra ventana de Windows PowerShell 5.1, donde ProtectedData esta siempre a la mano
# (en PowerShell 7 depende de que se haya cargado). Lo que entra viaja en una variable de entorno de este proceso, como los secretos del
# instalador, y lo que sale se toma de su salida sin ensenarlo: la llave no pasa por una linea de ordenes, un archivo ni el registro.
function Con-Dpapi([string]$orden, [string]$entrada) {
    $antes = $ErrorActionPreference
    # Lo que escriba en la salida de errores no detiene esto: se revisa su codigo.
    $ErrorActionPreference = 'Continue'
    [Environment]::SetEnvironmentVariable('PAPELERIAPOS_DPAPI_DE_LA_PRUEBA', $entrada, 'Process')
    try {
        $todo = @(& powershell.exe -NoProfile -NonInteractive -Command $orden 2>&1)
        $codigo = $LASTEXITCODE
    } finally {
        [Environment]::SetEnvironmentVariable('PAPELERIAPOS_DPAPI_DE_LA_PRUEBA', $null, 'Process')
        $ErrorActionPreference = $antes
        $global:LASTEXITCODE = 0
    }
    $salida = @($todo | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    if ($codigo -ne 0 -or $salida.Count -ne 1 -or -not $salida[0]) {
        # Solo lo que dijo por la salida de errores: la otra es justo lo que no se ensena.
        foreach ($linea in @($todo | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })) { Write-Host "    $linea" }
        throw "DPAPI no salio en Windows PowerShell 5.1 (termino con $codigo)."
    }
    $salida[0]
}

function Proteger-Llave([string]$llave) {
    Con-Dpapi ('Add-Type -AssemblyName System.Security; [Console]::Out.Write([Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(' +
        '[Text.Encoding]::UTF8.GetBytes($env:PAPELERIAPOS_DPAPI_DE_LA_PRUEBA), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)))') $llave
}

function Abrir-Llave([string]$protegida) {
    Con-Dpapi ('Add-Type -AssemblyName System.Security; [Console]::Out.Write([Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(' +
        '[Convert]::FromBase64String($env:PAPELERIAPOS_DPAPI_DE_LA_PRUEBA), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)))') $protegida
}

# caja.json como lo deja la caja al darse de alta con el servicio de esta misma PC (ConfigurarCaja.GuardarAsync y ConfiguracionCaja.Guardar):
# sobre lo que ya tenia (la impresora y la cola que puso el instalador), la direccion del servicio en localhost, su CajaId, su llave
# cifrada para esta PC, la llave publica del servicio que la dio de alta y esa misma direccion como la de confianza. Sin BOM, como ella.
function Guardar-Alta-Como-La-Caja([string]$cajaId, [string]$llave, [string]$llaveDelServicio) {
    $actual = Leer-Json $CajaJson
    $direccion = ([uri]$ServicioDeEstaPc).AbsoluteUri
    $nueva = [ordered]@{
        Servicio = $direccion
        CajaId = $cajaId
        Impresora = $actual.Impresora
        LlaveProtegida = (Proteger-Llave $llave)
        ColaImpresion = $actual.ColaImpresion
        QuitarImpresos = $true
        Prueba = $false
        ServicioLocal = $null
        BaseInicial = $null
        CarpetaDatos = $null
        LlaveDelServicio = $llaveDelServicio
        DireccionDeConfianza = $direccion
        Red = $null
        Lector = $null
        ColasImpresion = $null
    }
    [IO.File]::WriteAllText($CajaJson, ($nueva | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
}

# La llave de la caja de esta PC, abierta de su caja.json como la abre la caja. Se queda en la memoria del paso que la pide.
function Llave-De-Esta-Caja {
    $guardada = [string](Leer-Json $CajaJson).LlaveProtegida
    Que ([bool]$guardada) 'caja.json trae la llave de la caja, cifrada para esta PC'
    Abrir-Llave $guardada
}

# El servicio.json de la caja 2 del piloto, puesto a mano con el Bloc de notas: el puente con eleventa leyendo y ESCRIBIENDO, como la
# caja 3 de eleventa, contra un servidor que no existe (nadie escucha en el puerto 1 de esta PC). El puerto y la espera no son los de
# siempre: tambien tienen que llegar a la principal. Y sigue siendo «La unica caja».
function Texto-De-ServicioJson-Con-El-Puente {
    (@(
        '// A mano, con el Bloc de notas, como la caja 2 del piloto (docs/pruebas-en-tienda.md).',
        '{',
        '  "Eleventa": {',
        '    "Activo": true,',
        '    "Escribir": true,',
        '    "Servidor": "127.0.0.1",',
        '    "Puerto": 1,',
        ('    "Base": "' + ($BaseDeEleventa -replace '\\', '\\') + '",'),
        '    "Caja": 3,',
        '    "Cajero": 1,',
        '    "SegundosMaximos": 5',
        '  },',
        '  "Instalacion": { "UnaSolaCaja": true }',
        '}'
    ) -join "`r`n") + "`r`n"
}

# ── El paquete de mudanza y lo que deja en la PC ──

function Bases-Del-Paquete { @(Get-ChildItem -LiteralPath $PaqueteDeMudanza -Filter 'pos-*.db' -File -ErrorAction SilentlyContinue) }

# El aviso que deja en la USB la PC que trajo la base (traida.txt, como lo escribe instalar-pc.ps1 al quedar bien «traer»): de que paquete
# es, que PC lo trajo, en que direccion atiende y cuando. Aqui lo deja la prueba por la principal de mentiras, que no se instala (nadie lo
# escribe por ella): sin el, «terminar» no da por traida la base aunque esa principal conteste con la misma tienda.
function Dejar-Traida-De-Mentiras([string]$idDelPaquete, [string]$ip, [int]$puerto) {
    $traida = Join-Path $PaqueteDeMudanza 'traida.txt'
    [IO.File]::WriteAllLines($traida, [string[]]@(
            '# Papeleria POS: la base de este paquete de mudanza ya se trajo a esta PC. De mentiras: lo dejo la prueba, por el servicio suelto.',
            "paquete=$idDelPaquete",
            "equipo=$env:COMPUTERNAME",
            "ips=$ip",
            "puerto=$puerto",
            "cuando=$((Get-Date).ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture))"), (New-Object System.Text.UTF8Encoding $true))
    Write-Host "    $traida (de mentiras): paquete=$idDelPaquete; ips=$ip; puerto=$puerto"
}

# El paquete que acaba de sacar el instalador: completo (su mudanza.txt y su base, con el tamano y el SHA-256 que dice) y coherente con lo
# que se dejo en esta PC al principio: la tienda, la caja 2 con su alta, su turno abierto y su venta, y el puente con eleventa
# escribiendo como la caja 3. Devuelve lo que dice mudanza.txt.
function Revisar-El-Paquete([string]$cajaId, [string]$ip) {
    $texto = Join-Path $PaqueteDeMudanza 'mudanza.txt'
    Que (Test-Path -LiteralPath $texto -PathType Leaf) "el paquete tiene su mudanza.txt ($texto)"
    $m = Leer-Claves $texto
    Write-Host ('    mudanza.txt: ' + (($m.Keys | Sort-Object | ForEach-Object { "$_=$($m[$_])" }) -join '; '))
    Que ($m['formato'] -eq '1') "formato=1 (dice '$($m['formato'])')"
    Que ($m['paquete'] -match '^[0-9a-f]{32}$') "paquete: un id de 32 cifras y letras (dice '$($m['paquete'])')"
    Que ($m['equipo'] -eq $env:COMPUTERNAME -and $m['puerto'] -eq '5080' -and $m['version'] -eq $VersionDelInstalador) "lo saco esta PC ($env:COMPUTERNAME), con el servicio $VersionDelInstalador en el 5080 (equipo='$($m['equipo'])', puerto='$($m['puerto'])', version='$($m['version'])')"
    Que ((Lista $m['ips']) -contains $ip) "ips trae la IP de red de esta PC, $ip (dice '$($m['ips'])')"
    Que ($m['hecho-utc'] -match 'Z$' -and $m['hecho-local'] -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$') "dice cuando se saco (hecho-utc='$($m['hecho-utc'])', hecho-local='$($m['hecho-local'])')"
    Que ($m['archivo'] -match '^pos-\d{4}-\d{2}-\d{2}-mudanza-\d{4}\.db$') "archivo: pos-AAAA-MM-DD-mudanza-HHmm.db (dice '$($m['archivo'])')"
    $respaldo = Join-Path $PaqueteDeMudanza ([string]$m['archivo'])
    Que (Test-Path -LiteralPath $respaldo -PathType Leaf) "la base esta en el paquete ($respaldo)"
    $bases = @(Bases-Del-Paquete)
    Que ($bases.Count -eq 1) "y es la unica base del paquete (hay $($bases.Count))"
    $bytes = (Get-Item -LiteralPath $respaldo).Length
    Que ([string]$bytes -eq $m['bytes']) "bytes es su tamano ($bytes; dice '$($m['bytes'])')"
    $sha = (Get-FileHash -LiteralPath $respaldo -Algorithm SHA256).Hash.ToLowerInvariant()
    Que ($m['sha256'] -ceq $sha) "sha256 es el de esa base, en minusculas ($sha; dice '$($m['sha256'])')"
    Que (-not (Test-Path -LiteralPath "$respaldo-wal")) 'sin un -wal al lado (restaurar.ps1 no la pondria)'
    Que ($m['tienda'] -eq 'Matriz') "tienda: Matriz (dice '$($m['tienda'])')"
    Que ($m['ventas'] -eq '1' -and $m['ultima-venta']) "con la venta que se cobro (ventas='$($m['ventas'])', ultima-venta='$($m['ultima-venta'])')"
    Que ($m['cajas'] -eq '1' -and $m['caja.1.id'] -eq $cajaId -and $m['caja.1.nombre'] -eq 'Caja 2' -and $m['caja.1.serie'] -eq 'B' -and $m['caja.1.equipo'] -eq $env:COMPUTERNAME) "con la caja de esta PC: «Caja 2», serie B (cajas='$($m['cajas'])', id='$($m['caja.1.id'])', nombre='$($m['caja.1.nombre'])', serie='$($m['caja.1.serie'])', equipo='$($m['caja.1.equipo'])')"
    Que ($m['caja.1.con-llave'] -eq 'si' -and $m['caja.1.con-turnos'] -eq 'si' -and $m['caja.1.turno-abierto'] -eq 'si') "dada de alta, con turnos y con su turno abierto (con-llave='$($m['caja.1.con-llave'])', con-turnos='$($m['caja.1.con-turnos'])', turno-abierto='$($m['caja.1.turno-abierto'])')"
    Que ($m['avisos'] -match '^[1-9]\d*$') "y avisa, al menos, de ese turno abierto (avisos='$($m['avisos'])')"
    Que ($m['eleventa'] -eq 'si' -and $m['eleventa-escribir'] -eq 'si') "el puente con eleventa viaja como estaba: prendido y escribiendo (eleventa='$($m['eleventa'])', eleventa-escribir='$($m['eleventa-escribir'])')"
    Que ($m['eleventa-servidor'] -eq '127.0.0.1' -and $m['eleventa-puerto'] -eq '1' -and $m['eleventa-base'] -eq $BaseDeEleventa) "con su servidor, su puerto y su base (servidor='$($m['eleventa-servidor'])', puerto='$($m['eleventa-puerto'])', base='$($m['eleventa-base'])')"
    Que ($m['eleventa-caja'] -eq '3' -and $m['eleventa-cajero'] -eq '1' -and $m['caja.1.eleventa-caja'] -eq '3') "la caja y el cajero de alla, y la caja de eleventa de la «Caja 2» (eleventa-caja='$($m['eleventa-caja'])', eleventa-cajero='$($m['eleventa-cajero'])', caja.1.eleventa-caja='$($m['caja.1.eleventa-caja'])')"
    Que ($m['eleventa-cajas'] -eq 'B=3') "eleventa-cajas: la serie B es la caja 3 de eleventa (dice '$($m['eleventa-cajas'])')"
    Que ((Lista $m['eleventa-ajustes']) -contains 'SegundosMaximos=5') "eleventa-ajustes trae lo que alla no era lo de siempre: SegundosMaximos=5 (dice '$($m['eleventa-ajustes'])')"
    Que ($m['eleventa-pendientes'] -eq '0' -and $m['controlprint'] -eq 'no') "sin pendientes en la bandeja y sin Control Print (eleventa-pendientes='$($m['eleventa-pendientes'])', controlprint='$($m['controlprint'])')"
    $m
}

# Si esa base tiene la marca (marca.py). Sobre una copia, con su -wal y su -shm si los tiene: leerla con SQLite no es cambiarla, pero puede
# dejarle o quitarle esos archivos, y ni la base del paquete ni la que se aparto se tocan.
function Marca-En([string]$archivoDeLaBase, [string]$texto) {
    $copia = Join-Path $env:RUNNER_TEMP ('marca-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $copia | Out-Null
    try {
        foreach ($cola in @('', '-wal', '-shm')) {
            if (Test-Path -LiteralPath "$archivoDeLaBase$cola") { Copy-Item -LiteralPath "$archivoDeLaBase$cola" -Destination (Join-Path $copia "pos.db$cola") }
        }
        & python (Join-Path $Ayudas 'marca.py') revisar (Join-Path $copia 'pos.db') $texto | ForEach-Object { Write-Host "    $_" }
        $bien = $LASTEXITCODE -eq 0
        $global:LASTEXITCODE = 0
        $bien
    } finally {
        Remove-Item -LiteralPath $copia -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Ningun secreto en esos archivos (el paquete de mudanza y lo que la mudanza deja en la PC): Sin-Secretos revisa lo demas.
function Sin-Secretos-En([string[]]$archivos, [string[]]$secretos) {
    foreach ($archivo in $archivos) {
        if (-not (Test-Path -LiteralPath $archivo -PathType Leaf)) { continue }
        $texto = [IO.File]::ReadAllText($archivo)
        foreach ($secreto in $secretos) {
            Que ($texto.IndexOf($secreto, [StringComparison]::OrdinalIgnoreCase) -lt 0) "$(Split-Path -Leaf $archivo) no trae un secreto"
        }
    }
}

# ── El servicio de esta PC, con la marca ──

# Hasta que Windows lo da por detenido y su proceso ya termino (todavia tiene abierta la base un momento despues).
function Esperar-Servicio-Detenido([int]$segundos = 60) {
    $limite = (Get-Date).AddSeconds($segundos)
    while ((Get-Date) -lt $limite) {
        $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='PapeleriaPos'"
        if ($cim -and $cim.State -eq 'Stopped' -and [int]$cim.ProcessId -eq 0) { return $true }
        Start-Sleep -Seconds 1
    }
    $false
}

# Cuantas veces dice el registro del servicio (registros\servicio-*.log) que no arranca porque su base se saco para mudarla.
function Avisos-De-La-Marca {
    $cuantos = 0
    foreach ($registro in @(Get-ChildItem -Path (Join-Path $Datos 'registros') -Filter 'servicio-*.log' -File -ErrorAction SilentlyContinue)) {
        $cuantos += [regex]::Matches((Leer-Compartido $registro.FullName), 'para mudarla a otra PC').Count
    }
    $cuantos
}

# Lo que tiene que quedar como estaba cuando algo se niega con la base ya sacada: esta PC sigue a media mudanza, como la dejo «sacar». La
# marca (la misma: por su huella) junto a la base, que no se movio, no se aparto ni se borro; el servicio registrado, detenido y
# deshabilitado, con su programa y su firewall; servicio.json y caja.json iguales byte por byte (el alta de la caja); y el paquete de la
# USB completo y sin el aviso de que ya se trajo.
function Revisar-Que-Sigue-A-Media-Mudanza([string]$huellaDeLaMarca) {
    Que ((Test-Path -LiteralPath $MarcaDeMudanza -PathType Leaf) -and (Huella-De $MarcaDeMudanza) -eq $huellaDeLaMarca) 'la marca de la mudanza sigue junto a la base, y es la misma'
    $apartadas = @(Get-ChildItem -LiteralPath $Datos -Directory -Filter 'antes-de-mudar-*' -ErrorAction SilentlyContinue)
    Que ((Test-Path -LiteralPath $Base -PathType Leaf) -and $apartadas.Count -eq 0) "la base sigue en su lugar: ni se aparto ni se borro (carpetas antes-de-mudar-…: $($apartadas.Count))"
    $servicio = Get-Service -Name PapeleriaPos -ErrorAction SilentlyContinue
    Que ($null -ne $servicio -and $servicio.Status -eq 'Stopped' -and $servicio.StartType -eq 'Disabled') "el servicio sigue registrado, detenido y deshabilitado (esta '$($servicio.Status)', '$($servicio.StartType)')"
    Que ((Test-Path -LiteralPath $ClaveDelServicio) -and (Test-Path -LiteralPath $ExeServicio -PathType Leaf) -and (Regla-Del-Firewall)) 'con su clave del registro (sus variables), su programa y su firewall'
    Que ((Test-Path -LiteralPath $ServicioJson -PathType Leaf) -and (Huella-De $ServicioJson) -eq (Anotado 'servicio-json')) 'servicio.json quedo igual, byte por byte'
    Que ((Test-Path -LiteralPath $CajaJson -PathType Leaf) -and (Huella-De $CajaJson) -eq (Anotado 'caja-json')) 'caja.json quedo igual, byte por byte: la caja sigue con su alta y apuntando a esta PC'
    $bases = @(Bases-Del-Paquete)
    Que ((Test-Path -LiteralPath (Join-Path $PaqueteDeMudanza 'mudanza.txt') -PathType Leaf) -and $bases.Count -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $PaqueteDeMudanza 'traida.txt'))) "el paquete de la USB sigue completo y sin el aviso de traida (bases: $($bases.Count))"
}

# ── La principal de mentiras: un servicio suelto ──

# La IPv4 de red de esta PC: la de una tarjeta de cable o Wi-Fi prendida, como las cuenta el servicio para saber quien es de la red de la
# tienda (RedLocal.Subredes) y el ayudante para saber cuales son de esta PC (EquipoWindows.Ips). Primero la de la tarjeta con puerta de
# enlace. Vacia si no hay ninguna.
function Ip-De-Red {
    $tipos = @('Ethernet', 'Ethernet3Megabit', 'FastEthernetT', 'FastEthernetFx', 'GigabitEthernet', 'Wireless80211')
    $todas = New-Object System.Collections.Generic.List[string]
    $conPuerta = New-Object System.Collections.Generic.List[string]
    foreach ($tarjeta in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ([string]$tarjeta.OperationalStatus -ne 'Up' -or $tipos -notcontains [string]$tarjeta.NetworkInterfaceType) { continue }
        $propiedades = $tarjeta.GetIPProperties()
        foreach ($direccion in $propiedades.UnicastAddresses) {
            $ip = $direccion.Address
            if ([string]$ip.AddressFamily -ne 'InterNetwork' -or [System.Net.IPAddress]::IsLoopback($ip) -or $ip.GetAddressBytes()[0] -eq 169) { continue }
            if ($direccion.PrefixLength -lt 1 -or $direccion.PrefixLength -gt 32) { continue }
            $todas.Add($ip.ToString())
            if (@($propiedades.GatewayAddresses).Count -gt 0) { $conPuerta.Add($ip.ToString()) }
        }
    }
    if ($conPuerta.Count -gt 0) { return $conPuerta[0] }
    if ($todas.Count -gt 0) { return $todas[0] }
    ''
}

# Detiene el servicio suelto y deja su registro con los del instalador (lo que se sube si algo falla, y lo que revisa Sin-Secretos).
function Detener-Servicio-Suelto($proceso) {
    if ($proceso -and -not $proceso.HasExited) {
        Stop-Process -Id $proceso.Id -Force -ErrorAction SilentlyContinue
        [void]$proceso.WaitForExit(15000)
    }
    netsh advfirewall firewall delete rule name="$ReglaDelSuelto" | Out-Null
    $global:LASTEXITCODE = 0
    foreach ($registro in @(Get-ChildItem -Path (Join-Path $CarpetaDelSuelto 'registros') -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        Copy-Item -LiteralPath $registro.FullName -Destination (Join-Path $RegistrosDelInstalador "servicio-suelto-$($registro.Name)") -Force
    }
}

# Arranca el servicio del paquete, suelto (sin instalarlo, en otro puerto y fuera de Archivos de programa), con una copia de esa base como
# la suya y sin leer el servicio.json de esta PC, que trae el puente con eleventa. No tiene pos.llave: hace la suya, como la principal de
# verdad. Espera a que conteste y devuelve su proceso: quien lo arranca lo detiene en ese mismo paso (Detener-Servicio-Suelto), porque
# un proceso suelto no se queda de un paso a otro.
function Arrancar-Servicio-Suelto([string]$baseDelPaquete) {
    Que (Test-Path -LiteralPath $ExeDelSuelto -PathType Leaf) "esta el servicio del paquete de esta corrida ($ExeDelSuelto)"
    Que (Test-Path -LiteralPath $baseDelPaquete -PathType Leaf) "esta la base del paquete de mudanza ($baseDelPaquete)"
    if (Test-Path -LiteralPath $CarpetaDelSuelto) { Remove-Item -LiteralPath $CarpetaDelSuelto -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $CarpetaDelSuelto, $RegistrosDelInstalador | Out-Null
    $suBase = Join-Path $CarpetaDelSuelto 'pos.db'
    Copy-Item -LiteralPath $baseDelPaquete -Destination $suBase
    # Su configuracion de la PC, aparte y sin nada. Sin nada, pero no en ceros: el servicio no arranca con un servicio.json que no se entiende.
    $suConfiguracion = Join-Path $CarpetaDelSuelto 'servicio.json'
    [IO.File]::WriteAllText($suConfiguracion, "{`r`n}`r`n", (New-Object System.Text.UTF8Encoding $false))
    # En la tienda el instalador abre el 5080 de la principal para la red local. Al suelto, que no se instala, se le abre aqui su puerto
    # igual (solo para la red local), por si el firewall de este Windows no dejara pasar lo que la misma PC le manda a su IP de red.
    netsh advfirewall firewall add rule name="$ReglaDelSuelto" dir=in action=allow protocol=TCP localport=$PuertoDelSuelto remoteip=localsubnet profile=any | Out-Null
    $global:LASTEXITCODE = 0
    $argumentos = @(
        "--Urls=http://*:$PuertoDelSuelto",
        (Argumento "--ConnectionStrings:Pos=Data Source=$suBase"),
        (Argumento "--ConfiguracionDeLaPc=$suConfiguracion"),
        '--Descubrimiento:Activo=false'
    )
    $arranque = @{
        FilePath = $ExeDelSuelto
        ArgumentList = $argumentos
        WorkingDirectory = $CarpetaDelSuelto
        PassThru = $true
        WindowStyle = 'Hidden'
        RedirectStandardOutput = (Join-Path $RegistrosDelInstalador 'servicio-suelto-salida.log')
        RedirectStandardError = (Join-Path $RegistrosDelInstalador 'servicio-suelto-errores.log')
    }
    Write-Host ">>> $(Split-Path -Leaf $ExeDelSuelto) $($argumentos -join ' ')" -ForegroundColor Cyan
    $proceso = Start-Process @arranque
    # Sin pedir antes su Handle, ExitCode sale vacio al terminar.
    $null = $proceso.Handle
    $estado = $null
    $limite = (Get-Date).AddSeconds(120)
    while (-not $estado -and -not $proceso.HasExited -and (Get-Date) -lt $limite) {
        Start-Sleep -Seconds 1
        $estado = Estado-De "http://localhost:$PuertoDelSuelto"
    }
    if (-not $estado) {
        $comoVa = if ($proceso.HasExited) { "termino con $($proceso.ExitCode)" } else { 'sigue corriendo, sin contestar' }
        Detener-Servicio-Suelto $proceso
        foreach ($registro in @(Get-ChildItem -Path $RegistrosDelInstalador -Filter 'servicio-suelto-*.log' -File -ErrorAction SilentlyContinue)) {
            Write-Host "===== $($registro.Name)"
            Get-Content -LiteralPath $registro.FullName -Tail 60 | ForEach-Object { Write-Host "    $_" }
        }
        Que $false "el servicio suelto contesta en el puerto $PuertoDelSuelto antes de 2 minutos ($comoVa)"
    }
    Write-Host "<<< el servicio suelto contesta (version $($estado.version), proceso $($proceso.Id))" -ForegroundColor Cyan
    $proceso
}
