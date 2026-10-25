#Requires -RunAsAdministrator
<#
    Pone (o quita) la clave de Firebird de eleventa en el servicio de la caja 2 del piloto (docs/pruebas-en-tienda.md, «La clave de
    Firebird»). Esa clave va solo en una variable de entorno del servicio, Eleventa__Clave, nunca en un archivo (ni en
    servicio.json). Se corre como administrador:

        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\poner-clave-eleventa.ps1"
        powershell -ExecutionPolicy Bypass -File "C:\Program Files\PapeleriaPos\Servicio\poner-clave-eleventa.ps1" -Quitar

    - Pide la clave dos veces, sin que se vea al escribirla.
    - La pone en las variables de entorno del servicio PapeleriaPos (en el registro de Windows: el valor Environment de
      HKLM\SYSTEM\CurrentControlSet\Services\PapeleriaPos), sin tocar las demás que tenga.
    - Reinicia el servicio y espera a su primera lectura de eleventa (http://localhost:5080/api/eleventa): dice si salió bien o por
      qué no (la clave equivocada, eleventa que no contesta, lo que falta en servicio.json). Nunca escribe la clave.
    - Con -Quitar: la quita (las demás variables se quedan) y reinicia el servicio; la lectura y el puente se apagan solos (dicen que
      falta la clave).
    - Actualizar (actualizar.ps1) no la toca: el servicio sigue siendo el mismo. instalar-servicio.ps1 -Quitar sí se la lleva (quita
      el servicio con todo y sus variables): después de volver a instalarlo, se corre este otra vez.

    -Puerto            El del servicio, si se cambió en servicio.json (Urls). Por omisión, 5080.
    -SegundosDeEspera  Lo más que se espera la primera lectura de eleventa. Por omisión, 180 (eleventa puede tardar en contestar
                       por el Wi-Fi; el servicio deja de esperarlo a los 2 minutos).
    -Clave             (Una cadena segura) Para correrlo desde otro script, como las pruebas; en la tienda se pregunta.

    Termina con 0 si la clave quedó y eleventa contestó con ella, con 2 si quedó pero la lectura no salió bien (dice por qué), y con 1
    si no se pudo poner (o quitar).
    Los mensajes van sin acentos: así se leen igual en cualquier consola.
#>
param(
    [switch]$Quitar,
    [int]$Puerto = 5080,
    [int]$SegundosDeEspera = 180,
    [securestring]$Clave
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$nombre = 'PapeleriaPos'
$llave = "HKLM:\SYSTEM\CurrentControlSet\Services\$nombre"
$variable = 'Eleventa__Clave'
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

# Las variables de entorno que ya tiene el servicio, sin la de la clave: se quedan como están.
$actuales = @()
$propiedad = Get-ItemProperty -LiteralPath $llave -Name Environment -ErrorAction SilentlyContinue
if ($propiedad -and $propiedad.Environment) { $actuales = @($propiedad.Environment | Where-Object { $_ }) }
$otras = @($actuales | Where-Object { -not $_.StartsWith("$variable=", [StringComparison]::OrdinalIgnoreCase) })
$teniaClave = $actuales.Count -ne $otras.Count

try {
    if ($Quitar) {
        if (-not $teniaClave) {
            Write-Host 'El servicio no tenia la clave de eleventa: no habia nada que quitar.'
            exit 0
        }
        if ($otras.Count -gt 0) {
            New-ItemProperty -LiteralPath $llave -Name Environment -PropertyType MultiString -Value ([string[]]$otras) -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $llave -Name Environment
        }
        Write-Host 'Se quito la clave de eleventa del servicio.'
    } else {
        if (-not $Clave) {
            $Clave = Read-Host 'Clave de Firebird de eleventa (no se ve al escribirla)' -AsSecureString
            $otraVez = Read-Host 'Escribela otra vez' -AsSecureString
            if ((Texto-De $Clave) -cne (Texto-De $otraVez)) {
                Write-Host 'Las dos no son iguales: no se cambio nada. Corre el script otra vez.' -ForegroundColor Red
                exit 1
            }
            $otraVez = $null
        }
        $texto = Texto-De $Clave
        if (-not $texto) {
            Write-Host 'La clave quedo vacia: no se cambio nada.' -ForegroundColor Red
            exit 1
        }
        if ($texto.IndexOfAny([char[]]@("`0", "`r", "`n")) -ge 0) {
            Write-Host 'La clave no puede llevar saltos de linea: no se cambio nada.' -ForegroundColor Red
            exit 1
        }
        $valores = [string[]](@($otras) + "$variable=$texto")
        $texto = $null
        New-ItemProperty -LiteralPath $llave -Name Environment -PropertyType MultiString -Value $valores -Force | Out-Null
        $valores = $null
        Write-Host $(if ($teniaClave) { 'Se cambio la clave de eleventa del servicio.' } else { 'Se puso la clave de eleventa en el servicio.' })
    }
} catch {
    # Sin el detalle del registro: nunca la clave.
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

if ($Quitar) {
    $lectura = Consultar 'eleventa'
    if ($lectura -and $lectura.activa) {
        Write-Host 'Aviso: el servicio sigue leyendo eleventa: la clave tambien esta en otro lado (servicio.json o appsettings.json). Quitala de ahi.' -ForegroundColor Yellow
        exit 2
    }
    Write-Host 'Listo: el servicio ya no tiene la clave; la lectura y el puente con eleventa estan apagados.' -ForegroundColor Green
    exit 0
}

# La primera lectura de eleventa la hace el servicio al arrancar: aquí se espera y se dice cómo salió.
$lectura = Consultar 'eleventa'
if (-not $lectura -or -not $lectura.activa) {
    $motivo = if ($lectura -and $lectura.error) { $lectura.error } else { 'la lectura esta apagada: en servicio.json, Eleventa:Activo va en true (con Servidor y Base)' }
    Write-Host "La clave quedo puesta, pero el servicio no lee eleventa: $motivo" -ForegroundColor Yellow
    exit 2
}
Write-Host "Esperando la lectura de eleventa ($($lectura.origen))..."
$limite = (Get-Date).AddSeconds($SegundosDeEspera)
while (-not $lectura.ultimaLocal -and (Get-Date) -lt $limite) {
    Start-Sleep -Seconds 2
    $nueva = Consultar 'eleventa'
    if ($nueva) { $lectura = $nueva }
}
if ($lectura.bien) {
    Write-Host "Listo: el servicio leyo eleventa con la clave ($($lectura.resultado))." -ForegroundColor Green
    exit 0
}
if (-not $lectura.ultimaLocal) {
    Write-Host "La clave quedo puesta, pero eleventa no contesto en $SegundosDeEspera s. Revisa en Edge http://localhost:$Puerto/api/eleventa en un rato (dice si la lectura salio bien), o corre el script otra vez." -ForegroundColor Yellow
    exit 2
}
Write-Host "La clave quedo puesta, pero la lectura de eleventa no salio bien: $($lectura.error)" -ForegroundColor Yellow
if ([string]$lectura.error -match 'usuario o la clave') { Write-Host 'Corre este script otra vez con la clave correcta.' -ForegroundColor Yellow }
exit 2
