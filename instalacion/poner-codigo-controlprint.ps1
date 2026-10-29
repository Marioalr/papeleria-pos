#Requires -RunAsAdministrator
<#
    Pone (o quita) el codigo de conexion de Control Print en el servicio de la caja 2 (docs/pruebas-en-tienda.md, «Control Print en
    la caja 2»). Con el, el POS lee la cola «por cobrar» de Control Print de la caja principal y la muestra en F2; SOLO LEE: nunca
    le escribe a Control Print. El codigo va solo en una variable de entorno del servicio, ControlPrint__Codigo (como la clave de
    eleventa), nunca en un archivo (ni en servicio.json). Se corre como administrador:

        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\poner-codigo-controlprint.ps1"
        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\poner-codigo-controlprint.ps1" -Quitar

    - El codigo esta en la caja principal, en Control Print -> Ajustes -> Red, «Codigo de conexion» (16 letras y numeros; los
      guiones, los espacios y las mayusculas dan igual). Se pide sin que se vea al escribirlo.
    - Lo pone en las variables de entorno del servicio PapeleriaPos (en el registro de Windows: el valor Environment de
      HKLM\SYSTEM\CurrentControlSet\Services\PapeleriaPos), sin tocar las demas que tenga (la clave de eleventa sigue ahi).
    - Reinicia el servicio y espera a que salude a Control Print y lea su cola (http://localhost:5080/api/controlprint): dice si
      salio bien o por que no (el codigo no coincide, Control Print no contesta, falta la seccion «ControlPrint» en servicio.json).
      Nunca escribe el codigo.
    - Con -Quitar: lo quita (las demas variables se quedan) y reinicia el servicio; las tarjetas de Control Print dejan de salir.
    - Actualizar (actualizar.ps1) no lo toca. instalar-servicio.ps1 -Quitar si se lo lleva (quita el servicio con sus variables):
      despues de volver a instalarlo, se corre este otra vez.

    -Puerto            El del servicio, si se cambio en servicio.json (Urls). Por omision, 5080.
    -SegundosDeEspera  Lo mas que se espera la primera lectura de Control Print. Por omision, 60.
    -Codigo            (Una cadena segura) Para correrlo desde otro script, como las pruebas; en la tienda se pregunta.

    Termina con 0 si el codigo quedo y Control Print contesto con el, con 2 si quedo pero no se pudo leer (dice por que), y con 1 si
    no se pudo poner (o quitar).
    Los mensajes van sin acentos: asi se leen igual en cualquier consola.
#>
param(
    [switch]$Quitar,
    [int]$Puerto = 5080,
    [int]$SegundosDeEspera = 60,
    [securestring]$Codigo
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$nombre = 'PapeleriaPos'
$llave = "HKLM:\SYSTEM\CurrentControlSet\Services\$nombre"
$variable = 'ControlPrint__Codigo'
$api = "http://localhost:$Puerto/api"

function Texto-De([securestring]$seguro) {
    $puntero = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($puntero) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($puntero) }
}

function Consultar([string]$ruta) {
    try { Invoke-RestMethod -Uri "$api/$ruta" -UseBasicParsing -TimeoutSec 10 } catch { $null }
}

if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    Write-Host 'Esta es la ventana de PowerShell de 32 bits (x86): abre Windows PowerShell (sin x86) como administrador.' -ForegroundColor Red
    exit 1
}
if (-not (Get-Service -Name $nombre -ErrorAction SilentlyContinue)) {
    Write-Host 'No esta instalado el servicio PapeleriaPos en esta PC: primero instalar-servicio.ps1 (docs/pruebas-en-tienda.md).' -ForegroundColor Red
    exit 1
}

# Las variables de entorno que ya tiene el servicio, sin la del codigo: se quedan como estan.
$actuales = @()
$propiedad = Get-ItemProperty -LiteralPath $llave -Name Environment -ErrorAction SilentlyContinue
if ($propiedad -and $propiedad.Environment) { $actuales = @($propiedad.Environment | Where-Object { $_ }) }
$otras = @($actuales | Where-Object { -not $_.StartsWith("$variable=", [StringComparison]::OrdinalIgnoreCase) })
$teniaCodigo = $actuales.Count -ne $otras.Count

