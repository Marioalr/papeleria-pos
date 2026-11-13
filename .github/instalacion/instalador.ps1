<#
    Lo que comparten los pasos del trabajo «instalador-en-windows» de .github/workflows/prueba.yml: correr el instalador de Papeleria
    POS sin preguntar, como lo haria quien lo usa con /VERYSILENT (docs/instalacion.md, «Sin preguntar»), y revisar lo que deja. Cada
    paso carga antes .github\instalacion\ayudas.ps1 (Que, Estado, Esperar-Version, Marca, Correr…) y despues este:

        . .github\instalacion\ayudas.ps1
        . .github\instalacion\instalador.ps1

    Los pasos de «mudanza-en-windows» cargan ademas, despues de este, .github\instalacion\mudanza.ps1.

    Corre en PowerShell 7 (pwsh): su ConvertFrom-Json entiende los comentarios de servicio.json. El instalador, por dentro, usa Windows
    PowerShell 5.1, como en la tienda.
#>
$Instalador = @(Get-ChildItem -Path (Join-Path $env:GITHUB_WORKSPACE 'instalador') -Filter 'PapeleriaPos-Instalador-*.exe' -File)[0].FullName
$VersionDelInstalador = [regex]::Match((Split-Path -Leaf $Instalador), '^PapeleriaPos-Instalador-(.+)\.exe$').Groups[1].Value
$RegistrosDelInstalador = Join-Path $env:RUNNER_TEMP 'registros-del-instalador'
$CajaJson = Join-Path $Datos 'Caja\caja.json'
$Desinstalador = Join-Path $Programa 'unins000.exe'
$AccesoDelEscritorio = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Caja Papelería POS.lnk'
$ClaveDelServicio = 'HKLM:\SYSTEM\CurrentControlSet\Services\PapeleriaPos'
$ClaveDesinstalar = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{F7B6F872-3E72-4188-96CF-D93827097657}_is1'
$ResultadoDelInstalador = Join-Path $Datos 'instalador\resultado.txt'
# Los secretos de mentiras: se revisa que lleguen a las variables del servicio y a ningun archivo ni registro.
$ClaveFalsa = 'clave-falsa-del-instalador-5521'
$CodigoFalso = 'abcd-efgh-jklm-npqr'
$CodigoNormal = 'ABCDEFGHJKLMNPQR'

function Argumento([string]$texto) { '"' + $texto + '"' }

# El instalador sin preguntar, con sus respuestas como /CLAVE=valor y los secretos en las variables de entorno de su proceso (nunca en la
# linea de ordenes). Devuelve su codigo de salida y su registro (/LOG).
function Instalar([string]$nombre, [string[]]$parametros = @(), [hashtable]$secretos = @{}) {
    New-Item -ItemType Directory -Force -Path $RegistrosDelInstalador | Out-Null
    $registro = Join-Path $RegistrosDelInstalador "$nombre.log"
    $argumentos = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (Argumento "/LOG=$registro")) + @($parametros | ForEach-Object { Argumento $_ })
    Write-Host ">>> $(Split-Path -Leaf $Instalador) $($argumentos -join ' ')" -ForegroundColor Cyan
    foreach ($clave in $secretos.Keys) { [Environment]::SetEnvironmentVariable($clave, $secretos[$clave], 'Process') }
    try {
        $proceso = Start-Process -FilePath $Instalador -ArgumentList $argumentos -Wait -PassThru
    } finally {
        foreach ($clave in $secretos.Keys) { [Environment]::SetEnvironmentVariable($clave, $null, 'Process') }
    }
    Write-Host "<<< termino con $($proceso.ExitCode)" -ForegroundColor Cyan
    Ensenar-Lo-Ultimo
    [pscustomobject]@{ Codigo = $proceso.ExitCode; Registro = $registro }
}

# Desinstalar sin preguntar. El desinstalador se copia a la carpeta temporal y sigue desde ahi: se espera a que quite su registro.
function Desinstalar([string]$nombre, [string[]]$parametros = @()) {
    New-Item -ItemType Directory -Force -Path $RegistrosDelInstalador | Out-Null
    $registro = Join-Path $RegistrosDelInstalador "$nombre.log"
    $argumentos = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (Argumento "/LOG=$registro")) + @($parametros | ForEach-Object { Argumento $_ })
    Write-Host ">>> unins000.exe $($argumentos -join ' ')" -ForegroundColor Cyan
    Start-Process -FilePath $Desinstalador -ArgumentList $argumentos -Wait | Out-Null
    $limite = (Get-Date).AddMinutes(5)
    while (((Test-Path -LiteralPath $ClaveDesinstalar) -or @(Get-Process | Where-Object { $_.ProcessName -like '_iu*' }).Count -gt 0) -and (Get-Date) -lt $limite) {
        Start-Sleep -Seconds 2
    }
    Write-Host '<<< termino' -ForegroundColor Cyan
}

