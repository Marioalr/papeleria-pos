<#
    Lo que comparten los pasos de .github/workflows/instalacion.yml (la prueba de los scripts de instalacion/ en Windows): correr
    los scripts como en la tienda y revisar lo que dejan. Cada paso la carga al empezar:

        . .github\instalacion\ayudas.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Ayudas = $PSScriptRoot
$Paquetes = Join-Path $env:GITHUB_WORKSPACE 'paquetes'
$ProgramaQueTermina = Join-Path $Paquetes 'programa-que-termina.exe'
$Datos = Join-Path $env:ProgramData 'PapeleriaPos'
$Base = Join-Path $Datos 'pos.db'
$ServicioJson = Join-Path $Datos 'servicio.json'
$Actualizaciones = Join-Path $Datos 'actualizaciones'
$Programa = Join-Path $env:ProgramFiles 'PapeleriaPos'
$ExeServicio = Join-Path $Programa 'Servicio\PapeleriaPos.Servicio.exe'
$ExeCaja = Join-Path $Programa 'Caja\PapeleriaPos.Caja.exe'
$EstadoUrl = 'http://localhost:5080/api/estado'
# Entre pasos (cada uno es otra ventana de PowerShell): la huella de servicio.json y el primer respaldo.
$HuellaServicioJson = Join-Path $env:RUNNER_TEMP 'servicio-json.sha256'
$PrimerRespaldo = Join-Path $env:RUNNER_TEMP 'primer-respaldo.txt'

# Un script como en la tienda: otra ventana de Windows PowerShell con -ExecutionPolicy Bypass. Enseña todo lo que escribió y
# devuelve su código de salida y su texto.
function Correr([string]$script, [string[]]$argumentos = @()) {
    Write-Host ">>> $script $($argumentos -join ' ')" -ForegroundColor Cyan
    $antes = $ErrorActionPreference
    # Lo que escribe en la salida de errores no detiene este paso: se revisa el código.
    $ErrorActionPreference = 'Continue'
    try {
        $salida = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @argumentos 2>&1 | ForEach-Object { "$_" })
        $codigo = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $antes
    }
    foreach ($linea in $salida) { Write-Host "    $linea" }
    Write-Host "<<< salio con el codigo $codigo" -ForegroundColor Cyan
    # GitHub termina cada paso con el último código de salida: uno que se esperaba distinto de 0 no tumba el paso.
    $global:LASTEXITCODE = 0
    [pscustomobject]@{ Codigo = $codigo; Texto = ($salida -join "`n") }
}

function Que([bool]$condicion, [string]$que) {
    if ($condicion) {
        Write-Host "  bien: $que" -ForegroundColor Green
        return
    }
    Write-Host "::error::No se cumple: $que"
    throw "No se cumple: $que"
}

function Estado {
    try { Invoke-RestMethod -Uri $EstadoUrl -UseBasicParsing -TimeoutSec 10 } catch { $null }
}

# Hasta que api/estado conteste con esa versión; nada si no contesta a tiempo.
function Esperar-Version([string]$version, [int]$segundos = 120) {
    $limite = (Get-Date).AddSeconds($segundos)
    do {
        $estado = Estado
        if ($estado -and [string]$estado.version -eq $version) { return $estado }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $limite)
    Write-Host "api/estado no contesto con la $version en $segundos s (lo ultimo: $(if ($estado) { $estado | ConvertTo-Json -Compress } else { 'no contesto' }))."
    $null
}

# Como la lee actualizar.ps1: la del .exe y, si no trae, la de su .dll.
function Version-De([string]$exe) {
    $dice = [string](Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    $dll = [IO.Path]::ChangeExtension($exe, '.dll')
    if (-not $dice -and (Test-Path -LiteralPath $dll)) { $dice = [string](Get-Item -LiteralPath $dll).VersionInfo.ProductVersion }
    ($dice -split '\+')[0]
}

function Ruta-Del-Servicio { [string](Get-CimInstance -ClassName Win32_Service -Filter "Name='PapeleriaPos'").PathName }

function Respaldos { @(Get-ChildItem -LiteralPath $Actualizaciones -Directory -ErrorAction SilentlyContinue | Sort-Object Name) }

# La marca en la base (marca.py): «poner» o «revisar», con su texto (sin él, la de siempre). Verdadero si salió bien.
function Marca([string]$que, [string]$texto) {
    $argumentos = @((Join-Path $Ayudas 'marca.py'), $que, $Base)
    if ($texto) { $argumentos += $texto }
    # Lo que escribe marca.py va a la pantalla, no a lo que devuelve la función (si no, devolvería ese texto y el booleano).
    & python @argumentos | ForEach-Object { Write-Host "    $_" }
    $bien = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    $bien
}

# Una llamada a la API del servicio, en JSON (desde esta PC no hace falta la llave de una caja).
function Api([string]$ruta, $cuerpo = $null) {
    $parametros = @{ Uri = "http://localhost:5080/api/$ruta"; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($null -ne $cuerpo) {
        $parametros.Method = 'Post'
        $parametros.ContentType = 'application/json; charset=utf-8'
        $parametros.Body = [Text.Encoding]::UTF8.GetBytes(($cuerpo | ConvertTo-Json -Compress))
    }
    Invoke-RestMethod @parametros
}

function Proceso-Del-Servicio { [int](Get-CimInstance -ClassName Win32_Service -Filter "Name='PapeleriaPos'").ProcessId }

function Carpetas-De-Antes { @(Get-ChildItem -LiteralPath $Datos -Directory -Filter 'antes-de-restaurar-*' -ErrorAction SilentlyContinue) }

function Huella-De-ServicioJson { (Get-FileHash -LiteralPath $ServicioJson -Algorithm SHA256).Hash }

# Una caja abierta de mentiras: un proceso que se llama como la caja y corre desde su carpeta de Archivos de programa (la tiene
# abierta, como la de verdad). Termina sola a los $segundos.
function Abrir-Caja-De-Mentiras([int]$segundos) {
    $carpeta = Join-Path $Programa 'Caja\de-mentiras'
    New-Item -ItemType Directory -Force -Path $carpeta | Out-Null
    $exe = Join-Path $carpeta 'PapeleriaPos.Caja.exe'
    Copy-Item -LiteralPath $ProgramaQueTermina -Destination $exe -Force
    $proceso = Start-Process -FilePath $exe -ArgumentList "$segundos" -PassThru -WindowStyle Hidden
    # En Windows PowerShell, sin pedir antes su Handle, ExitCode sale vacío al terminar.
    $null = $proceso.Handle
    $proceso
}

# Si está la regla del firewall de las cajas que pone instalar-servicio.ps1.
function Regla-Del-Firewall {
    netsh advfirewall firewall show rule name='Papeleria POS - cajas' | Out-Null
    $esta = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    $esta
}

function Esperar-Sin-Servicio {
    $limite = (Get-Date).AddSeconds(60)
    while ((Get-Service -Name PapeleriaPos -ErrorAction SilentlyContinue) -and (Get-Date) -lt $limite) { Start-Sleep -Seconds 1 }
    -not (Get-Service -Name PapeleriaPos -ErrorAction SilentlyContinue)
}
