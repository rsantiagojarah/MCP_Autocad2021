param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools\cli'
if(-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES=Join-Path $env:USERPROFILE '.nuget\packages' }
$dotnet=Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'
if(-not (Test-Path -LiteralPath $dotnet)) { $dotnet=(Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT=Split-Path -Parent $dotnet
[xml]$props=Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version=$props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if($version -ne '0.6.0') { throw 'Este empaquetador verifica la versión 0.6.0 y su catálogo de 89 herramientas.' }
$release=Join-Path $projectRoot "artifacts\expanded-$version"
Push-Location -LiteralPath $projectRoot
try {
    foreach($project in @('src/CadMcp.Civil3D/CadMcp.Civil3D.csproj','tests/CadMcp.Tests/CadMcp.Tests.csproj')) {
        & $dotnet build $project -c Release -m:1 -nodeReuse:false -p:UseInstalledFrameworkReferences=true -p:UseSharedCompilation=false -p:RestoreConfigFile=NuGet.offline.config --nologo
        if($LASTEXITCODE -ne 0) { throw "Falló la compilación de $project" }
    }
    # The test DLL may still be loaded in Autodesk. Build an identical copy into a separate
    # output directory, never overwrite the loaded DLL or ask Autodesk to release it.
    $nativeSource=Join-Path $projectRoot 'artifacts\native-builds\line-graphics'
    & $dotnet build tests/CadMcp.LineGraphics.Native/CadMcp.LineGraphics.Native.csproj -c Release -m:1 -nodeReuse:false -p:UseInstalledFrameworkReferences=true -p:UseSharedCompilation=false -p:RestoreConfigFile=NuGet.offline.config -o $nativeSource --nologo
    if($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba nativa.' }
    $results=Join-Path $projectRoot 'artifacts\test-results\line-graphics'
    New-Item -ItemType Directory -Path $results -Force | Out-Null
    if(-not $SkipTests) {
        & $dotnet test tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release --no-build --no-restore -m:1 -nodeReuse:false --nologo --logger 'trx;LogFileName=line-graphics.trx' --results-directory $results
        if($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas automáticas.' }
    }
    & $dotnet publish src/CadMcp.Server/CadMcp.Server.csproj -c Release --no-build --no-restore --self-contained false -m:1 -nodeReuse:false -o (Join-Path $release 'server') --nologo
    if($LASTEXITCODE -ne 0) { throw 'Falló la publicación oficial.' }
    & python tests/integration_sdk_stdio.py $dotnet (Join-Path $release 'server\CadMcp.Server.dll') --report (Join-Path $results 'official-stdio.json')
    if($LASTEXITCODE -ne 0) { throw 'Falló el protocolo oficial.' }
    $compatibilityHost=(Get-Command dotnet -ErrorAction Stop).Source
    & $compatibilityHost publish src/CadMcp.Server/CadMcp.Server.csproj -c Release -m:1 -nodeReuse:false -p:ModernTargetFramework=net8.0 -p:UseOfficialMcpSdk=false -p:UseInstalledFrameworkReferences=true -p:UseSharedCompilation=false --configfile NuGet.offline.config --self-contained false -o (Join-Path $release 'compatibility-server') --nologo
    if($LASTEXITCODE -ne 0) { throw 'Falló la publicación de compatibilidad (.NET 8).' }
    & python tests/integration_sdk_stdio.py $compatibilityHost (Join-Path $release 'compatibility-server\CadMcp.Server.dll') --report (Join-Path $results 'compatibility-stdio.json')
    if($LASTEXITCODE -ne 0) { throw 'Falló stdio de compatibilidad.' }
    $content=Join-Path $release 'CadMcp2021.bundle\Contents\Windows'
    $native=Join-Path $release 'native-check'
    $verification=Join-Path $release 'verification'
    New-Item -ItemType Directory -Path $content,$native,$verification -Force | Out-Null
    Get-ChildItem -LiteralPath 'src\CadMcp.Civil3D\bin\Release\net48' -Filter '*.dll' -File | Copy-Item -Destination $content -Force
    Copy-Item -LiteralPath 'packaging\CadMcp2021.bundle\PackageContents.xml' -Destination (Join-Path $release 'CadMcp2021.bundle') -Force
    foreach($name in @('CadMcp.LineGraphics.Native.dll','Newtonsoft.Json.dll')) { Copy-Item -LiteralPath (Join-Path $nativeSource $name) -Destination $native -Force }
    foreach($name in @('line-graphics.trx','official-stdio.json','compatibility-stdio.json')) {
        $path=Join-Path $results $name
        if(Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $verification -Force }
    }
    $nativeStatus='pending_autocad_execution'; $nativeChecks=0
    $nativeReport=Join-Path $projectRoot 'tests\CadMcp.LineGraphics.Native\bin\Release\net48\line-graphics-native.json'
    if(Test-Path -LiteralPath $nativeReport) {
        $nativeResult=Get-Content -LiteralPath $nativeReport -Raw | ConvertFrom-Json
        $nativeHash=(Get-FileHash -LiteralPath (Join-Path $native 'CadMcp.LineGraphics.Native.dll')).Hash
        if($nativeResult.assemblySha256 -eq $nativeHash) {
            $nativeStatus=if($nativeResult.passed) { 'passed_in_autocad_linked_production_sources' } else { 'failed' }
            $nativeChecks=$nativeResult.checks.Count
            Copy-Item -LiteralPath $nativeReport -Destination $verification -Force
        }
    }
    $unitPassed=0; $trxPath=Join-Path $verification 'line-graphics.trx'
    if(Test-Path -LiteralPath $trxPath) { [xml]$trx=Get-Content -LiteralPath $trxPath -Raw; $unitPassed=[int]$trx.SelectSingleNode("//*[local-name()='Counters']").passed }
    [ordered]@{version=$version;tools=89;unitTestsPassed=$unitPassed;officialStdioPassed=$true;compatibilityStdioPassed=$true;nativeValidation=$nativeStatus;nativeChecks=$nativeChecks;activeInstallationUpdated=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $verification 'summary.json') -Encoding utf8
    Copy-Item -LiteralPath 'docs\LINEAS.md' -Destination $release -Force
    Copy-Item -LiteralPath 'docs\TEXTO_HATCH.md' -Destination $release -Force
    @{mcpServers=@{'autocad-civil3d-2021'=@{command=$dotnet;args=@((Join-Path $release 'server\CadMcp.Server.dll'))}}} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $release 'mcp.config.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'Build-DesktopPlugin.ps1') -ServerRoot (Join-Path $release 'server')
    if($LASTEXITCODE -ne 0) { throw 'Falló el complemento de escritorio.' }
    $desktop=Join-Path $release 'desktop-plugin'; New-Item -ItemType Directory -Path $desktop -Force | Out-Null
    Get-ChildItem -LiteralPath 'desktop-marketplace\plugins\autocad-civil3d-2021-net10' -Force | Copy-Item -Destination $desktop -Recurse -Force
    $files=@(Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object Name -NE 'release-manifest.json' | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($release.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    [ordered]@{version=$version;tools=89;builtUtc=[DateTime]::UtcNow.ToString('o');unitTestsExecuted=(-not $SkipTests);nativeValidation=$nativeStatus;files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Encoding utf8
    Compress-Archive -LiteralPath $release -DestinationPath (Join-Path $projectRoot "artifacts\CadMcp2021-$version.zip") -Force
    Write-Output "Complemento preparado: $release. No se actualizó la instalación activa ni se cerraron dibujos."
} finally { Pop-Location }
