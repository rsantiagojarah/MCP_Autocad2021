param(
    [switch]$Offline,
    [string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2021'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    $modern = if ($Offline) { 'net8.0' } else { 'net10.0' }
    $props = @("-p:ModernTargetFramework=$modern", "-p:AutoCADDir=$AutoCADDir")
    $config = 'NuGet.config'
    if ($Offline) {
        $props += '-p:UseInstalledFrameworkReferences=true'
        $props += '-p:UseOfficialMcpSdk=false'
        $config = 'NuGet.offline.config'
    }
    & dotnet build src/CadMcp.Civil3D/CadMcp.Civil3D.csproj -c Release @props --configfile $config --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de los plugins.' }
    & dotnet test tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release @props "-p:RestoreConfigFile=$config" --nologo --logger 'trx;LogFileName=core-tests.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas.' }
    $variant = if ($Offline) { 'compatibility-net8' } else { 'official-net10' }
    $outputRoot = Join-Path $projectRoot "artifacts\$variant"
    $serverOutput = Join-Path $outputRoot 'server'
    & dotnet publish src/CadMcp.Server/CadMcp.Server.csproj -c Release @props --no-restore --self-contained false -o $serverOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del servidor.' }
    $bundle = Join-Path $outputRoot 'CadMcp2021.bundle'
    $content = Join-Path $bundle 'Contents\Windows'
    New-Item -ItemType Directory -Path $content -Force | Out-Null
    $updates = @()
    foreach ($sourceDll in (Get-ChildItem -LiteralPath 'src\CadMcp.Civil3D\bin\Release\net48' -File -Filter '*.dll')) {
        $targetDll = Join-Path $content $sourceDll.Name
        if ((Test-Path -LiteralPath $targetDll) -and (Get-FileHash -LiteralPath $sourceDll.FullName).Hash -eq (Get-FileHash -LiteralPath $targetDll).Hash) { continue }
        if (Test-Path -LiteralPath $targetDll) {
            try {
                $lockProbe = [IO.File]::Open($targetDll, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $lockProbe.Dispose()
            } catch { throw "La DLL $targetDll requiere actualización y está en uso. Cierra Autodesk antes de actualizar el paquete. Los archivos idénticos se conservan sin sobrescribir." }
        }
        $updates += [pscustomobject]@{ Source = $sourceDll.FullName; Target = $targetDll }
    }
    foreach ($update in $updates) { Copy-Item -LiteralPath $update.Source -Destination $update.Target -Force }
    Copy-Item -LiteralPath 'packaging\CadMcp2021.bundle\PackageContents.xml' -Destination $bundle -Force
    $configObject = @{ mcpServers = @{ 'autocad-civil3d-2021' = @{ command = 'dotnet'; args = @((Join-Path $serverOutput 'CadMcp.Server.dll')) } } }
    $configObject | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'mcp.config.json') -Encoding utf8
    Copy-Item -LiteralPath 'README.md' -Destination $outputRoot -Force
    $artifactManifest = @{ version = '0.1.0'; variant = $variant; framework = $modern; officialSdk = -not $Offline; builtUtc = [DateTime]::UtcNow.ToString('o') }
    $artifactManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'build.json') -Encoding utf8
    Write-Output "Paquete generado: $outputRoot"
} finally { Pop-Location }
