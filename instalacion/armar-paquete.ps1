<#
    Arma el paquete para instalar o actualizar una PC de la tienda (docs/pruebas-en-tienda.md, «Actualizar la caja 2 del piloto»).
    Se corre en Windows, en el repositorio, con el SDK de .NET 10 (no hace falta administrador):

        powershell -ExecutionPolicy Bypass -File instalacion\armar-paquete.ps1 -Version 1.0.57
        powershell -ExecutionPolicy Bypass -File instalacion\armar-paquete.ps1 -Version 1.0.57 -Salida D:\PapeleriaPos-1.0.57

    Lo que deja en -Salida (por omisión, publicar\PapeleriaPos-<versión>), que se lleva así a la PC:

        actualizar.ps1   actualiza el servicio y la caja de esa PC, con respaldo y vuelta atrás (como administrador)
        servicio\        el servicio publicado para Windows de 64 bits, con .NET adentro, más instalar-servicio.ps1 (la primera
                         vez) y plantilla-servicio.json (la configuración de la PC, vacía)
        caja\            la caja publicada igual, más instalar-caja.ps1 (la primera vez)

    -Version  La que llevan los .exe, y la que compara actualizar.ps1 para no poner una anterior. En GitHub Actions, 1.0.<número de
              la corrida> (prueba.yml, el mismo número que el instalador de prueba). Sin -Version sale como 0.0.0-local
              (Directory.Build.props): más vieja que cualquiera del CI, así que actualizar.ps1 solo la pone con -Forzar.
    -Rapido   La caja sin precompilar (ReadyToRun), que tarda: solo para probar los scripts (instalacion.yml). El paquete de la
              tienda va siempre precompilado.
#>
param([string]$Version, [string]$Salida, [switch]$Rapido)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot

$esperada = '0.0.0-local'
if ($Version) {
    # Como la acepta .NET para el .exe: tres o cuatro números (de hasta 65534) y, si acaso, una etiqueta (-algo).
    if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(\.(\d+))?(-[0-9A-Za-z.-]+)?$') {
        throw "La version '$Version' no sirve: van tres o cuatro numeros con punto (1.0.57), y si acaso una etiqueta (1.0.57-prueba)."
    }
    foreach ($numero in @($Matches[1], $Matches[2], $Matches[3], $Matches[5])) {
        if ($numero -and [long]$numero -gt 65534) { throw "La version '$Version' no sirve: cada numero va hasta 65534." }
    }
    $esperada = $Version
}
if (-not $Salida) { $Salida = Join-Path $raiz "publicar\PapeleriaPos-$esperada" }
$Salida = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Salida)

$opciones = @('-c', 'Release', '-r', 'win-x64', '--self-contained')
if ($Version) { $opciones += "-p:Version=$Version" }
if ($Rapido) { $opciones += '-p:PublishReadyToRun=false' }

foreach ($parte in @(@{ Proyecto = 'src\PapeleriaPos.Servicio'; Carpeta = 'servicio' }, @{ Proyecto = 'src\PapeleriaPos.Caja'; Carpeta = 'caja' })) {
    $destino = Join-Path $Salida $parte.Carpeta
    if (Test-Path -LiteralPath $destino) { Remove-Item -LiteralPath $destino -Recurse -Force }
    Write-Host "Publicando $($parte.Proyecto) en $destino ..."
    & dotnet publish (Join-Path $raiz $parte.Proyecto) @opciones -o $destino
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish de $($parte.Proyecto) fallo (codigo $LASTEXITCODE)." }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'actualizar.ps1') -Destination $Salida -Force

# Cada .exe dice su versión (en sus propiedades de Windows; si no, su .dll): es lo que lee actualizar.ps1.
foreach ($exe in @('servicio\PapeleriaPos.Servicio.exe', 'caja\PapeleriaPos.Caja.exe')) {
    $ruta = Join-Path $Salida $exe
    if (-not (Test-Path -LiteralPath $ruta)) { throw "Falta $ruta en el paquete." }
    $dice = [string](Get-Item -LiteralPath $ruta).VersionInfo.ProductVersion
    if (-not $dice) { $dice = [string](Get-Item -LiteralPath ([IO.Path]::ChangeExtension($ruta, '.dll'))).VersionInfo.ProductVersion }
    if (($dice -split '\+')[0] -ne $esperada) { throw "$exe dice la version '$dice', y tenia que decir $esperada." }
}
Write-Host "Paquete $esperada listo en $Salida"
