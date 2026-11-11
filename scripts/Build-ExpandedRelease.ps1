param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$currentProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
if ($currentProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText -eq '0.6.0') {
    & (Join-Path $PSScriptRoot 'Build-LineGraphicsRelease.ps1')
    return
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages' }
$dotnet = Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$release = Join-Path $projectRoot 'artifacts\expanded-0.4.0'
Push-Location -LiteralPath $projectRoot
try {
    & $dotnet build src/CadMcp.Civil3D/CadMcp.Civil3D.csproj -c Release -p:UseInstalledFrameworkReferences=true --configfile NuGet.offline.config --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación Autodesk 2021.' }
    foreach ($nativeCheck in @('tests/CadMcp.AutoCAD.Smoke/CadMcp.AutoCAD.Smoke.csproj','tests/CadMcp.Civil3D.Smoke/CadMcp.Civil3D.Smoke.csproj')) {
        & $dotnet build $nativeCheck -c Release -p:UseInstalledFrameworkReferences=true --configfile NuGet.offline.config --nologo
        if ($LASTEXITCODE -ne 0) { throw "Falló la preparación de la prueba nativa: $nativeCheck" }
    }
    & $dotnet test tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release -p:UseInstalledFrameworkReferences=true -p:RestoreConfigFile=NuGet.offline.config --nologo --logger 'trx;LogFileName=expanded-release.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas del MCP.' }
    & $dotnet publish src/CadMcp.Server/CadMcp.Server.csproj -c Release --no-restore --self-contained false -o (Join-Path $release 'server') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación oficial.' }
    $bundle = Join-Path $release 'CadMcp2021.bundle'
    $contents = Join-Path $bundle 'Contents\Windows'
    New-Item -ItemType Directory -Path $contents -Force | Out-Null
    Get-ChildItem -LiteralPath 'src\CadMcp.Civil3D\bin\Release\net48' -Filter '*.dll' -File | Copy-Item -Destination $contents -Force
    Copy-Item -LiteralPath 'packaging\CadMcp2021.bundle\PackageContents.xml' -Destination $bundle -Force
    $config = @{mcpServers=@{'autocad-civil3d-2021'=@{command=$dotnet;args=@((Join-Path $release 'server\CadMcp.Server.dll'))}}}
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $release 'mcp.config.json') -Encoding utf8
    Copy-Item -LiteralPath 'docs\FUNCIONALIDADES.md' -Destination $release -Force
    Copy-Item -LiteralPath 'docs\SECCIONES.md' -Destination $release -Force
    $testDir = Join-Path $release 'native-check'
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    Get-ChildItem -LiteralPath 'tests\CadMcp.Civil3D.Smoke\bin\Release\net48' -Filter '*.dll' -File | Copy-Item -Destination $testDir -Force
    $files = @(Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object Name -NE 'release-manifest.json' | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($release.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    [ordered]@{version='0.4.0';tools=71;builtUtc=[DateTime]::UtcNow.ToString('o');nativeValidation='pending_civil3d_reload';files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'Build-DesktopPlugin.ps1') -ServerRoot (Join-Path $release 'server')
    Write-Output "Paquete preparado: $release. La instalación activa no se modificó."
} finally { Pop-Location }
