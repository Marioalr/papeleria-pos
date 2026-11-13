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
                    mide la caja y escribe servicio.json y caja.json con PapeleriaPos.Instalacion.exe (solo lo que cambio). Y lo de la
                    mudanza de la base, si se pidio (abajo).
    -Paso revisar   Reinicia el servicio si se pide (-Reiniciar: cambio servicio.json o una variable) y dice si contestan el servicio,
                    eleventa y Control Print (poner-clave-eleventa.ps1 y poner-codigo-controlprint.ps1 con -SoloRevisar); en «Otra
                    caja», si contesta la caja principal. Con la marca de la mudanza no arranca nada y dice «servicio=mudada». Al traer
                    la base de otra PC, aqui se da por terminado: deja traida.txt en el paquete y borra de alli la base
                    («mudanza-paquete=borrado»), o dice que quedo a medias («mudanza=a-medias») si el servicio no contesta con esa tienda.
    -Paso quitar    Desinstala: instalar-caja.ps1 -Quitar e instalar-servicio.ps1 -Quitar (el servicio, sus variables, el firewall, el
                    programa y el acceso del escritorio). La base, los respaldos, servicio.json y lo de la caja se quedan en
                    C:\ProgramData\PapeleriaPos, salvo con -BorrarDatos. Ni con -BorrarDatos se borra lo de eleventa que deja ahi el
                    instructivo (respaldos-eleventa, firebird20 y ensayo, y la carpeta de respaldos que diga servicio.json si esta
                    adentro), ni se sigue un enlace: de un enlace se quita el enlace. eleventa y Control Print no se tocan. Con
                    -BorrarDatos y la marca de la mudanza no quita nada (termina con 1): la caja puede tener ventas sin subir.

    La mudanza de la base de la tienda a otra PC (docs/instalacion.md) son tres corridas del instalador y una carpeta, el «paquete de
    mudanza» (por lo comun, en la USB). El asistente la pide con las respuestas «mudanza» (sacar, traer, terminar o cancelar),
    «mudanza-paquete» (la carpeta), «mudanza-encima» y «mudanza-aunque-traida». A sabiendas no deja dos servicios vivos con la misma
    base (lo que se puede saber: quien contesta en la red y lo que dice la USB de la mudanza), ni el puente con eleventa prendido sobre
    una base vacia, y una corrida que se corte a la mitad se puede repetir. Lo que se hace aqui:
      sacar     En la PC que tiene la base. Antes de tocar nada revisa que el servicio conteste con tienda (y que su nombre se pueda pasar
                al ayudante, por si despues se cancela) y que la carpeta se pueda escribir. Al final del paso instalar, ya con el programa
                y la configuracion de siempre: deshabilita y detiene el servicio (con calma: si no se detiene, no se saca) y corre
                «PapeleriaPos.Servicio.exe mudanza-sacar», que deja el paquete y, junto a la base, la marca pos.db.mudada. Con la marca el
                servicio no arranca: esta PC ya no atiende. Si no se pudo, el servicio vuelve a quedar automatico y en marcha
                (mudanza=fallo). Mientras saca deja la nota mudanza-sacando.txt: si se va la luz antes de la marca (el servicio quedo
                deshabilitado, con su base completa), la siguiente corrida lo vuelve a dejar automatico y en marcha.
      traer     En la PC nueva. Antes de tocar nada el ayudante revisa el paquete y esta PC (mudanza-revisar). Con el programa ya
                puesto: copia la base del paquete a respaldos\ (revisa su SHA-256) y la pone con restaurar.ps1 -PorMudanza, que aparta
                la que hubiera; en cuanto queda puesta deja la nota mudanza-puesta.txt (con ella, otra corrida no la trae otra vez:
                «ya-estaba»). Solo entonces escribe la configuracion que deja el puente con eleventa como estaba alla (escribir
                --mudanza): nunca sobre una base vacia. En el paso revisar, si el servicio contesta con la tienda del paquete, deja en
                el paquete el aviso traida.txt (con la direccion de esta PC) y le quita la base. De un paso al otro, lo que se trajo
                va en una nota junto a las respuestas (<respuestas>.mudanza), que solo queda si la base y su configuracion quedaron.
      terminar  Otra vez en la PC vieja, que queda como «Otra caja». Antes de actualizar: comprueba que la caja principal contesta con
                la misma tienda y si conoce la llave de esta caja (probar --esta-caja), y que la USB de la mudanza, si esta a la mano, no
                diga que ese paquete todavia no se trae (su base sigue en el paquete y no tiene traida.txt). Deja caja.json apuntando
                alla con su alta (escribir --mudada-a): desde ahi ya no se cancela ni se regresa, y si algo de lo que sigue no queda se
                dice «mudanza=fallo» con lo que falta. Quita el servicio y aparta (mueve; no copia ni borra) servicio.json, las llaves y
                la base a antes-de-mudar-<fecha>; la marca, al final, para que una corrida a medias se pueda repetir.
      cancelar  En la PC vieja, si se arrepiente. Antes de actualizar: si ninguna otra PC contesta con esa tienda (mudanza-viva, que
                tambien le pregunta a la direccion del traida.txt de este paquete) y la caja de esta PC todavia no apunta a otra PC,
                quita la marca (queda como pos.db.mudada.cancelada) y deja el servicio automatico; lo arranca el paso revisar. Si la USB
                dice que la base ya se trajo a otra PC (ese traida.txt) y esa PC no contesta, solo cancela con «mudanza-aunque-traida=si»
                (en el asistente se confirmo que alla ya se desinstalo con «Borrar tambien los datos»).
    Con la marca y sin «terminar» ni «cancelar» no se cambia nada. Lo que le dice al asistente: «mudanza» (sacada, traida, ya-estaba,
    convertida, cancelada, fallo o a-medias), «mudanza-mensaje», «mudanza-paquete» (la carpeta al sacar; «borrado» al quitarle la
    base), «mudanza-cuando» y «mudanza-avisos» (al sacar), «mudanza-equipo» (al traer: de que PC viene la base), «mudanza-alta»
    (se-conserva, otra-vez o no-habia) y «mudanza-apartada» (al terminar). Lo que dice el paso revisar manda sobre lo del paso instalar.

    Lo que pasa va a -Registro; para el asistente, «clave=valor» en -Resultado. Termina con 0 si quedo, con 1 si no (lo dice
    «motivo»; lo de antes se queda, salvo lo de la mudanza que ya se dijo: un «terminar» que ya dejo la caja apuntando a la caja
    principal dice mudanza=fallo y su motivo empieza con eso) y con 2 si actualizar.ps1 no pudo regresar solo a la version de antes (lo de
    antes esta completo en su respaldo). Una mudanza que no quedo con el programa ya instalado no es un 1: termina con 0 y lo dicen
    «mudanza» (fallo: la base se quedo donde estaba; a-medias: ya esta en esta PC y falta terminar) y «mudanza-mensaje». Los mensajes van
    sin acentos: asi se leen igual en el registro y en cualquier consola.
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
# La mudanza de la base a otra PC. La marca la deja «mudanza-sacar» junto a la base de la PC de donde sale: con ella el servicio no
# arranca. La nota de puesta queda en la PC a donde llega: la base de ese paquete ya es la suya.
$marcaDeMudanza = Join-Path $datos 'pos.db.mudada'
$notaDePuesta = Join-Path $datos 'mudanza-puesta.txt'
# Mientras «Sacar» tiene el servicio deshabilitado y todavia no hay marca: si se queda (se fue la luz), dice por que quedo deshabilitado.
# Tambien la quitan restaurar.ps1 y actualizar.ps1 cuando son ellos los que lo deshabilitan.
$notaDeSacando = Join-Path $datos 'mudanza-sacando.txt'
# Como nombra «mudanza-sacar» la base de un paquete (pos-AAAA-MM-DD-mudanza-HHmm.db): solo un nombre asi se usa para armar una ruta o
# una linea de ordenes (lo demas de un paquete viene de una USB).
$nombreDeBaseMudada = '^pos-\d{4}-\d{2}-\d{2}[A-Za-z0-9-]*\.db$'
$utf8 = New-Object System.Text.UTF8Encoding $true
# Lo que va a -Resultado. Con otro nombre: en PowerShell $Resultado y $resultado son la misma variable, y la de -Resultado es [string]
# (la lista se volvia texto vacio y se perdia la ruta del archivo). Por lo mismo, ninguna funcion usa una variable suya que se llame como
# un parametro del script ($paquete, $registro, $respuestas...): las funciones que llama verian esa en lugar del parametro.
$script:lineasDelResultado = New-Object System.Collections.Generic.List[string]
# Lo que la mudanza ya dejo hecho en esta corrida: si despues algo falla, el motivo empieza por ahi (ya no es «no se cambio nada»).
$script:loQueYaSeHizo = ''
# Al traer la base: si la del paquete ya quedo como la de esta PC (para decirlo bien si despues pasa algo que no se esperaba).
$script:basePuesta = $false
# Tras cancelar una mudanza: el servicio quedo automatico y detenido, y si este paso no queda hay que arrancarlo aqui (ver Terminar).
$script:arrancarSiNoQueda = $false
# Al terminar una mudanza: lo que cambio en caja.json al dejarla apuntando a la caja principal, para decirlo con lo demas.
$script:cambiosDeLaMudanza = ''

# ── El registro y el resultado ──

function Escribir([string]$texto) {
    Write-Host $texto
    if ($Registro) { [IO.File]::AppendAllText($Registro, ('{0:HH:mm:ss} {1}' -f (Get-Date), $texto) + "`r`n", $utf8) }
}

function Resultado([string]$clave, [string]$valor) {
    $script:lineasDelResultado.Add("$clave=$($valor -replace '[\r\n]+', ' ')")
}

# Lo que ya se habia dicho de esa clave deja de valer: se quita, para decirla otra vez distinta sin que queden las dos.
function Quitar-Resultado([string]$clave) {
    for ($i = $script:lineasDelResultado.Count - 1; $i -ge 0; $i--) {
        if ($script:lineasDelResultado[$i].StartsWith("$clave=", [StringComparison]::OrdinalIgnoreCase)) { $script:lineasDelResultado.RemoveAt($i) }
    }
}

function Terminar([int]$codigo) {
    # Tras cancelar una mudanza el servicio queda automatico y detenido, y lo arranca el paso revisar. Si este paso no quedo, el asistente
    # no corre ese: se arranca aqui, para que la PC no se quede sin atender. Deshabilitado no se toca: asi lo deja actualizar.ps1 cuando no
    # pudo regresar solo.
    if ($codigo -ne 0 -and $script:arrancarSiNoQueda) {
        try {
            $servicioAhora = Get-Service -Name $nombre -ErrorAction Stop
            if ($servicioAhora.Status -eq 'Stopped' -and $servicioAhora.StartType -ne 'Disabled') {
                Start-Service -Name $nombre -ErrorAction Stop
                Escribir 'La mudanza ya se habia cancelado: se arranco el servicio de esta PC.'
            }
        } catch {
            Escribir "  El servicio de esta PC no se pudo arrancar: $($_.Exception.Message)"
        }
    }
    if ($Resultado) {
        $carpeta = Split-Path -Parent $Resultado
        if ($carpeta) { [IO.Directory]::CreateDirectory($carpeta) | Out-Null }
        [IO.File]::WriteAllLines($Resultado, [string[]](@('# Papeleria POS: lo que hizo instalar-pc.ps1') + $script:lineasDelResultado), $utf8)
    }
    exit $codigo
}