try {
    if ($Quitar) {
        if (-not $teniaCodigo) {
            Write-Host 'El servicio no tenia el codigo de Control Print: no habia nada que quitar.'
            exit 0
        }
        if ($otras.Count -gt 0) {
            New-ItemProperty -LiteralPath $llave -Name Environment -PropertyType MultiString -Value ([string[]]$otras) -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $llave -Name Environment
        }
        Write-Host 'Se quito el codigo de Control Print del servicio.'
    } else {
        if (-not $Codigo) {
            Write-Host 'El codigo esta en la caja principal: Control Print -> Ajustes -> Red, «Codigo de conexion».'
            $Codigo = Read-Host 'Codigo de conexion de Control Print (no se ve al escribirlo)' -AsSecureString
        }
        # Como lo acepta Control Print: sin guiones ni espacios y en mayusculas (ABCD-EFGH-JKLM-NPQR).
        $texto = ((Texto-De $Codigo) -replace '[^A-Za-z0-9]', '').ToUpperInvariant()
        if ($texto.Length -ne 16) {
            $texto = $null
            Write-Host 'El codigo de conexion tiene 16 letras y numeros (como ABCD-EFGH-JKLM-NPQR): no se cambio nada. Corre el script otra vez.' -ForegroundColor Red
            exit 1
        }
        $valores = [string[]](@($otras) + "$variable=$texto")
        $texto = $null
        New-ItemProperty -LiteralPath $llave -Name Environment -PropertyType MultiString -Value $valores -Force | Out-Null
        $valores = $null
        Write-Host $(if ($teniaCodigo) { 'Se cambio el codigo de Control Print del servicio.' } else { 'Se puso el codigo de Control Print en el servicio.' })
    }
} catch {
    # Sin el detalle del registro: nunca el codigo.
    Write-Host "No se pudo guardar en las variables del servicio ($($_.Exception.GetType().Name)): no se cambio nada." -ForegroundColor Red
    exit 1
}

# El servicio lee sus variables al arrancar.
Write-Host 'Reiniciando el servicio...'
try {
    Restart-Service -Name $nombre -Force
} catch {
    Write-Host "El servicio no se pudo reiniciar: $($_.Exception.Message) Reinicialo en Servicios (services.msc)." -ForegroundColor Red
    exit 2
}
$limite = (Get-Date).AddMinutes(2)
do {
    Start-Sleep -Seconds 2
    $estado = Consultar 'estado'
} while (-not $estado -and (Get-Date) -lt $limite)
if (-not $estado) {
    Write-Host 'El servicio no contesto en 2 minutos despues de reiniciarlo: revisa en Servicios (services.msc) que PapeleriaPos este en ejecucion.' -ForegroundColor Red
    exit 2
}

$cp = Consultar 'controlprint'
if ($Quitar) {
    if ($cp -and $cp.tieneCodigo) {
        Write-Host 'Aviso: el servicio sigue con un codigo de Control Print: esta tambien en otro lado (servicio.json o appsettings.json). Quitalo de ahi.' -ForegroundColor Yellow
        exit 2
    }
    Write-Host 'Listo: el servicio ya no tiene el codigo; las tarjetas de Control Print ya no salen en F2.' -ForegroundColor Green
    exit 0
}
if (-not $cp -or -not $cp.configurado) {
    Write-Host 'El codigo quedo puesto, pero el servicio no tiene la seccion «ControlPrint» en servicio.json: agrega «"ControlPrint": { "Servidor": "192.168.1.106" }» y corre el script otra vez (docs/pruebas-en-tienda.md).' -ForegroundColor Yellow
    exit 2
}

# El saludo y la primera lectura los hace el servicio al arrancar: aqui se espera y se dice como salio.
# Sin servidor: el de servicio.json no sirve (el servicio no lo repite, por si era el codigo) y $cp.error dice que hacer.
$donde = if ($cp.servidor) { " en $($cp.servidor):$($cp.puerto)" } else { '' }
Write-Host "Esperando a Control Print$donde..."
$limite = (Get-Date).AddSeconds($SegundosDeEspera)
while ($cp.contesta -ne $true -and -not ($cp.contesta -eq $false -and $cp.error) -and (Get-Date) -lt $limite) {
    Start-Sleep -Seconds 2
    $nuevo = Consultar 'controlprint'
    if ($nuevo) { $cp = $nuevo }
}
if ($cp.contesta -eq $true) {
    Write-Host "Listo: Control Print $($cp.version) en $($cp.nombre) contesta con el codigo; hay $($cp.porCobrar) por cobrar de hoy y ayer." -ForegroundColor Green
    Write-Host "Su certificado quedo fijado (huella $($cp.huella)): si otro contesta en esa direccion, no se le manda nada."
    if ($cp.soloInvitado) {
        Write-Host 'Aviso: Control Print esta en «cada quien ve sus trabajos»: sin sesion, en la caja 2 solo salen los de «Sin sesion».' -ForegroundColor Yellow
    }
    # Lo demas que hay que saber (el codigo tambien en un archivo, los costos de fabrica de Control Print): nunca trae el codigo.
    foreach ($aviso in @($cp.avisos)) {
        if ($aviso) { Write-Host "Aviso: $aviso" -ForegroundColor Yellow }
    }
    exit 0
}
if ($cp.error) {
    Write-Host "El codigo quedo puesto, pero Control Print no se pudo leer: $($cp.error)" -ForegroundColor Yellow
    if ([string]$cp.error -match 'no coincide|ya no acepta') { Write-Host 'Corre este script otra vez con el codigo de Control Print -> Ajustes -> Red.' -ForegroundColor Yellow }
    exit 2
}
Write-Host "El codigo quedo puesto, pero Control Print no contesto en $SegundosDeEspera s. Revisa en Edge http://localhost:$Puerto/api/controlprint en un rato, o corre el script otra vez." -ForegroundColor Yellow
exit 2
