param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages' }
$dotnet = Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -eq '0.6.0') {
    & (Join-Path $PSScriptRoot 'Build-LineGraphicsRelease.ps1') -SkipTests:$SkipTests
    return
}
$release = Join-Path $projectRoot "artifacts\expanded-$version"
Push-Location -LiteralPath $projectRoot
try {
    foreach ($project in @('src/CadMcp.Civil3D/CadMcp.Civil3D.csproj','tests/CadMcp.TextHatch.Native/CadMcp.TextHatch.Native.csproj')) {
        & $dotnet build $project -c Release -m:1 -nodeReuse:false -p:UseInstalledFrameworkReferences=true -p:UseSharedCompilation=false --configfile NuGet.offline.config --nologo
        if ($LASTEXITCODE -ne 0) { throw "Falló la compilación: $project" }
    }
    & $dotnet build tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release -m:1 -nodeReuse:false -p:UseInstalledFrameworkReferences=true -p:UseSharedCompilation=false -p:RestoreConfigFile=NuGet.offline.config --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de servidor y pruebas.' }
    if (-not $SkipTests) {
        & $dotnet test tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release --no-build --no-restore -m:1 -nodeReuse:false --nologo --logger 'trx;LogFileName=text-hatch.trx' --results-directory artifacts/test-results/text-hatch
        if ($LASTEXITCODE -ne 0) { throw 'Falló la suite automática.' }
    }
    & $dotnet publish src/CadMcp.Server/CadMcp.Server.csproj -c Release --no-build --no-restore --self-contained false -m:1 -nodeReuse:false -o (Join-Path $release 'server') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación MCP.' }
    & python tests/integration_sdk_stdio.py $dotnet (Join-Path $release 'server\CadMcp.Server.dll') --report artifacts/test-results/text-hatch/official-stdio.json
    if ($LASTEXITCODE -ne 0) { throw 'Falló el protocolo MCP.' }
    $compatibilityHost = (Get-Command dotnet -ErrorAction Stop).Source
    & $compatibilityHost publish src/CadMcp.Server/CadMcp.Server.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:ModernTargetFramework=net8.0 -p:UseOfficialMcpSdk=false -p:UseInstalledFrameworkReferences=true --configfile NuGet.offline.config --self-contained false -o (Join-Path $release 'compatibility-server') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló el adaptador de compatibilidad. Requiere un SDK .NET 8 instalado o sus paquetes de referencia.' }
    & python tests/integration_sdk_stdio.py $compatibilityHost (Join-Path $release 'compatibility-server\CadMcp.Server.dll') --report artifacts/test-results/text-hatch/compatibility-stdio.json
    if ($LASTEXITCODE -ne 0) { throw 'Falló stdio de compatibilidad.' }
    $content = Join-Path $release 'CadMcp2021.bundle\Contents\Windows'
    New-Item -ItemType Directory -Path $content -Force | Out-Null
    Get-ChildItem -LiteralPath 'src\CadMcp.Civil3D\bin\Release\net48' -Filter '*.dll' -File | Copy-Item -Destination $content -Force
    Copy-Item -LiteralPath 'packaging\CadMcp2021.bundle\PackageContents.xml' -Destination (Join-Path $release 'CadMcp2021.bundle') -Force
    $native = Join-Path $release 'native-check'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach ($name in @('CadMcp.TextHatch.NativeV2.dll','Newtonsoft.Json.dll')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "tests\CadMcp.TextHatch.Native\bin\Release\net48\$name") -Destination $native -Force
    }
    $previousNativeDll = Join-Path $native 'CadMcp.TextHatch.Native.dll'
    if (Test-Path -LiteralPath $previousNativeDll) { Remove-Item -LiteralPath $previousNativeDll }
    $verification = Join-Path $release 'verification'
    New-Item -ItemType Directory -Path $verification -Force | Out-Null
    foreach ($report in @('text-hatch.trx','official-stdio.json','compatibility-stdio.json')) {
        $path = Join-Path $projectRoot "artifacts\test-results\text-hatch\$report"
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $verification -Force }
    }
    $nativeStatus = 'pending_user_load_and_execution'
    $nativeReport = Join-Path $projectRoot 'tests\CadMcp.TextHatch.Native\bin\Release\net48\text-hatch-native.json'
    if (Test-Path -LiteralPath $nativeReport) {
        $result = Get-Content -LiteralPath $nativeReport -Raw | ConvertFrom-Json
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $native 'CadMcp.TextHatch.NativeV2.dll')).Hash
        if ($result.assemblySha256 -eq $expectedHash) {
            $nativeStatus = if ($result.passed -and -not $result.nativeAreaPending) { 'passed' } elseif ($result.passed) { 'pending_area_verification' } else { 'failed' }
            Copy-Item -LiteralPath $nativeReport -Destination $verification -Force
            if ($result.passed -and (Test-Path -LiteralPath $result.drawing)) { Copy-Item -LiteralPath $result.drawing -Destination $verification -Force }
        }
    }
    $liveReport = Join-Path $projectRoot 'artifacts\test-results\text-hatch\live-native-mcp-complete.json'
    if (Test-Path -LiteralPath $liveReport) {
        $live = Get-Content -LiteralPath $liveReport -Raw | ConvertFrom-Json
        $bridgeHash = (Get-FileHash -LiteralPath (Join-Path $content 'CadMcp.AutoCAD.dll')).Hash
        if ($live.passed -and $live.installedBridgeMatchesRelease -and $live.installedBridgeSha256 -eq $bridgeHash) {
            $nativeStatus = 'passed_live_production_mcp'
            Copy-Item -LiteralPath $liveReport -Destination $verification -Force
        }
    }
    $unitResult = Join-Path $verification 'text-hatch.trx'
    $unitPassed = 0
    if (Test-Path -LiteralPath $unitResult) {
        [xml]$trx = Get-Content -LiteralPath $unitResult -Raw
        $unitPassed = [int]$trx.SelectSingleNode("//*[local-name()='Counters']").passed
    }
    [ordered]@{version=$version;tools=80;unitTestsPassed=$unitPassed;officialStdioPassed=$true;compatibilityStdioPassed=$true;nativeValidation=$nativeStatus;activeInstallationUpdated=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $verification 'summary.json') -Encoding utf8
    Copy-Item -LiteralPath 'docs\TEXTO_HATCH.md' -Destination $release -Force
    $config = @{mcpServers=@{'autocad-civil3d-2021'=@{command=$dotnet;args=@((Join-Path $release 'server\CadMcp.Server.dll'))}}}
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $release 'mcp.config.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'Build-DesktopPlugin.ps1') -ServerRoot (Join-Path $release 'server')
    if ($LASTEXITCODE -ne 0) { throw 'Falló el paquete de escritorio.' }
    $desktop = Join-Path $release 'desktop-plugin'
    New-Item -ItemType Directory -Path $desktop -Force | Out-Null
    Get-ChildItem -LiteralPath 'desktop-marketplace\plugins\autocad-civil3d-2021-net10' -Force | Copy-Item -Destination $desktop -Recurse -Force
    $files = @(Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object Name -NE 'release-manifest.json' | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($release.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    [ordered]@{version=$version;tools=80;builtUtc=[DateTime]::UtcNow.ToString('o');unitTestsExecuted=(-not $SkipTests);nativeValidation=$nativeStatus;files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Encoding utf8
    Compress-Archive -LiteralPath $release -DestinationPath (Join-Path $projectRoot "artifacts\CadMcp2021-$version.zip") -Force
    Write-Output "Complemento preparado: $release. No se modificó la instalación activa."
} finally { Pop-Location }