# Lo ultimo del registro de lo que hizo instalar-pc.ps1 (C:\ProgramData\PapeleriaPos\instalador): lo de cada script que corrio.
function Ensenar-Lo-Ultimo {
    $carpeta = Join-Path $Datos 'instalador'
    $ultimo = Get-ChildItem -LiteralPath $carpeta -Filter 'instalador-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($ultimo) {
        Write-Host "    ($($ultimo.Name), lo ultimo)"
        Get-Content -LiteralPath $ultimo.FullName -Encoding UTF8 -Tail 40 | ForEach-Object { Write-Host "    $_" }
    }
}

function Leer-Claves([string]$ruta) {
    $claves = @{}
    foreach ($renglon in [IO.File]::ReadAllLines($ruta, [Text.Encoding]::UTF8)) {
        $renglon = $renglon.TrimStart([char]0xFEFF)
        if (-not $renglon -or $renglon.StartsWith('#')) { continue }
        $igual = $renglon.IndexOf('=')
        if ($igual -gt 0) { $claves[$renglon.Substring(0, $igual)] = $renglon.Substring($igual + 1) }
    }
    $claves
}

# Como quedo, segun el instalador (C:\ProgramData\PapeleriaPos\instalador\resultado.txt).
function Resultado-Del-Instalador {
    Que (Test-Path -LiteralPath $ResultadoDelInstalador) "dejo su resultado en $ResultadoDelInstalador"
    $claves = Leer-Claves $ResultadoDelInstalador
    Write-Host ('    resultado: ' + (($claves.Keys | Sort-Object | ForEach-Object { "$_=$($claves[$_])" }) -join '; '))
    $claves
}

function Leer-Json([string]$ruta) { Get-Content -LiteralPath $ruta -Raw -Encoding UTF8 | ConvertFrom-Json }

function Variables-Del-Servicio { @((Get-ItemProperty -LiteralPath $ClaveDelServicio -Name Environment -ErrorAction SilentlyContinue).Environment) }

function Regla([string]$cual) {
    netsh advfirewall firewall show rule name="$cual" | Out-Null
    $esta = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    $esta
}

# Ningun secreto en lo que deja el instalador: su registro (/LOG), los de instalar-pc.ps1, su resultado, servicio.json, caja.json y los
# registros del servicio.
function Sin-Secretos([string[]]$secretos) {
    $archivos = @(Get-ChildItem -LiteralPath $RegistrosDelInstalador -File -ErrorAction SilentlyContinue) +
        @(Get-ChildItem -LiteralPath (Join-Path $Datos 'instalador') -File -ErrorAction SilentlyContinue) +
        @(Get-ChildItem -LiteralPath (Join-Path $Datos 'registros') -File -ErrorAction SilentlyContinue) +
        @(Get-Item -LiteralPath $ServicioJson, $CajaJson -ErrorAction SilentlyContinue)
    Que ($archivos.Count -gt 3) "se revisan $($archivos.Count) archivos"
    foreach ($archivo in $archivos) {
        $texto = [IO.File]::ReadAllText($archivo.FullName)
        foreach ($secreto in $secretos) {
            Que (-not $texto.Contains($secreto, [StringComparison]::OrdinalIgnoreCase)) "$($archivo.Name) no trae un secreto"
        }
    }
}

# Una cola de Windows para «la impresora que mide», si el CI tiene la cola de impresion; vacia si no se pudo.
function Cola-De-Prueba {
    try {
        $spooler = Get-Service -Name Spooler -ErrorAction Stop
        if ($spooler.Status -ne 'Running') { Start-Service -Name Spooler -ErrorAction Stop }
        $nombre = 'WF-6590 de prueba'
        if (-not (Get-Printer -Name $nombre -ErrorAction SilentlyContinue)) {
            $driver = @(Get-PrinterDriver -ErrorAction Stop | Where-Object { $_.Name -match 'Generic / Text Only|Print To PDF|XPS' })[0].Name
            if (-not $driver) { throw 'no hay un driver para la cola de prueba' }
            if (-not (Get-PrinterPort -Name 'IP_192.0.2.30' -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name 'IP_192.0.2.30' -PrinterHostAddress '192.0.2.30' }
            Add-Printer -Name $nombre -DriverName $driver -PortName 'IP_192.0.2.30'
        }
        $nombre
    } catch {
        Write-Host "::notice::Sin cola de impresion de prueba en este Windows ($($_.Exception.Message)): no se revisa «Conservar documentos impresos»."
        ''
    }
}