function Fallo([string]$porque, [int]$codigo = 1) {
    if ($script:loQueYaSeHizo) { $porque = "$($script:loQueYaSeHizo) $porque" }
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

# El porque de un error, sin lo que PowerShell le pone alrededor (como en actualizar.ps1 y restaurar.ps1).
function Mensaje([System.Management.Automation.ErrorRecord]$falla) {
    $ex = $falla.Exception
    while ($ex.InnerException) { $ex = $ex.InnerException }
    $ex.Message
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

# Un programa que deja lo suyo en un archivo aparte («clave=valor», con --salida), que se lee y se borra. Lo que escribe en la pantalla
# va al registro; devuelve su codigo y sus claves. Ningun argumento va vacio: Windows PowerShell 5.1 no se lo pasaria al programa.
function Con-Salida([string]$exe, [string[]]$argumentos) {
    $salida = Join-Path ([IO.Path]::GetTempPath()) ('papeleriapos-' + [guid]::NewGuid().ToString('N') + '.txt')
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @argumentos --salida $salida 2>&1 | ForEach-Object { Escribir "   $_" }
        $codigo = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        $claves = if (Test-Path -LiteralPath $salida) { Leer-Claves $salida } else { @{} }
        [pscustomobject]@{ Codigo = $codigo; Claves = $claves }
    } finally {
        $ErrorActionPreference = $antes
        Remove-Item -LiteralPath $salida -Force -ErrorAction SilentlyContinue
    }
}

# PapeleriaPos.Instalacion.exe: deja lo suyo en un archivo aparte, que se lee y se borra.
function Ayudante([string[]]$argumentos) {
    Con-Salida $Ayudante $argumentos
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

# ══ La mudanza de la base a otra PC ══

# ── Lo que comparten sus pasos ──

# La carpeta de un paquete como se escribio: sin comillas ni la diagonal del final, salvo en la raiz de una unidad («E:\»). Con esa
# diagonal, una ruta con espacios no llega bien a la linea de ordenes del ayudante ni del servicio: Windows PowerShell 5.1 no la repite
# antes de la comilla que cierra, y se lleva lo que sigue.
function Carpeta-Normal([string]$carpeta) {
    $texto = ("$carpeta" -replace '"', '').Trim().TrimEnd('\')
    if ($texto -match '^[A-Za-z]:$') { return $texto + '\' }
    $texto
}

# Un archivo de esa carpeta. Sin Join-Path: con una unidad que ya no esta (la USB que se quito) no arma la ruta, truena.
function En-La-Carpeta([string]$carpeta, [string]$archivo) {
    try { [IO.Path]::Combine($carpeta, $archivo) } catch { $carpeta.TrimEnd('\') + '\' + $archivo }
}

# Un «clave=valor» que puede no estar (o estar en una USB que ya no esta conectada): sin claves si no se pudo leer.
function Leer-Si-Esta([string]$ruta) {
    try {
        if ($ruta -and (Test-Path -LiteralPath $ruta -PathType Leaf)) { return (Leer-Claves $ruta) }
    } catch {
        Escribir "  No se pudo leer ${ruta}: $(Mensaje $_)"
    }
    @{}
}

# La marca (JSON: paquete, tienda, cuandoUtc, carpeta, archivo, sha256 y equipo). Una que no se entiende sigue siendo marca (esta PC
# sigue a media mudanza): solo que no dice de que paquete ni de que tienda, y se dice.
function Leer-Marca {
    $leida = @{ Paquete = ''; Tienda = ''; Carpeta = ''; Archivo = '' }
    try {
        $json = [IO.File]::ReadAllText($marcaDeMudanza, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $leida.Paquete = ([string]$json.paquete).Trim()
        $leida.Tienda = ([string]$json.tienda).Trim()
        $leida.Carpeta = Carpeta-Normal ([string]$json.carpeta)
        $leida.Archivo = ([string]$json.archivo).Trim()
    } catch {
        Escribir "La marca de la mudanza ($marcaDeMudanza) no se pudo leer: $(Mensaje $_)"
    }
    if (-not $leida.Paquete -or -not $leida.Tienda) {
        Escribir "La marca de la mudanza ($marcaDeMudanza) no se entiende (no dice su paquete o su tienda): esta PC sigue a media mudanza."
    }
    $leida
}

# Donde puede estar el paquete de la mudanza de esta PC, para terminarla o cancelarla: la carpeta que dijo el asistente
# («mudanza-paquete»: donde vio el traida.txt de este paquete o, al cancelar, la «mudanza» junto al instalador; la USB pudo cambiar de
# letra desde que se saco) y la carpeta a donde se saco (la de la marca). Sin repetir y sin vacias. De lo que haya ahi solo se usa lo
# que diga el paquete de la marca: viene de una USB.
function Carpetas-De-La-Mudanza($r, $marca) {
    $carpetas = New-Object System.Collections.Generic.List[string]
    foreach ($carpeta in @((Carpeta-Normal ([string]$r['mudanza-paquete'])), [string]$marca.Carpeta)) {
        if ($carpeta -and @($carpetas | Where-Object { $_ -ieq $carpeta }).Count -eq 0) { $carpetas.Add($carpeta) }
    }
    $carpetas
}

# El aviso traida.txt de ESE paquete (lo deja en el paquete la PC que trajo la base, al quedar bien) en la primera de esas carpetas que
# lo tenga: donde esta y lo que dice. Nada si no esta en ninguna, o si es de otro paquete.
function Traida-De([string[]]$carpetas, [string]$idDelPaquete) {
    if (-not $idDelPaquete) { return $null }
    foreach ($carpeta in $carpetas) {
        if (-not $carpeta) { continue }
        $archivo = En-La-Carpeta $carpeta 'traida.txt'
        $claves = Leer-Si-Esta $archivo
        if (([string]$claves['paquete']).Trim() -eq $idDelPaquete) {
            return [pscustomobject]@{ Carpeta = $carpeta; Archivo = $archivo; Claves = $claves }
        }
    }
    $null
}

# Si en esa carpeta esta ESE paquete todavia con su base: su mudanza.txt es de ese paquete y la base que nombra sigue ahi.
function Trae-Su-Base([string]$carpeta, [string]$idDelPaquete) {
    if (-not $carpeta -or -not $idDelPaquete) { return $false }
    $delPaquete = Leer-Si-Esta (En-La-Carpeta $carpeta 'mudanza.txt')
    if (([string]$delPaquete['paquete']).Trim() -ne $idDelPaquete) { return $false }
    $archivo = ([string]$delPaquete['archivo']).Trim()
    if ($archivo -notmatch $nombreDeBaseMudada) { return $false }
    try { return [bool](Test-Path -LiteralPath (En-La-Carpeta $carpeta $archivo) -PathType Leaf) } catch { return $false }
}

# A donde le habla la caja de esta PC («Servicio» de su caja.json), si ya no es a esta misma PC; vacio si es a esta PC o no se sabe. En
# una PC con el servicio el instalador siempre la deja hablandole a esta PC (localhost): solo «Terminar» (escribir --mudada-a) la deja
# hablandole a otra. Con la marca, eso quiere decir que un «Terminar» ya paso de su paso 2: desde ahi ya no se cancela ni se regresa.
function Caja-Apunta-A-Otra-Pc {
    try {
        $archivo = Join-Path $datos 'Caja\caja.json'
        if (-not (Test-Path -LiteralPath $archivo -PathType Leaf)) { return '' }
        $servicioDeLaCaja = ([string]([IO.File]::ReadAllText($archivo, [Text.Encoding]::UTF8) | ConvertFrom-Json).Servicio).Trim()
        $direccion = $null
        if ($servicioDeLaCaja -and [Uri]::TryCreate($servicioDeLaCaja, [UriKind]::Absolute, [ref]$direccion) -and -not $direccion.IsLoopback) {
            return $servicioDeLaCaja
        }
    } catch {
        Escribir "  No se pudo leer a donde le habla la caja de esta PC (caja.json): $(Mensaje $_)"
    }
    ''
}

# El nombre de la tienda que contesta un servicio en api/estado, como va en mudanza.txt y en la marca: sus sucursales unidas con «; ».
function Tienda-De($estado) {
    @($estado.sucursales | Where-Object { $_ } | ForEach-Object { ([string]$_).Trim() }) -join '; '
}

# Los nombres de las sucursales de ese texto, recortados y unidos igual. Tambien si vienen separados con comas (asi los une «probar»
# para ensenarlos): que una coma de mas o de menos no haga pasar a la misma tienda por otra.
function Tienda-Normal([string]$texto) {
    @("$texto" -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join '; '
}

# «La misma tienda»: los mismos nombres, recortando espacios y sin distinguir mayusculas. Es una identidad debil (un nombre): sirve para
# negar, no para dar por buena a otra PC; para eso esta la llave de la caja (probar --esta-caja).
function Misma-Tienda([string]$una, [string]$otra) {
    $primera = Tienda-Normal $una
    [bool]$primera -and ($primera -ieq (Tienda-Normal $otra))
}

# La IP (o el nombre) de una direccion como «192.168.1.10», «192.168.1.10:5090» o «http://192.168.1.10:5080/».
function Ip-De([string]$direccion) {
    $texto = "$direccion".Trim() -replace '^[A-Za-z][A-Za-z0-9+.-]*://', ''
    (($texto -split '/')[0] -split ':')[0]
}

# Con .NET y no con Get-FileHash: en Windows PowerShell 5.1 esa es una funcion de un modulo de script que no se carga si el
# instalador se abrio desde PowerShell 7 (hereda su PSModulePath; asi lo corre el CI, y ahi «Traer» fallaba).
function Sha256-De([string]$archivo) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $flujo = [System.IO.File]::Open($archivo, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try { -join ($sha.ComputeHash($flujo) | ForEach-Object { $_.ToString('x2') }) }
    finally { $flujo.Dispose(); $sha.Dispose() }
}

function Ahora-Local {
    (Get-Date).ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
}

# Para el asistente, que lo pone dentro de una frase («No se trajo la base: <mensaje>.»): sin su punto final.
function Sin-Punto([string]$texto) {
    "$texto".Trim().TrimEnd('.')
}

# Si Windows todavia tiene registrado el servicio: por su nombre o por su clave del registro («sc delete» lo deja marcado para borrarse
# mientras algo lo tenga abierto, como la ventana de Servicios, y actualizar.ps1 lo tomaria por instalado).
function Servicio-Registrado {
    [bool](Get-Service -Name $nombre -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$nombre")
}

# Detiene el servicio y espera a que su proceso termine (todavia tiene abierta la base un momento despues de que Windows lo da por
# detenido). Como las ayudas de restaurar.ps1 y actualizar.ps1 (cada script lleva su copia), pero sin cerrarlo a la fuerza: para sacar la
# base tiene que haberla cerrado el mismo. Dice si quedo detenido.
function Detener-Servicio([int]$segundos) {
    $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
    if (-not $servicioAhora) { return $true }
    $limite = (Get-Date).AddSeconds($segundos)
    $idProceso = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$nombre'").ProcessId
    if ($servicioAhora.Status -ne 'Stopped') {
        try { if ($servicioAhora.Status -ne 'StopPending') { $servicioAhora.Stop() } } catch { Escribir "  (No acepto detenerse: $(Mensaje $_))" }
        try { $servicioAhora.WaitForStatus('Stopped', (New-TimeSpan -Seconds $segundos)) } catch { return $false }
    }
    while ($idProceso -gt 0 -and (Get-Process -Id $idProceso -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $limite) { return $false }
        Start-Sleep -Milliseconds 500
    }
    $true
}

# El servicio otra vez automatico y en marcha (no se saco la base). Nada si quedo; si no, por que.
function Arrancar-Otra-Vez {
    try {
        Set-Service -Name $nombre -StartupType Automatic -ErrorAction Stop
        $servicioAhora = Get-Service -Name $nombre -ErrorAction Stop
        if ($servicioAhora.Status -eq 'StopPending') {
            # Todavia se esta deteniendo: se le espera un poco mas, para poder arrancarlo.
            try { $servicioAhora.WaitForStatus('Stopped', (New-TimeSpan -Seconds 30)) } catch { }
            $servicioAhora = Get-Service -Name $nombre -ErrorAction Stop
        }
        if ($servicioAhora.Status -eq 'Stopped') {
            Start-Service -Name $nombre -ErrorAction Stop
        } elseif ($servicioAhora.Status -ne 'Running' -and $servicioAhora.Status -ne 'StartPending') {
            return "quedo en «$($servicioAhora.Status)»"
        }
    } catch {
        return (Mensaje $_)
    }
}

# ── Sacar: la base de esta PC, al paquete ──

# Por que no se puede crear esa carpeta o escribir en ella; nada si se puede. No deja rastro: lo que crea para probar, lo quita.
function No-Se-Puede-Escribir([string]$carpeta) {
    try {
        $habia = Test-Path -LiteralPath $carpeta -PathType Container
        [IO.Directory]::CreateDirectory($carpeta) | Out-Null
        $prueba = En-La-Carpeta $carpeta ('prueba-' + [guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($prueba, 'Papeleria POS: aqui se puede escribir.')
        [IO.File]::Delete($prueba)
        if (-not $habia) { try { [IO.Directory]::Delete($carpeta, $false) } catch { } }
    } catch {
        return (Mensaje $_)
    }
}

# Antes de tocar nada: solo se saca la base de una PC cuyo servicio contesta con su tienda, y a una carpeta en la que se puede escribir.
function Mudanza-Antes-De-Sacar([string]$carpeta) {
    $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
    if (-not $servicioAhora) { Fallo 'En esta PC no esta el servicio con la base de la tienda: no hay nada que mudar. No se cambio nada.' }
    $estado = if ($servicioAhora.Status -eq 'Stopped') { $null } else { Esperar-Estado 30 }
    if (-not $estado) {
        $comoSeguir = 'Revisa en Servicios (services.msc) que «Papeleria POS» este en ejecucion y corre el instalador otra vez.'
        if ($servicioAhora.StartType -eq 'Disabled') {
            $comoSeguir = 'En Servicios (services.msc), «Papeleria POS» esta deshabilitado. Si quedo asi porque una mudanza se corto a la mitad (se fue la luz), ponlo en «Automatico», inicialo y corre el instalador otra vez; si fue una actualizacion que no pudo regresar, sigue el instructivo.'
        }
        Fallo "El servicio de esta PC no contesta: no se sabe que base tiene, asi que no hay nada que mudar. $comoSeguir No se cambio nada."
    }
    if ($estado.tiendaNueva -or -not (Tienda-De $estado)) { Fallo 'La base de esta PC todavia no tiene tienda: no hay nada que mudar. No se cambio nada.' }
    # Para cancelar, el nombre de la tienda va en la linea de ordenes del ayudante (mudanza-viva), y Windows PowerShell 5.1 no sabe pasar
    # unas comillas dentro de un argumento (ni una diagonal al final de uno con espacios): «Cancelar» se negaria siempre y esta PC se
    # quedaria sin regreso. Se dice aqui, antes de detenerla.
    $laTienda = Tienda-De $estado
    if ($laTienda.Contains('"') -or $laTienda.EndsWith('\')) {
        Fallo "El nombre de la tienda ($laTienda) lleva comillas (o termina en una diagonal): con el, si despues hubiera que cancelar la mudanza, el instalador no podria revisar si la base ya esta viva en otra PC. Con ese nombre la base no se muda con el instalador: se muda a mano (el plan B de la guia). No se cambio nada."
    }
    # Con su ruta completa: una carpeta suelta iria a dar a la carpeta temporal del instalador, que se borra al cerrarlo.
    if ($carpeta -notmatch '^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)') {
        Fallo "La carpeta del paquete de mudanza va con su ruta completa (por ejemplo, E:\mudanza), y llego '$carpeta'. No se cambio nada."
    }
    $porque = No-Se-Puede-Escribir $carpeta
    if ($porque) {
        Fallo "No se puede escribir en la carpeta del paquete de mudanza ($carpeta): $porque Revisa que la USB este conectada y que no este protegida contra escritura. No se cambio nada."
    }
    Escribir "Mudanza: al final se saca la base de «$laTienda» al paquete de $carpeta."
}

# Al final del paso instalar, ya con el programa y la configuracion de siempre. El servicio queda deshabilitado y detenido ANTES de
# sacar la base (asi nada le escribe mientras se copia) y, si se saco, se queda asi: con la marca no arrancaria. Si no se saco, vuelve a
# quedar automatico y en marcha: esta PC sigue trabajando como antes, y se dice (mudanza=fallo; lo demas de la instalacion ya quedo).
function Mudanza-Sacar([string]$carpeta) {
    Escribir "Mudanza: sacar la base de esta PC al paquete de $carpeta. Se detiene el servicio."
    $sacada = $false
    $resuelto = $false
    $porque = ''
    $claves = @{}
    try {
        try {
            $exe = Exe-Del-Servicio
            # 1. Deshabilitado (para que Windows no lo vuelva a arrancar a la mitad) y detenido, con calma: si no se detiene, no se saca.
            #    Antes, la nota de que se esta sacando: si se va la luz antes de la marca, la siguiente corrida sabe por que quedo
            #    deshabilitado y lo regresa (Si-Se-Corto-Al-Sacar).
            Dejar-Nota-De-Sacando
            Set-Service -Name $nombre -StartupType Disabled -ErrorAction Stop
            if (-not (Detener-Servicio 60)) {
                $porque = 'el servicio de esta PC no se detuvo en 60 segundos: no se saco la base'
            } elseif (-not $exe -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                $porque = 'no se encontro el programa del servicio de esta PC: no se saco la base'
            } else {
                # 2. El mismo programa del servicio, sin arrancarlo: hace el respaldo, arma el paquete y deja la marca.
                Escribir ">> $(Split-Path -Leaf $exe) mudanza-sacar --carpeta $carpeta"
                $corrida = Con-Salida $exe @('mudanza-sacar', '--carpeta', $carpeta)
                Escribir "<< termino con $($corrida.Codigo)"
                $claves = $corrida.Claves
                # Con 0 solo si quedaron el paquete completo y la marca: sin la marca no se da por sacada, diga lo que diga.
                if ($corrida.Codigo -eq 0 -and $claves['bien'] -ne 'no' -and (Test-Path -LiteralPath $marcaDeMudanza)) {
                    $sacada = $true
                } else {
                    $porque = [string]$claves['error']
                    if (-not $porque) { $porque = "mudanza-sacar termino con $($corrida.Codigo) sin decir por que" }
                }
            }
        } catch {
            $porque = "algo que no se esperaba al sacar la base: $(Mensaje $_)"
        }
        if ($sacada) {
            # 3. Quedo sacada. El servicio se queda deshabilitado.
            $resuelto = $true
            $dondeQuedo = [string]$claves['carpeta']
            if (-not $dondeQuedo) { $dondeQuedo = $carpeta }
            $avisos = New-Object System.Collections.Generic.List[string]
            $cuantos = 0
            [void][int]::TryParse([string]$claves['avisos'], [ref]$cuantos)
            for ($i = 1; $i -le $cuantos; $i++) {
                if ($claves["aviso.$i"]) { $avisos.Add(([string]$claves["aviso.$i"]).Trim()) }
            }
            Resultado 'mudanza' 'sacada'
            Resultado 'mudanza-paquete' $dondeQuedo
            Resultado 'mudanza-cuando' ([string]$claves['hecho-local'])
            Resultado 'mudanza-avisos' ($avisos -join ' | ')
            Escribir "Mudanza: la base de esta PC ya esta en el paquete de $dondeQuedo. Su servicio queda detenido y deshabilitado a proposito."
            foreach ($aviso in $avisos) { Escribir "  Aviso: $aviso" }
            return
        }
        # 4. No se saco. Si aun asi quedo la marca, el servicio no arrancaria: se deja como esta y se dice como salir. Pasa en los dos
        #    casos raros que dice el error de mudanza-sacar (no se pudo comprobar que el paquete quedo sin terminar, o la marca no se dejo
        #    borrar): la marca va antes que mudanza.txt (nunca hay un paquete completo sin ella), asi que una marca con el paquete sin
        #    terminar es lo que deja un corte, y se resuelve cancelando.
        if (Test-Path -LiteralPath $marcaDeMudanza) {
            $porque = "$(Sin-Punto $porque). Quedo la marca de la mudanza junto a la base, y con ella el servicio no arranca: corre el instalador otra vez y elige «Cancelar la mudanza»"
        } else {
            $noArranco = Arrancar-Otra-Vez
            $resuelto = $true
            if ($noArranco) {
                $porque = "$(Sin-Punto $porque). Ademas, el servicio de esta PC no volvio a arrancar ($(Sin-Punto $noArranco)): revisa en Servicios (services.msc) que «Papeleria POS» este en ejecucion"
            } else {
                Escribir '  El servicio de esta PC volvio a quedar automatico y en marcha.'
            }
        }
        Escribir "Mudanza: NO se saco la base. $porque"
        Resultado 'mudanza' 'fallo'
        Resultado 'mudanza-mensaje' (Sin-Punto $porque)
    } finally {
        # Si esto se corta a la mitad (como en restaurar.ps1 y actualizar.ps1): sin la marca, el servicio no se queda deshabilitado.
        if (-not $resuelto -and -not (Test-Path -LiteralPath $marcaDeMudanza)) { [void](Arrancar-Otra-Vez) }
        Quitar-Nota-De-Sacando
    }
}

# La nota de que se esta sacando la base: el servicio ya va a quedar deshabilitado y todavia no hay marca. Si no se puede dejar, se sigue:
# solo se pierde que, si se va la luz justo ahora, la siguiente corrida lo regrese sola.
function Dejar-Nota-De-Sacando {
    try {
        [IO.File]::WriteAllLines($notaDeSacando, [string[]]@(
                '# Papeleria POS: se esta sacando la base de esta PC para mudarla (instalar-pc.ps1). Si esto se queda, esa corrida se corto con el servicio deshabilitado y sin la marca: la siguiente lo regresa.',
                "cuando=$(Ahora-Local)"), $utf8)
    } catch {
        Escribir "  No se pudo dejar la nota ${notaDeSacando}: $(Mensaje $_)"
    }
}

# Se quita en cuanto se sabe como quedo: con la marca (la base se saco), sin el servicio, o con el servicio otra vez habilitado. Con el
# servicio todavia deshabilitado y sin la marca se queda: la siguiente corrida lo intenta otra vez.
function Quitar-Nota-De-Sacando {
    if (-not (Test-Path -LiteralPath $notaDeSacando)) { return }
    $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $marcaDeMudanza) -or -not $servicioAhora -or $servicioAhora.StartType -ne 'Disabled') {
        Remove-Item -LiteralPath $notaDeSacando -Force -ErrorAction SilentlyContinue
    }
}

# Al empezar el paso instalar: si quedo la nota de que se estaba sacando la base, esa corrida se corto (se fue la luz, o se cerro) entre
# deshabilitar el servicio y dejar la marca. Sin la marca la base no se saco: sigue completa aqui (mudanza-sacar no la cambia), y el
# servicio quedo deshabilitado. Vuelve a quedar como antes de sacar, automatico y en marcha: lo que antes se hacia a mano en Servicios.
# Sin la base en su lugar no se arranca (no es lo que deja un corte al sacar). Con la marca la base si se saco: eso se termina o se
# cancela, y la nota ya no dice nada.
function Si-Se-Corto-Al-Sacar {
    if (-not (Test-Path -LiteralPath $notaDeSacando)) { return }
    $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $marcaDeMudanza) -and $servicioAhora -and $servicioAhora.StartType -eq 'Disabled' -and (Test-Path -LiteralPath (Join-Path $datos 'pos.db') -PathType Leaf)) {
        Escribir 'Una corrida anterior se corto mientras sacaba la base para la mudanza (se fue la luz, o se cerro): el servicio de esta PC quedo deshabilitado y sin la marca, con su base completa aqui. Vuelve a quedar automatico y en marcha, como antes de sacarla.'
        $noArranco = Arrancar-Otra-Vez
        if ($noArranco) { Escribir "  El servicio de esta PC no volvio a arrancar: $(Sin-Punto $noArranco)." }
    }
    Quitar-Nota-De-Sacando
}

# ── Traer: la base del paquete, a esta PC ──

function No-Se-Trajo([string]$como, [string]$porque) {
    Escribir "Mudanza: $como. $porque"
    Resultado 'mudanza' $como
    Resultado 'mudanza-mensaje' (Sin-Punto $porque)
    $como
}

# La nota de que la base de ese paquete ya es la de esta PC: con ella, otra corrida con el mismo paquete no la trae otra vez.
function Dejar-Nota-De-Puesta($resumen) {
    try {
        [IO.File]::WriteAllLines($notaDePuesta, [string[]]@(
                '# Papeleria POS: la base de este paquete de mudanza ya se puso en esta PC (instalar-pc.ps1)',
                "paquete=$(([string]$resumen['paquete']).Trim())",
                "equipo=$(([string]$resumen['equipo']).Trim())",
                "archivo=$(([string]$resumen['archivo']).Trim())",
                "sha256=$(([string]$resumen['sha256']).Trim())",
                "cuando=$(Ahora-Local)"), $utf8)
        Escribir "  Quedo la nota $notaDePuesta."
    } catch {
        Escribir "  No se pudo dejar la nota ${notaDePuesta}: $(Mensaje $_)"
    }
}

# Por que restaurar.ps1 no cambio nada: lo que dice justo antes de su «No se cambio nada.» y, si la base de antes tuvo que regresar a su
# lugar, por que no se pudo poner la otra. (Sus ultimos renglones a secas traen parte de la advertencia de siempre, que aqui no viene al caso.)
function Por-Que-No-Restauro($corrida) {
    $lineas = @($corrida.Lineas | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $partes = @()
    $causa = @($lineas | Where-Object { $_ -match '^No se pudo (poner el respaldo|mover la base de ahora)' } | Select-Object -First 1)
    if ($causa.Count -gt 0) { $partes += (Sin-Punto ($causa[0])) }
    for ($i = $lineas.Count - 1; $i -ge 1; $i--) {
        if ($lineas[$i] -match '^No se cambio nada') { $partes += (Sin-Punto ($lineas[$i - 1])); break }
    }
    if ($partes.Count -eq 0) { return (Lo-Que-Dijo $corrida) }
    # Aqui lo que se repite es el instalador, no restaurar.ps1.
    (($partes -join '. ') -replace 'corre restaurar\.ps1 otra vez', 'corre el instalador otra vez') + ': no se trajo la base'
}

# Del paso instalar al paso revisar (otra corrida de este script, con las mismas respuestas): la base que se trajo, ya con su
# configuracion. Va junto a las respuestas, en la carpeta temporal del asistente.
function Nota-Por-Cerrar {
    "$Respuestas.mudanza"
}

# Con el programa ya puesto y ANTES de escribir la configuracion. Dice como quedo: «traida» o «ya-estaba» (la base del paquete es la de
# esta PC: se puede escribir la configuracion de la mudanza), o «fallo» (la base se quedo como estaba) o «a-medias» (ya esta puesta y
# algo no quedo), que ya van dichos al asistente con su mensaje. Con esos dos NO se escribe la configuracion de la mudanza: el puente
# con eleventa no se prende sobre una base que no es la del paquete.
function Mudanza-Traer([string]$carpeta, $resumen, [bool]$yaEstaba, [bool]$habiaServicio, [bool]$encima) {
    $archivo = ([string]$resumen['archivo']).Trim()
    $equipo = ([string]$resumen['equipo']).Trim()
    $tienda = ([string]$resumen['tienda']).Trim()
    $sha = ([string]$resumen['sha256']).Trim().ToLowerInvariant()
    Resultado 'mudanza-equipo' $equipo
    Escribir "Mudanza: traer la base de $equipo («$tienda»), del paquete de $carpeta."

    # 1. Ya estaba (la nota mudanza-puesta.txt es de este paquete, y lo que contesto el servicio no la contradice): no se trae otra vez.
    if ($yaEstaba -and $habiaServicio) {
        Escribir '  La base de ese paquete ya estaba puesta en esta PC: no se trae otra vez.'
        $script:basePuesta = $true
        return 'ya-estaba'
    }

    # 2. El servicio de esta PC tiene que contestar antes de cambiarle la base (recien instalado tarda).
    $estado = Esperar-Estado 120
    if ($yaEstaba) {
        # La nota dice que ya estaba, pero el servicio se acaba de instalar y nadie le habia preguntado que base tiene: ahora si.
        if (-not $estado) { return (No-Se-Trajo 'a-medias' "el servicio de esta PC no contesto: no se pudo comprobar que la base de $equipo siga puesta") }
        if (-not $estado.tiendaNueva) {
            if (Misma-Tienda (Tienda-De $estado) $tienda) {
                Escribir '  La base de ese paquete ya estaba puesta en esta PC (el servicio contesta con su tienda): no se trae otra vez.'
                $script:basePuesta = $true
                return 'ya-estaba'
            }
            return (No-Se-Trajo 'fallo' "esta PC tiene otra tienda ($(Tienda-De $estado)), no la del paquete: no se trajo la base de $equipo")
        }
        Escribir '  La nota decia que esa base ya estaba puesta, pero la de esta PC esta vacia: se trae.'
    } else {
        if (-not $estado) { return (No-Se-Trajo 'fallo' 'el servicio de esta PC no contesto: no se trajo la base') }
        # Lo reviso el ayudante antes de tocar nada; aqui otra vez, por si cambio mientras: la tienda de una PC que ya trabajaba solo
        # se aparta si se confirmo (una base que quedo de antes en una PC sin servicio se aparta sola: nunca se borra).
        if ($habiaServicio -and -not $estado.tiendaNueva -and -not $encima) {
            return (No-Se-Trajo 'fallo' "esta PC ya tiene una tienda ($(Tienda-De $estado)) y no se confirmo apartarla: no se trajo la base de $equipo")
        }
    }

    # 3. La base del paquete, a los respaldos de esta PC (ahi se queda, como un respaldo mas), revisando que llego igual.
    $respaldos = Join-Path $datos 'respaldos'
    $destino = Join-Path $respaldos $archivo
    try {
        [IO.Directory]::CreateDirectory($respaldos) | Out-Null
        if ((Test-Path -LiteralPath $destino -PathType Leaf) -and (Sha256-De $destino) -eq $sha) {
            Escribir "  $archivo ya estaba en $respaldos, con el mismo SHA-256: no se copia otra vez."
        } else {
            $copia = "$destino.copiando"
            Escribir "  Copiando $archivo a $respaldos..."
            Copy-Item -LiteralPath (En-La-Carpeta $carpeta $archivo) -Destination $copia -Force -ErrorAction Stop
            # Que no quede de solo lectura (como venga de la USB): de ahi sale la base de esta PC.
            Set-ItemProperty -LiteralPath $copia -Name IsReadOnly -Value $false -ErrorAction Stop
            if ((Sha256-De $copia) -ne $sha) { throw 'la copia no salio igual a la del paquete (su SHA-256 no cuadra): revisa la USB' }
            if (Test-Path -LiteralPath $destino) { Remove-Item -LiteralPath $destino -Force -ErrorAction Stop }
            Move-Item -LiteralPath $copia -Destination $destino -ErrorAction Stop
        }
    } catch {
        $porque = Mensaje $_
        Remove-Item -LiteralPath "$destino.copiando" -Force -ErrorAction SilentlyContinue
        return (No-Se-Trajo 'fallo' "no se pudo copiar la base del paquete a esta PC ($(Sin-Punto $porque)): no se trajo la base")
    }

    # 4. restaurar.ps1 la pone: detiene el servicio, aparta la base que haya (nunca la borra), pone esta y espera a que conteste.
    $deQuien = $equipo -replace '[^A-Za-z0-9._-]', ''
    if (-not $deQuien) { $deQuien = 'otra-pc' }
    $corrida = Correr (Join-Path $Paquete 'servicio\restaurar.ps1') @('-Carpeta', $respaldos, '-Respaldo', $archivo, '-Si', '-PorMudanza', $deQuien, '-MinutosDeEspera', '15')
    if ($corrida.Codigo -eq 0) {
        Dejar-Nota-De-Puesta $resumen
        $script:basePuesta = $true
        Escribir "  La base de $equipo ya es la de esta PC."
        return 'traida'
    }
    # Con 2, restaurar.ps1 deja el servicio deshabilitado solo si la base quedo a medio mover (ni la de antes ni la del paquete en su
    # lugar); si la del paquete ya quedo puesta y lo que fallo fue el arranque, lo deja habilitado.
    $deshabilitado = $false
    try { $deshabilitado = (Get-Service -Name $nombre -ErrorAction Stop).StartType -eq 'Disabled' } catch { }
    if ($corrida.Codigo -eq 2 -and -not $deshabilitado) {
        Dejar-Nota-De-Puesta $resumen
        $script:basePuesta = $true
        $detalle = @($corrida.Lineas | Where-Object { $_ -match '^\s*(Windows no lo pudo arrancar|El servicio no contesto|El servicio arranco, pero)' } | Select-Object -First 1)
        $porque = "la base de $equipo ya quedo puesta en esta PC, pero su servicio no quedo contestando bien"
        if ($detalle.Count -gt 0) { $porque = "$porque ($(Sin-Punto $detalle[0]))" }
        return (No-Se-Trajo 'a-medias' $porque)
    }
    # Con 1 y diciendo «No se cambio nada», la base de esta PC se quedo como estaba (o regreso a su lugar).
    if ($corrida.Codigo -eq 1 -and @($corrida.Lineas | Where-Object { $_ -match 'No se cambio nada' }).Count -gt 0) {
        return (No-Se-Trajo 'fallo' (Por-Que-No-Restauro $corrida))
    }
    # Lo demas (se interrumpio, o la base quedo a medio mover): no se da por puesta ni se deja la nota. Lo de antes esta completo en
    # su carpeta antes-de-restaurar-…, y lo dice.
    No-Se-Trajo 'fallo' "restaurar.ps1 no termino de poner la base (termino con $($corrida.Codigo)) y la de esta PC pudo quedar a medio mover: $(Lo-Que-Dijo $corrida 6)"
}

# En el paso revisar, lo ultimo de «Traer»: ya con la configuracion de la mudanza escrita y el servicio reiniciado. Si el servicio
# contesta con la tienda del paquete, se deja en el paquete el aviso traida.txt (con la direccion de esta PC: la USB se lo lleva a la PC
# de donde salio la base, para terminar alla) y se le quita la base, que ya quedo aqui como respaldo. Si no, la mudanza quedo a medias.
function Mudanza-Cerrar-La-Traida($estado) {
    $archivoDeLaNota = Nota-Por-Cerrar
    $nota = Leer-Si-Esta $archivoDeLaNota
    Remove-Item -LiteralPath $archivoDeLaNota -Force -ErrorAction SilentlyContinue
    $idDelPaquete = ([string]$nota['paquete']).Trim()
    # Sin la nota, el paso instalar no dejo la base puesta con su configuracion (ya lo dijo): aqui no hay nada que dar por terminado.
    if (-not $idDelPaquete) { return }
    $equipo = [string]$nota['equipo']
    $tienda = [string]$nota['tienda']
    $carpeta = [string]$nota['carpeta']
    $queContesta = if ($estado) { Tienda-De $estado } else { '' }
    if (-not $estado) {
        $porque = "la base de $equipo ya esta en esta PC, pero su servicio no contesto"
    } elseif ($estado.tiendaNueva) {
        $porque = "el servicio de esta PC contesta sin tienda: la base de $equipo no quedo puesta"
    } elseif (-not (Misma-Tienda $queContesta $tienda)) {
        $porque = "el servicio de esta PC contesta con otra tienda ($queContesta), no con la del paquete ($tienda)"
    } elseif ($estado.avisoBase) {
        # Como restaurar.ps1 y actualizar.ps1: una base que no paso la revision del servicio no se da por buena. La del paquete se queda.
        $porque = "el servicio de esta PC contesta con la tienda del paquete, pero avisa que la base no paso su revision ($(Sin-Punto ([string]$estado.avisoBase))): no se da por traida, y el paquete se queda con su base"
    } else {
        $porque = ''
    }
    if ($porque) {
        Escribir "Mudanza: a-medias. $porque"
        Resultado 'mudanza' 'a-medias'
        Resultado 'mudanza-mensaje' $porque
        return
    }
    # Las IP de esta PC, como las cuenta el ayudante (las de la red de la tienda).
    $leido = Ayudante @('leer')
    $ips = @(([string]$leido.Claves['ips']) -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $traida = En-La-Carpeta $carpeta 'traida.txt'
    try {
        [IO.File]::WriteAllLines($traida, [string[]]@(
                '# Papeleria POS: la base de este paquete de mudanza ya se trajo a esta PC (instalar-pc.ps1). Falta terminar en la PC de donde salio.',
                "paquete=$idDelPaquete",
                "equipo=$env:COMPUTERNAME",
                "ips=$($ips -join ';')",
                'puerto=5080',
                "cuando=$(Ahora-Local)"), $utf8)
    } catch {
        # No es un error: la base ya quedo. Solo que la USB no lleva la direccion de esta PC (se escribe o se busca en la red alla).
        Escribir "Mudanza: no se pudo dejar el aviso $traida ($(Mensaje $_)). El paquete se queda como esta."
        return
    }
    Escribir "Mudanza: quedo el aviso $traida (esta PC: $($ips -join ', '))."
    $archivo = [string]$nota['archivo']
    if ($archivo -notmatch $nombreDeBaseMudada) { return }
    $suBase = En-La-Carpeta $carpeta $archivo
    try {
        if (Test-Path -LiteralPath $suBase) { Remove-Item -LiteralPath $suBase -Force -ErrorAction Stop }
        Resultado 'mudanza-paquete' 'borrado'
        Escribir "Mudanza: se quito la base del paquete ($suBase): queda como respaldo en esta PC."
    } catch {
        Escribir "Mudanza: no se pudo quitar la base del paquete ($suBase): $(Mensaje $_)"
    }
}

# ── Terminar: esta PC, de donde salio la base, queda como «Otra caja» ──

# Si un traida.txt (el aviso que deja en el paquete la PC que lo trajo) dice que ESE paquete se trajo a esa IP.
function Se-Trajo-Alla([string[]]$carpetas, [string]$idDelPaquete, [string]$ip) {
    if (-not $idDelPaquete -or -not $ip) { return $false }
    foreach ($carpeta in $carpetas) {
        if (-not $carpeta) { continue }
        $traida = Leer-Si-Esta (En-La-Carpeta $carpeta 'traida.txt')
        if (([string]$traida['paquete']).Trim() -ne $idDelPaquete) { continue }
        $ips = @(([string]$traida['ips']) -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($ips -contains $ip) {
            Escribir "  $(En-La-Carpeta $carpeta 'traida.txt') dice que este paquete se trajo a $ip."
            return $true
        }
    }
    $false
}

# Desde que caja.json apunta a la caja principal ya no se cancela ni se regresa: la caja de esta PC, al abrirse, le sube alla lo que
# cobro. Si despues algo no queda (quitar el servicio, apartar la base), no es «no se cambio nada»: la mudanza se dice «fallo», con lo
# que falta, y el motivo empieza por ahi (asi el asistente no dice «lo de antes sigue como estaba»). Si todo queda, al final dice
# «convertida» en su lugar.
function Ya-No-Hay-Regreso([string]$direccion, [string]$desde = '') {
    $script:loQueYaSeHizo = "La caja de esta PC ya apunta a la caja principal ($direccion)$desde, pero la mudanza no se termino: ya no se cancela ni se regresa, se repite «Terminar la mudanza»."
    Quitar-Resultado 'mudanza'
    Quitar-Resultado 'mudanza-mensaje'
    Resultado 'mudanza' 'fallo'
    Resultado 'mudanza-mensaje' "la caja de esta PC ya apunta a la caja principal ($direccion)$desde, pero falta quitar su servicio o apartar su base de antes. Ya no se cancela ni se regresa (no desinstales la principal): corre el instalador otra vez y elige «Terminar la mudanza»"
}

# Antes de actualizar. Primero lo que se puede regresar (caja.json deja su .anterior) y la marca al final: una corrida que se corte a
# la mitad se repite (el servicio puede seguir registrado o ya no: las dos valen).
function Mudanza-Terminar($r, $marca) {
    # Una corrida anterior ya dejo la caja de esta PC apuntando a la caja principal y despues algo no quedo: desde ahi ya no hay regreso,
    # aunque esta se detenga antes de cambiar nada. Se dice desde el principio.
    $yaApunta = Caja-Apunta-A-Otra-Pc
    if ($yaApunta) {
        Escribir "Mudanza: la caja de esta PC ya apunta a la caja principal ($yaApunta): una corrida anterior empezo a terminar la mudanza."
        Ya-No-Hay-Regreso $yaApunta ' desde una corrida anterior'
    }
    $direccion = ([string]$r['principal']).Trim()
    if (-not $direccion) {
        Fallo 'Para terminar la mudanza hace falta la direccion de la caja principal (la PC a donde se llevo la base de esta). No se cambio nada.'
    }
    Escribir "Mudanza: terminar. Se le pregunta a la caja principal ($direccion) si ya tiene la base de esta PC."

    # 1. La caja principal: que conteste, con la tienda de esta PC, y si reconoce la llave de esta caja (la prueba de identidad de
    #    siempre). La llave no pasa por aqui: la abre y la prueba el ayudante. El porque de «conoce» (sin secretos) va al registro.
    $probado = Ayudante @('probar', '--direccion', $direccion, '--esta-caja', 'si')
    $dijo = $probado.Claves
    $conoce = [string]$dijo['conoce']
    $renglon = "  La caja principal: contesta=$($dijo['contesta']), tienda=$($dijo['tienda']), sucursales=$($dijo['sucursales']), conoce=$conoce"
    $conoceMotivo = Sin-Punto ([string]$dijo['conoce-motivo'])
    if ($conoceMotivo) { $renglon = "$renglon, conoce-motivo=$conoceMotivo" }
    Escribir "$renglon."
    if ($dijo['contesta'] -ne 'si') {
        Fallo "La caja principal ($direccion) no contesta: revisa la direccion y que alla ya haya terminado el instalador. No se cambio nada."
    }
    if ($dijo['tienda'] -ne 'si') { Fallo 'La caja principal todavia no tiene la base: termina primero alla. No se cambio nada.' }
    if ($marca.Tienda) {
        if (-not (Misma-Tienda ([string]$dijo['sucursales']) $marca.Tienda)) { Fallo 'Esa PC tiene OTRA tienda, no la de esta PC. No se cambio nada.' }
    } elseif ($conoce -ne 'si') {
        # La marca no dice de que tienda es: solo vale la identidad fuerte, que la principal pruebe conocer la llave de esta caja.
        Fallo "La marca de la mudanza ($marcaDeMudanza) no se entiende y la caja principal ($direccion) no probo conocer la llave de esta caja: no se sabe si alla esta la base de esta PC. No se cambio nada."
    }

    # Que la USB de la mudanza, si esta a la mano, no diga lo contrario. Una base de antes de la misma tienda (la de un intento anterior
    # que se cancelo aqui sin quitarla alla) tambien contesta con esa tienda y conoce la llave de esta caja: si el paquete de ESTA mudanza
    # todavia trae su base y no tiene el aviso de que ya se trajo (traida.txt, que deja la principal al quedar), la principal no lo trajo.
    # El aviso gana, aunque en otra carpeta haya una copia del paquete con su base. Sin la USB (o en otra letra) no se puede ver.
    $carpetas = @(Carpetas-De-La-Mudanza $r $marca)
    if ($marca.Paquete -and -not (Traida-De $carpetas $marca.Paquete)) {
        foreach ($carpeta in $carpetas) {
            if (Trae-Su-Base $carpeta $marca.Paquete) {
                Fallo "El paquete de mudanza de $carpeta todavia trae la base de esta PC y no tiene el aviso de que ya se trajo: la caja principal ($direccion) puede tener una base de antes. Lleva la USB a la principal, corre ahi el instalador y elige «Traer la base de otra PC» (si ya la tenia no la trae otra vez: solo deja el aviso) y despues termina aqui. No se cambio nada."
            }
        }
    }
    $argumentos = @('escribir', '--respuestas', $Respuestas, '--mudada-a', $direccion)
    $alta = ''
    if ($conoce -eq 'si') {
        # La reconoce: la caja conserva su alta, y se le deja la llave publica del servicio nuevo (la que acaba de presentar).
        $alta = 'se-conserva'
        if ($dijo['llave-del-servicio']) { $argumentos += @('--llave-del-servicio', [string]$dijo['llave-del-servicio']) }
    } elseif ($conoce -eq 'sin-alta') {
        $alta = 'no-habia'
    } elseif ($conoce -eq 'no-se') {
        # No se pudo saber (la red, o la llave no se dejo abrir): conserva el alta sin fijar la llave del servicio; la caja la aprende.
        $alta = 'se-conserva'
    } elseif ($conoce -eq 'no') {
        # No la reconoce: solo se convierte (y la caja pide su alta otra vez) si la USB prueba que ESTE paquete se trajo a esa direccion.
        if (-not (Se-Trajo-Alla $carpetas $marca.Paquete (Ip-De $direccion))) {
            Fallo "La caja principal ($direccion) no reconoce la llave de esta caja. Si ya trajiste alla la base de esta PC, conecta la USB de la mudanza y corre el instalador otra vez; si no, termina primero alla. No se cambio nada."
        }
        $alta = 'otra-vez'
        $argumentos += @('--alta', 'otra-vez')
    } else {
        Fallo "El ayudante del instalador no dijo si la caja principal ($direccion) reconoce a esta caja (conoce='$conoce'). No se cambio nada."
    }

    # 2. caja.json: la direccion nueva y la llave publica del servicio nuevo, con su alta (lo de antes queda en caja.json.anterior).
    #    Con la caja cerrada: abierta, se quedaria con la direccion de antes hasta cerrarla.
    if ((Cajas-Abiertas).Count -gt 0) {
        Fallo 'La caja se abrio en esta PC mientras se revisaba: cierrala y corre el instalador otra vez. No se cambio nada.'
    }
    $convertida = Ayudante $argumentos
    if ($convertida.Codigo -ne 0 -or $convertida.Claves['error'] -or $convertida.Claves.Count -eq 0) {
        $porque = [string]$convertida.Claves['error']
        if (-not $porque) { $porque = "el ayudante del instalador termino con $($convertida.Codigo)." }
        Fallo "No se pudo dejar la caja de esta PC apuntando a la caja principal: $porque No se cambio nada."
    }
    $script:cambiosDeLaMudanza = [string]$convertida.Claves['cambios']
    Escribir "  caja.json ya apunta a la caja principal ($direccion); el alta: $alta."
    # Desde aqui, si algo no queda, ya no es «no se cambio nada».
    Ya-No-Hay-Regreso $direccion

    # 3. El servicio de esta PC, fuera (con el se van sus variables y su firewall). «sc delete» lo deja marcado para borrarse mientras
    #    algo lo tenga abierto: se espera a que Windows termine y, si no, no se mueve nada (la marca sigue: se repite).
    if (Servicio-Registrado) {
        $corrida = Correr (Join-Path $Paquete 'servicio\instalar-servicio.ps1') @('-Quitar')
        if ($corrida.Codigo -ne 0) { Escribir "  instalar-servicio.ps1 -Quitar termino con $($corrida.Codigo): se revisa si el servicio se quito." }
        $limite = (Get-Date).AddSeconds(60)
        while ((Servicio-Registrado) -and (Get-Date) -lt $limite) { Start-Sleep -Seconds 1 }
        if (Servicio-Registrado) { Fallo 'Windows no termino de quitar el servicio: cierra la ventana de Servicios y corre el instalador otra vez.' }
    } else {
        Escribir '  El servicio ya no estaba registrado (lo quito una corrida anterior).'
    }

    # 4. Lo que haria creer que la base sigue en esta PC se aparta (se mueve: ni se copia ni se borra): servicio.json (con el, la caja
    #    creeria que el servicio sigue aqui), las llaves y la base con lo que la acompana (primero eso y al final ella); la marca, lo
    #    ultimo. Los respaldos, los registros, lo de eleventa y lo de la caja se quedan donde estan.
    $sello = Get-Date -Format 'yyyy-MM-dd-HHmm'
    $apartada = Join-Path $datos "antes-de-mudar-$sello"
    for ($n = 2; Test-Path -LiteralPath $apartada; $n++) { $apartada = Join-Path $datos "antes-de-mudar-$sello-$n" }
    Escribir "  La base de antes va a $apartada."
    try {
        [IO.Directory]::CreateDirectory($apartada) | Out-Null
        $deLaBase = @(Get-ChildItem -LiteralPath $datos -Force | Where-Object { $_.Name -like 'pos.db.*' -and $_.Name -ne 'pos.db.mudada' } | ForEach-Object { $_.Name })
        $loQueSeAparta = @('servicio.json', 'servicio.json.anterior', 'pos.llave', 'pos.controlprint', 'pos.db-wal', 'pos.db-shm', 'pos.db-journal', 'pos.db') + $deLaBase + @('pos.db.mudada')
        foreach ($archivo in $loQueSeAparta) {
            $origen = Join-Path $datos $archivo
            if (-not (Test-Path -LiteralPath $origen)) { continue }
            # Unos intentos: el antivirus puede tener abierto un momento el archivo.
            for ($intento = 1; ; $intento++) {
                try { Move-Item -LiteralPath $origen -Destination (Join-Path $apartada $archivo) -ErrorAction Stop; break }
                catch { if ($intento -ge 5) { throw }; Start-Sleep -Seconds 1 }
            }
            Escribir "    $archivo"
        }
    } catch {
        Fallo "No se pudo apartar la base de esta PC ($(Sin-Punto (Mensaje $_))): la mudanza sigue a medias. Corre el instalador otra vez y elige «Terminar la mudanza»."
    }

    # Ya es «Otra caja». Se dice desde ahora: si lo que sigue (actualizar la caja, su configuracion) no queda, esto ya quedo. En lugar
    # de lo de «ya no hay regreso»: ya quedo.
    Quitar-Resultado 'mudanza'
    Quitar-Resultado 'mudanza-mensaje'
    Resultado 'mudanza' 'convertida'
    Resultado 'mudanza-alta' $alta
    Resultado 'mudanza-apartada' $apartada
    $script:loQueYaSeHizo = "La mudanza ya se termino: esta PC ya es «Otra caja» de $direccion y su base de antes quedo apartada en $apartada. Pero lo demas no quedo:"
    Escribir "Mudanza: terminada. Esta PC ya es «Otra caja» de $direccion; su base de antes quedo apartada (no se borro) en $apartada."
}

# ── Cancelar: esta PC vuelve a tener la base ──

# A donde dice un traida.txt que se trajo la base, para decirlo: el nombre de esa PC, sus IP y cuando (lo que traiga, y solo con letras
# que se puedan ensenar: viene de una USB).
function A-Donde-Se-Trajo($traida, [string[]]$ips) {
    $equipoAlla = ([string]$traida.Claves['equipo']) -replace '[^A-Za-z0-9._-]', ''
    if ($equipoAlla.Length -gt 63) { $equipoAlla = $equipoAlla.Substring(0, 63) }
    $lasIps = @($ips | Select-Object -First 4) -join ', '
    $donde = if ($equipoAlla -and $lasIps) { "$equipoAlla ($lasIps)" } elseif ($equipoAlla) { $equipoAlla } elseif ($lasIps) { $lasIps } else { 'otra PC' }
    $cuando = ([string]$traida.Claves['cuando']).Trim()
    if ($cuando -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$') { $donde = "$donde el $cuando" }
    $donde
}

# Para pedir que se borre de la USB la carpeta del paquete: tal cual, si esta a la mano; si no (la USB no esta conectada, o ahora tiene
# otra letra), por su nombre, con donde estaba al sacar la base.
function Pedir-Que-Se-Borre([string]$carpeta) {
    if (-not $carpeta) { return 'Borra la carpeta del paquete de mudanza de la USB: ese paquete ya no sirve.' }
    $aLaMano = $false
    try { $aLaMano = [bool](Test-Path -LiteralPath $carpeta -PathType Container) } catch { }
    $hoja = ((($carpeta -replace '[\\/]+$', '') -split '[\\/]')[-1]).Trim()
    if ($aLaMano -or -not $hoja -or $hoja -match '^[A-Za-z]:$') { return "Borra la carpeta $carpeta de la USB: ese paquete ya no sirve." }
    "Borra la carpeta «$hoja» de la USB (al sacar la base era $carpeta): ese paquete ya no sirve."
}

# Antes de actualizar. Solo si ninguna otra PC contesta ya con esa tienda y, si la USB de la mudanza dice que la base ya se trajo a otra
# PC (su traida.txt), solo si se confirmo que alla ya se desinstalo: a sabiendas, nunca dos bases vivas. Y nunca despues de que un
# «Terminar» dejo la caja de esta PC apuntando a la caja principal. Al servicio lo arranca el paso revisar (despues de actualizar.ps1, si
# hay version nueva).
function Mudanza-Cancelar($r, $marca) {
    Escribir 'Mudanza: cancelar. Se revisa que la base no este ya viva en otra PC.'
    # Un «Terminar» que ya dejo la caja de esta PC apuntando a la caja principal: la base ya vive alla (la caja le sube lo que cobra).
    $yaApunta = Caja-Apunta-A-Otra-Pc
    if ($yaApunta) {
        Resultado 'mudanza' 'fallo'
        Resultado 'mudanza-mensaje' "la caja de esta PC ya apunta a la caja principal ($yaApunta): la mudanza se empezo a terminar, asi que ya no se cancela ni se regresa; corre el instalador otra vez y elige «Terminar la mudanza»"
        Fallo "La caja de esta PC ya apunta a la caja principal ($yaApunta): la mudanza se empezo a terminar, asi que no se cancelo (ya no se regresa). Corre el instalador otra vez y elige «Terminar la mudanza». No se cambio nada."
    }
    if (-not $marca.Tienda) {
        Fallo "La marca de la mudanza ($marcaDeMudanza) no se entiende: no se puede revisar si la base ya esta viva en otra PC, asi que no se cancelo. Si la otra PC ya la tiene, elige «Terminar la mudanza». No se cambio nada."
    }

    # El nombre de la tienda va en la linea de ordenes del ayudante, y Windows PowerShell 5.1 no sabe pasar unas comillas dentro de un
    # argumento (ni una diagonal al final de uno con espacios): llegaria otro nombre, nadie contestaria con el y se cancelaria sin
    # haber revisado nada.
    if ($marca.Tienda.Contains('"') -or $marca.Tienda.EndsWith('\')) {
        Fallo "El nombre de la tienda ($($marca.Tienda)) lleva comillas (o termina en una diagonal): desde aqui no se puede revisar si la base ya esta viva en otra PC, asi que no se cancelo. Si la otra PC ya la tiene, elige «Terminar la mudanza». No se cambio nada."
    }

    # 1. En la red, y en la direccion que dejo la PC que la trajo: el traida.txt de este paquete, en la carpeta que dijo el asistente o en
    #    la de la marca (la USB pudo cambiar de letra).
    $carpetas = @(Carpetas-De-La-Mudanza $r $marca)
    $argumentos = @('mudanza-viva', '--tienda', $marca.Tienda)
    $traida = Traida-De $carpetas $marca.Paquete
    $ips = @()
    if ($traida) {
        $ips = @(([string]$traida.Claves['ips']) -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[0-9.]+$' })
        if ($ips.Count -gt 0) {
            $argumentos += @('--direcciones', ($ips -join ';'))
            Escribir "  $($traida.Archivo) dice que este paquete ya se trajo a $($ips -join ', '): tambien se le pregunta ahi."
        } else {
            Escribir "  $($traida.Archivo) dice que este paquete ya se trajo, pero no a que direccion: solo se pregunta en la red."
        }
    } else {
        Escribir '  No se encontro el aviso traida.txt de este paquete (la USB de la mudanza no esta a la mano, o la base todavia no se trajo): solo se pregunta en la red.'
    }
    $viva = Ayudante $argumentos
    $dijo = $viva.Claves
    if ($dijo['viva'] -eq 'si') {
        $donde = [string]$dijo['donde']
        if ($dijo['equipo']) { $donde = "$donde ($($dijo['equipo']))" }
        # Apagarla no basta: su servicio quedo automatico y, al prenderla, volveria a trabajar con la base (sin marca en ninguna de las dos).
        Fallo "La base ya esta viva en ${donde}: no se cancelo. Si de verdad quieres regresar, primero desinstala alla con «Borrar tambien los datos» y corre esto otra vez. Apagar esa PC no basta: al prenderla volveria a trabajar con esa base."
    }
    if ($viva.Codigo -ne 0 -or $dijo['viva'] -ne 'no') {
        $porque = Sin-Punto ([string]$dijo['error'])
        if (-not $porque) { $porque = "el ayudante del instalador termino con $($viva.Codigo)" }
        Fallo "No se pudo revisar si la base ya esta viva en otra PC ($porque): no se cancelo. Corre el instalador otra vez. No se cambio nada."
    }
    # Nadie contesto con esa tienda, pero la USB prueba que la base ya se trajo a otra PC: esa PC pudo estar apagada o fuera de la red, y
    # al prenderla volveria a trabajar con la base. Solo se cancela si en el asistente se confirmo que alla ya se desinstalo con «Borrar
    # tambien los datos» (mudanza-aunque-traida; sin preguntar no hay quien lo confirme).
    if ($traida) {
        if (([string]$r['mudanza-aunque-traida']).Trim() -ne 'si') {
            Fallo "La USB de la mudanza dice que la base de esta PC ya se trajo a $(A-Donde-Se-Trajo $traida $ips), y esa PC ahora no contesta: no se cancelo, quedarian dos PC con la misma base. Si alla ya se desinstalo Papeleria POS con «Borrar tambien los datos», corre el instalador otra vez y confirmalo. No se cambio nada."
        }
        Escribir '  La USB dice que la base ya se trajo a otra PC, y en el asistente se confirmo que alla ya se desinstalo con «Borrar tambien los datos»: se cancela.'
    }

    # 2. Fuera la marca (queda como pos.db.mudada.cancelada, encima de una anterior) y el servicio, otra vez automatico. No se arranca
    #    aqui: si hay version nueva, primero la pone actualizar.ps1.
    $cancelada = "$marcaDeMudanza.cancelada"
    try {
        if (Test-Path -LiteralPath $cancelada) { Remove-Item -LiteralPath $cancelada -Recurse -Force -ErrorAction Stop }
        Move-Item -LiteralPath $marcaDeMudanza -Destination $cancelada -ErrorAction Stop
    } catch {
        Fallo "No se pudo quitar la marca de la mudanza ($(Sin-Punto (Mensaje $_))): no se cancelo. Corre el instalador otra vez. No se cambio nada."
    }
    if (Get-Service -Name $nombre -ErrorAction SilentlyContinue) {
        try {
            Set-Service -Name $nombre -StartupType Automatic -ErrorAction Stop
        } catch {
            $porque = Sin-Punto (Mensaje $_)
            # Sin el servicio habilitado no esta cancelada: la marca regresa, y se puede repetir.
            try { Move-Item -LiteralPath $cancelada -Destination $marcaDeMudanza -ErrorAction Stop } catch { Escribir "  Tampoco se pudo regresar la marca: $(Mensaje $_)" }
            Fallo "No se pudo habilitar el servicio de esta PC ($porque): no se cancelo. Corre el instalador otra vez."
        }
    } else {
        Escribir '  El servicio no esta registrado en esta PC: lo instala este mismo paso, con la base que ya esta aqui.'
    }
    $script:arrancarSiNoQueda = $true
    $script:loQueYaSeHizo = 'La mudanza ya se cancelo (esta PC vuelve a tener la base), pero lo demas no quedo:'
    Resultado 'mudanza' 'cancelada'

    # 3. El paquete ya no sirve. Donde este a la mano con el mudanza.txt de ESTE paquete (la carpeta de la marca y la que dijo el
    #    asistente: la USB pudo cambiar de letra), se le quitan su mudanza.txt (sin el esta incompleto: ya no se puede traer) y su base.
    #    Lo que no se pudo borrar, o si no estaba en ninguna, se pide borrarlo.
    $borradoEn = New-Object System.Collections.Generic.List[string]
    $quedoEn = ''
    if ($marca.Paquete) {
        foreach ($carpeta in $carpetas) {
            $delPaquete = Leer-Si-Esta (En-La-Carpeta $carpeta 'mudanza.txt')
            if (([string]$delPaquete['paquete']).Trim() -ne $marca.Paquete) { continue }
            $borrado = $false
            try {
                Remove-Item -LiteralPath (En-La-Carpeta $carpeta 'mudanza.txt') -Force -ErrorAction Stop
                $archivo = ([string]$delPaquete['archivo']).Trim()
                if (-not $archivo) { $archivo = $marca.Archivo }
                if ($archivo -match $nombreDeBaseMudada) {
                    $suBase = En-La-Carpeta $carpeta $archivo
                    if (Test-Path -LiteralPath $suBase) { Remove-Item -LiteralPath $suBase -Force -ErrorAction Stop }
                    $borrado = $true
                }
            } catch {
                Escribir "  No se pudo borrar el paquete de ${carpeta}: $(Mensaje $_)"
            }
            if ($borrado) { $borradoEn.Add($carpeta) } elseif (-not $quedoEn) { $quedoEn = $carpeta }
        }
    }
    if ($borradoEn.Count -gt 0) { Escribir "  El paquete de $($borradoEn -join ' y de ') se borro (su mudanza.txt y su base): ya no sirve." }
    if ($quedoEn) {
        Resultado 'mudanza-mensaje' (Pedir-Que-Se-Borre $quedoEn)
        Escribir "  El paquete de $quedoEn no se pudo borrar entero: hay que borrar esa carpeta de la USB."
    } elseif ($borradoEn.Count -eq 0) {
        Resultado 'mudanza-mensaje' (Pedir-Que-Se-Borre $marca.Carpeta)
        Escribir '  El paquete no se borro (la USB no esta a la mano, o ahi ya no esta este paquete completo): hay que borrar su carpeta de la USB.'
    }
    Escribir 'Mudanza: cancelada. El servicio de esta PC quedo automatico; se arranca al revisar.'
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
    # La de una corrida anterior del paso instalar, si quedo: al paso revisar solo le sirve la de esta.
    Remove-Item -LiteralPath (Nota-Por-Cerrar) -Force -ErrorAction SilentlyContinue

    $habiaServicio = [bool](Get-Service -Name $nombre -ErrorAction SilentlyContinue)
    $habiaCaja = Test-Path -LiteralPath $exeCaja

    # La mudanza de la base: lo que se pidio y si esta PC esta a media mudanza (la marca). Lo que no se puede se dice aqui, antes de
    # tocar nada (ni cerrar la caja).
    $mudanza = ([string]$r['mudanza']).Trim()
    $hayMarca = Test-Path -LiteralPath $marcaDeMudanza
    $carpetaDelPaquete = Carpeta-Normal ([string]$r['mudanza-paquete'])
    if ($mudanza -notin @('', 'sacar', 'traer', 'terminar', 'cancelar')) { Fallo "El asistente pidio una mudanza que no se conoce ('$mudanza'). No se cambio nada." }
    if ($hayMarca -and $mudanza -notin @('terminar', 'cancelar')) {
        Fallo 'La base de esta PC se saco para mudarla a otra PC: corre el instalador y elige «Terminar la mudanza» o «Cancelar la mudanza». No se cambio nada.'
    }
    if (-not $hayMarca -and $mudanza -in @('terminar', 'cancelar')) { Fallo 'En esta PC no hay una mudanza a medias. No se cambio nada.' }
    # Una PC con el servicio solo deja de tenerlo al terminar una mudanza (con la marca: su base ya vive en otra PC).
    if (-not $conServicio -and $habiaServicio -and $mudanza -ne 'terminar') {
        Fallo 'En esta PC esta el servicio con la base de la tienda: no puede ser «Otra caja». No se cambio nada.'
    }
    if ($mudanza -eq 'terminar' -and $conServicio) { Fallo 'Al terminar la mudanza esta PC queda como «Otra caja»: el asistente pidio otra cosa. No se cambio nada.' }
    if ($mudanza -in @('sacar', 'traer', 'cancelar') -and -not $conServicio) {
        Fallo "La base de la tienda solo se saca, se trae o se regresa en una PC que lleva el servicio, no en «Otra caja» (mudanza '$mudanza'). No se cambio nada."
    }
    if ($mudanza -in @('sacar', 'traer') -and -not $carpetaDelPaquete) { Fallo 'El asistente no dijo la carpeta del paquete de mudanza. No se cambio nada.' }

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

    # Si una corrida anterior se corto mientras sacaba la base (se fue la luz antes de la marca), el servicio vuelve a quedar como antes.
    Si-Se-Corto-Al-Sacar

    # La mudanza, antes de tocar el programa. Sacar y traer aqui solo revisan (lo suyo va despues, ya con el programa puesto); terminar
    # y cancelar corren enteros aqui: actualizar.ps1 no actualiza una PC con la marca.
    $yaEstaba = $false
    $resumenDelPaquete = @{}
    if ($mudanza -eq 'sacar') {
        Mudanza-Antes-De-Sacar $carpetaDelPaquete
    } elseif ($mudanza -eq 'traer') {
        # Todo lo que se puede saber antes: el paquete entero (su SHA-256), que la PC de donde salio no haya vuelto a trabajar, la base que
        # tiene esta PC y como quedaria el puente con eleventa.
        $versionDelPaquete = Version-De (Join-Path $Paquete 'servicio\PapeleriaPos.Servicio.exe')
        if (-not $versionDelPaquete) { Fallo "No se pudo leer la version del programa que trae el instalador ($Paquete): no se revisa el paquete de mudanza. No se cambio nada." }
        $revisado = Ayudante @('mudanza-revisar', '--paquete', $carpetaDelPaquete, '--respuestas', $Respuestas, '--version', $versionDelPaquete)
        if ($revisado.Codigo -ne 0 -or $revisado.Claves['bien'] -ne 'si') {
            $porque = [string]$revisado.Claves['error']
            if (-not $porque) { $porque = "El ayudante del instalador no pudo revisar el paquete de mudanza (termino con $($revisado.Codigo))." }
            Fallo "$porque No se cambio nada."
        }
        $yaEstaba = $revisado.Claves['ya-estaba'] -eq 'si'
        $resumenDelPaquete = Leer-Si-Esta (En-La-Carpeta $carpetaDelPaquete 'mudanza.txt')
        if ([string]$resumenDelPaquete['archivo'] -notmatch $nombreDeBaseMudada -or ([string]$resumenDelPaquete['sha256']).Trim() -notmatch '^[0-9A-Fa-f]{64}$' -or -not ([string]$resumenDelPaquete['paquete']).Trim()) {
            Fallo "El mudanza.txt del paquete de $carpetaDelPaquete no se entiende (su id, su archivo o su SHA-256). No se cambio nada."
        }
        Escribir "Mudanza: el paquete de $carpetaDelPaquete se puede traer (ya estaba puesto: $(if ($yaEstaba) { 'si' } else { 'no' }))."
    } elseif ($mudanza -eq 'terminar') {
        Mudanza-Terminar $r (Leer-Marca)
        # Ya sin el servicio: lo que sigue es lo de cualquier «Otra caja».
        $habiaServicio = $false
        $conServicio = $false
    } elseif ($mudanza -eq 'cancelar') {
        Mudanza-Cancelar $r (Leer-Marca)
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
    # Al instalar, o si es otra que la de antes: la guia la apaga mientras la caja no se usa, y actualizar sin cambiar nada la volvia a prender.
    $cola = [string]$r['cola']
    $colaDeAntes = $null
    try { $colaDeAntes = (Get-Content -LiteralPath (Join-Path $datos 'Caja\caja.json') -Raw -Encoding UTF8 | ConvertFrom-Json).ColaImpresion } catch { }
    if ($cola -and ($cola -ne $colaDeAntes -or -not $habiaCaja)) {
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
        if ($cola) { Escribir "La cola '$cola' ya estaba elegida: se queda como este." }
        Resultado 'cola' 'no-aplica'
    }

    # La mudanza, «Traer»: primero la base (con el servicio.json de esta PC: el puente, apagado) y hasta despues la configuracion que
    # lo deja como estaba alla. Si la base no quedo puesta, se escribe la configuracion de siempre, sin lo de la mudanza.
    $argumentosDeEscribir = @('escribir', '--respuestas', $Respuestas)
    $traida = ''
    if ($mudanza -eq 'traer') {
        # Lo ultimo que dice es como quedo.
        $traida = [string](@(Mudanza-Traer $carpetaDelPaquete $resumenDelPaquete $yaEstaba $habiaServicio ($r['mudanza-encima'] -eq 'si')) | Select-Object -Last 1)
        if ($traida -in @('traida', 'ya-estaba')) { $argumentosDeEscribir += @('--mudanza', $carpetaDelPaquete) }
    }

    # servicio.json y caja.json: solo lo que cambio, sin tocar lo demas (el ayudante; lo de antes queda en «.anterior»).
    $escrito = Ayudante $argumentosDeEscribir
    if ($escrito.Codigo -ne 0) {
        Resultado 'configuracion' 'fallo'
        if ($traida -in @('traida', 'ya-estaba')) {
            # La base ya es la de esta PC y servicio.json no se toco (el puente, apagado): no es «no se instalo». Otra corrida con el
            # mismo paquete lo termina.
            $porque = Sin-Punto ([string]$escrito.Claves['error'])
            if (-not $porque) { $porque = "el ayudante del instalador termino con $($escrito.Codigo)" }
            Escribir "Mudanza: a-medias. No se escribio la configuracion de la mudanza: $porque"
            Resultado 'mudanza' 'a-medias'
            Resultado 'mudanza-mensaje' "no se escribio la configuracion de la mudanza ($porque)"
            Terminar 0
        }
        Fallo "No se escribio la configuracion de esta PC: $($escrito.Claves['error'])"
    }
    $cambios = @($script:cambiosDeLaMudanza, [string]$escrito.Claves['cambios'] | Where-Object { $_ }) -join '; '
    $cambio = [bool]$script:cambiosDeLaMudanza -or $escrito.Claves['servicio-json'] -eq 'cambio' -or $escrito.Claves['caja-json'] -eq 'cambio'
    Resultado 'configuracion' $(if ($cambio) { 'cambio' } else { 'igual' })
    Resultado 'servicio-json' $escrito.Claves['servicio-json']
    Resultado 'caja-json' $escrito.Claves['caja-json']
    Resultado 'cambios' $cambios
    Escribir "La configuracion: $(if ($cambios) { $cambios } else { 'sin cambios' })."

    if ($traida -in @('traida', 'ya-estaba')) {
        # La base del paquete es la de esta PC y su configuracion ya esta escrita. Lo que falta (que el servicio conteste con esa
        # tienda, el aviso en el paquete) lo hace el paso revisar, que reinicia el servicio si la configuracion cambio.
        Resultado 'mudanza' $traida
        try {
            [IO.File]::WriteAllLines((Nota-Por-Cerrar), [string[]]@(
                    '# Papeleria POS: del paso instalar al paso revisar, la base que se trajo (instalar-pc.ps1)',
                    "paquete=$(([string]$resumenDelPaquete['paquete']).Trim())",
                    "equipo=$(([string]$resumenDelPaquete['equipo']).Trim())",
                    "archivo=$(([string]$resumenDelPaquete['archivo']).Trim())",
                    "tienda=$(([string]$resumenDelPaquete['tienda']).Trim())",
                    "carpeta=$carpetaDelPaquete"), $utf8)
        } catch {
            Escribir "  No se pudo dejar la nota para el paso revisar: $(Mensaje $_)"
        }
    }

    # La mudanza, «Sacar»: lo ultimo, con el programa y la configuracion de siempre ya puestos.
    if ($mudanza -eq 'sacar') { Mudanza-Sacar $carpetaDelPaquete }

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
    # Con la base sacada para mudarla (la marca), el servicio esta detenido y deshabilitado a proposito, y con la marca no arrancaria:
    # ni se reinicia ni se le espera, y no es que una actualizacion no pudo regresar.
    if (Test-Path -LiteralPath $marcaDeMudanza) {
        Resultado 'servicio' 'mudada'
        Resultado 'servicio-mensaje' 'El servicio de esta PC esta detenido a proposito: su base se saco para mudarla.'
        Resultado 'eleventa' 'no-aplica'
        Resultado 'controlprint' 'no-aplica'
        Escribir 'El servicio de esta PC esta detenido a proposito: su base se saco para mudarla.'
        Terminar 0
    }
    if ($Reiniciar) {
        # Lee servicio.json y sus variables (la clave, el codigo) al arrancar.
        Escribir 'Reiniciando el servicio...'
        try { Restart-Service -Name $nombre -Force -ErrorAction Stop } catch { Escribir "  No se pudo reiniciar: $($_.Exception.Message)" }
    }
    # Detenido, por ejemplo tras varios arranques fallidos: «Como quedo» manda a correr esto otra vez. Deshabilitado no se toca: asi lo deja
    # actualizar.ps1 cuando no pudo regresar solo, para que nada toque la base hasta regresarla a mano.
    $servicioAhora = Get-Service -Name $nombre -ErrorAction SilentlyContinue
    $deshabilitado = $servicioAhora -and $servicioAhora.StartType -eq 'Disabled'
    if ($servicioAhora -and $servicioAhora.Status -eq 'Stopped' -and -not $deshabilitado) {
        Escribir 'El servicio estaba detenido: se arranca.'
        try { Start-Service -Name $nombre -ErrorAction Stop } catch { Escribir "  No se pudo arrancar: $($_.Exception.Message)" }
    }
    $estado = if ($deshabilitado) { $null } else { Esperar-Estado 120 }
    if (-not $estado) {
        Resultado 'servicio' 'no-contesta'
        Resultado 'servicio-mensaje' $(if ($deshabilitado) { '«Papeleria POS» esta deshabilitado en Servicios: una actualizacion no pudo regresar sola. No vendas en esta caja y sigue el instructivo, «Actualizar la caja 2 del piloto».' } else { 'El servicio no contesto en 2 minutos: revisa en Servicios (services.msc) que «Papeleria POS» este en ejecucion.' })
        Resultado 'eleventa' $(if ($r['eleventa'] -eq 'si') { 'no-contesta' } else { 'no-aplica' })
        Resultado 'controlprint' $(if ($r['controlprint'] -eq 'si') { 'no-contesta' } else { 'no-aplica' })
        Escribir 'El servicio no contesto en 2 minutos.'
        # Si en el paso instalar se trajo la base de otra PC: quedo a medias, y se dice.
        if ($r['mudanza'] -eq 'traer') { Mudanza-Cerrar-La-Traida $null }
        Terminar 0
    }
    Resultado 'servicio' 'contesta'
    Resultado 'servicio-version' ([string]$estado.version)
    Resultado 'tienda-nueva' $(if ($estado.tiendaNueva) { 'si' } else { 'no' })
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
    # Si en el paso instalar se trajo la base de otra PC, aqui se da por terminado (o se dice que quedo a medias).
    if ($r['mudanza'] -eq 'traer') { Mudanza-Cerrar-La-Traida $estado }
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
    # A media mudanza (la marca), la caja de esta PC vende con su copia y guarda lo cobrado para subirlo al terminar: con los datos se
    # iria. No se quita nada: ni la caja ni el servicio.
    if ($BorrarDatos -and (Test-Path -LiteralPath $marcaDeMudanza)) {
        Resultado 'quitado' 'no'
        Resultado 'datos' 'quedaron'
        Fallo 'Esta PC esta a media mudanza y su caja puede tener ventas sin subir: primero corre el instalador y termina o cancela la mudanza.'
    }
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
    # Con el renglon del script: asi la proxima vez dice donde fue.
    $donde = $_.InvocationInfo
    Escribir "Donde: instalar-pc.ps1, renglon $($donde.ScriptLineNumber): $($donde.PositionMessage -replace '\s*\r?\n\s*', ' | ')"
    if ($Paso -eq 'instalar' -and $script:basePuesta) {
        # Al traer la base, con la del paquete ya puesta en esta PC: no es «no se instalo» (el programa ya quedo y la base ya esta
        # aqui). Quedo a medias: otra corrida con el mismo paquete lo termina.
        Resultado 'mudanza' 'a-medias'
        Resultado 'mudanza-mensaje' "algo que no se esperaba (instalar-pc.ps1, renglon $($donde.ScriptLineNumber)): $(Sin-Punto $_.Exception.Message)"
        Terminar 0
    }
    Fallo "Algo que no se esperaba (instalar-pc.ps1, renglon $($donde.ScriptLineNumber)): $($_.Exception.Message)"
}
