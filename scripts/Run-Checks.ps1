param(
    [switch]$Live,
    [switch]$WriteTest,
    [switch]$Benchmark,
    [switch]$CoreConsole,
    [string]$SessionId,
    [string]$SurfaceReferenceFile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot 'artifacts\test-results'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$summary = [ordered]@{ recordedUtc = [DateTime]::UtcNow.ToString('o'); windowsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name; checks = @() }
function Add-Check([string]$Name, [string]$Status, [string]$Detail) {
    $summary.checks += [ordered]@{ name=$Name; status=$Status; detail=$Detail }
    Write-Output "$Name : $Status - $Detail"
}
Push-Location -LiteralPath $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    try {
        & ./scripts/build.ps1 -Offline
        if ($LASTEXITCODE -ne 0) { throw 'La compilación o las pruebas devolvieron un error.' }
        Add-Check 'Compilación y pruebas .NET 8/net48' 'passed' 'Revisa core-tests.trx y los binarios publicados.'
    } catch { Add-Check 'Compilación y pruebas .NET 8/net48' 'failed' $_.Exception.Message }
    try {
        & python tests/integration_stdio.py
        if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba de protocolo MCP.' }
        Add-Check 'MCP stdio de compatibilidad' 'passed' 'Inicialización, catálogo de 71 herramientas, consultas y errores de protocolo.'
    } catch { Add-Check 'MCP stdio de compatibilidad' 'failed' $_.Exception.Message }
    $sdks = & dotnet --list-sdks
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        Add-Check 'Servidor oficial .NET 10' 'blocked' 'Falta un SDK .NET 10. Instálalo con tu usuario normal y vuelve a ejecutar este script con acceso a NuGet.'
    } else {
        try {
            & dotnet test tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release --nologo --logger 'trx;LogFileName=official-tests.trx' --results-directory $output
            if ($LASTEXITCODE -ne 0) { throw 'Falló la restauración, compilación o ejecución de las pruebas con el SDK oficial.' }
            & dotnet publish src/CadMcp.Server/CadMcp.Server.csproj -c Release --no-restore --self-contained false -o artifacts/official-net10/server --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del servidor oficial.' }
            Add-Check 'Servidor oficial .NET 10' 'partial' 'Compilado y pruebas del núcleo aprobadas; falta probar stdio y Autodesk con -ServerPath en Check-Live.ps1.'
        } catch { Add-Check 'Servidor oficial .NET 10' 'failed' $_.Exception.Message }
    }
    foreach ($project in @('CadMcp.AutoCAD.Smoke','CadMcp.Civil3D.Smoke')) {
        try {
            & dotnet build "tests/$project/$project.csproj" -c Release -p:ModernTargetFramework=net8.0 -p:UseInstalledFrameworkReferences=true --configfile NuGet.offline.config --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba nativa.' }
            Add-Check "Prueba nativa $project" 'prepared' "DLL compilada en tests/$project/bin/Release/net48; requiere ejecución dentro de Autodesk."
        } catch { Add-Check "Prueba nativa $project" 'failed' $_.Exception.Message }
    }
    if ($CoreConsole) {
        try {
            & ./scripts/test-coreconsole.ps1
            Add-Check 'AutoCAD Core Console' 'passed' 'Reporte nativo generado.'
        } catch { Add-Check 'AutoCAD Core Console' 'blocked' $_.Exception.Message }
    }
    if ($Live) {
        try {
            $liveOptions = @{}
            if ($WriteTest) { $liveOptions.AllowEmptyDrawingWriteTest = $true }
            if ($Benchmark) { $liveOptions.Benchmark = $true }
            if ($SessionId) { $liveOptions.SessionId = $SessionId }
            if ($SurfaceReferenceFile) { $liveOptions.SurfaceReferenceFile = $SurfaceReferenceFile }
            & ./scripts/Check-Live.ps1 @liveOptions
            $liveResult = Get-Content -LiteralPath (Join-Path $output 'live-check.json') -Raw | ConvertFrom-Json
            if (-not $liveResult.passed) { throw 'El reporte en vivo no está aprobado.' }
            Add-Check 'Autodesk por MCP' 'passed' 'Revisa live-check.json: se aprueban solo los casos ejecutados; pending indica los restantes.'
            foreach ($item in $liveResult.pending) { Add-Check 'Caso pendiente en Autodesk' 'pending' $item }
            if ($Benchmark) { Add-Check 'Rendimiento' 'measured' 'Tiempos y memoria guardados en live-check.json; interpretar según el dibujo y las muestras registradas.' }
        } catch { Add-Check 'Autodesk por MCP' 'blocked' $_.Exception.Message }
    } else { Add-Check 'Autodesk por MCP y rendimiento' 'pending' 'Ejecutar con -Live -WriteTest -Benchmark desde el mismo usuario de Autodesk y sobre un dibujo de ensayo vacío.' }
} finally {
    $summary.allRequestedChecksPassed = @($summary.checks | Where-Object { $_.status -ne 'passed' -and $_.status -ne 'measured' }).Count -eq 0
    $path = Join-Path $output 'verification-summary.json'
    $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Output "Resumen: $path"
    Pop-Location
}
