param([switch]$CheckConnectionOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($identity -match '\\CodexSandbox') {
    throw 'Ejecuta este script desde tu PowerShell normal. El entorno aislado no puede descargar el SDK ni los paquetes NuGet.'
}
$output = Join-Path $projectRoot 'artifacts\test-results'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$report = [ordered]@{ recordedUtc=[DateTime]::UtcNow.ToString('o'); windowsUser=$identity; passed=$false; stage='sdk' }
Push-Location -LiteralPath $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    $sdkList = & dotnet --list-sdks
    if ($sdkList | Where-Object { $_ -match '^10\.' }) {
        $sdkHost = (Get-Command dotnet -ErrorAction Stop).Source
    } else {
        $sdkDirectory = Join-Path $projectRoot '.tools\dotnet10'
        $sdkHost = Join-Path $sdkDirectory 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $sdkHost)) {
            New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
            $installer = Join-Path $projectRoot '.tools\dotnet-install.ps1'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -TimeoutSec 60
            & $installer -Channel '10.0' -InstallDir $sdkDirectory -NoPath
            if ($LASTEXITCODE -ne 0) { throw 'Falló la instalación local del SDK .NET 10.' }
        }
    }
    $report.dotnet = $sdkHost
    $report.sdkVersion = (& $sdkHost --version | Out-String).Trim()
    if ($report.sdkVersion -notmatch '^10\.') { throw 'El ejecutable seleccionado no está usando un SDK .NET 10.' }
    $report.stage = 'nuget-connection'
    $connectionChecks = [System.Collections.Generic.List[object]]::new()
    $report.nugetConnection = $connectionChecks
    $selectedSource = $null
    # Both URLs belong to NuGet.org. V2 is a legacy fallback when api.nuget.org
    # cannot be resolved; package downloads still need globalcdn.nuget.org.
    $sources = @('https://api.nuget.org/v3/index.json', 'https://www.nuget.org/api/v2/')
    foreach ($source in $sources) {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $check = [ordered]@{source=$source; attempt=$attempt; reachable=$false}
            try {
                $sourceHost = ([Uri]$source).DnsSafeHost
                $check.addresses = @([Net.Dns]::GetHostAddresses($sourceHost) | ForEach-Object IPAddressToString)
                if ($source -like '*/api/v2/') {
                    $check.downloadHostAddresses = @([Net.Dns]::GetHostAddresses('globalcdn.nuget.org') | ForEach-Object IPAddressToString)
                }
                $response = Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 12 -ErrorAction Stop
                if ($source -like '*/v3/index.json') {
                    $index = $response.Content | ConvertFrom-Json
                    if (-not $index.resources) { throw 'El servidor no devolvió un índice NuGet válido.' }
                }
                $check.statusCode = [int]$response.StatusCode
                $check.reachable = $true
                $selectedSource = $source
            } catch {
                $check.error = $_.Exception.Message
                Write-Warning "NuGet: no se pudo consultar $source (intento $attempt): $($check.error)"
            }
            $connectionChecks.Add([pscustomobject]$check)
            if ($selectedSource) { break }
            if ($attempt -lt 2) { Start-Sleep -Seconds 1 }
        }
        if ($selectedSource) { break }
    }
    if (-not $selectedSource) {
        throw 'El SDK .NET 10 está instalado, pero no hay conexión con los servidores de NuGet. Revisa los errores DNS/HTTPS en artifacts/test-results/official-preparation.json. Prueba otra conexión de Internet o revisa el DNS/proxy de esta red y vuelve a ejecutar el script.'
    }
    $report.nugetSource = $selectedSource
    Write-Output "Origen NuGet disponible: $selectedSource"
    if ($CheckConnectionOnly) {
        $report.connectionPassed = $true
        $report.stage = 'connection-checked'
        Write-Output 'Conexión comprobada. Ejecuta el script sin -CheckConnectionOnly para compilar y probar.'
        return
    }
    $report.stage = 'restore'
    & $sdkHost restore tests/CadMcp.Tests/CadMcp.Tests.csproj --source $selectedSource --disable-parallel --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la descarga de dependencias NuGet. El SDK está instalado; la compilación aún no se ha ejecutado.' }
    $report.stage = 'build-tests'
    & $sdkHost test tests/CadMcp.Tests/CadMcp.Tests.csproj -c Release --no-restore --nologo --logger 'trx;LogFileName=official-tests.trx' --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación o ejecución de pruebas con el SDK oficial MCP. Revisa la salida de dotnet y official-tests.trx.' }
    $report.stage = 'publish'
    $serverOutput = Join-Path $projectRoot 'artifacts\official-net10\server'
    & $sdkHost publish src/CadMcp.Server/CadMcp.Server.csproj -c Release --no-restore --self-contained false -o $serverOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del servidor oficial.' }
    $serverAssembly = Join-Path $serverOutput 'CadMcp.Server.dll'
    $config = @{mcpServers=@{'autocad-civil3d-2021'=@{command=$sdkHost;args=@($serverAssembly)}}}
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $serverOutput) 'mcp.config.json') -Encoding utf8
    $report.stage = 'mcp-and-live-read'
    & python tests/integration_sdk_stdio.py $sdkHost $serverAssembly --require-live --report artifacts/test-results/official-stdio-integration.json
    if ($LASTEXITCODE -ne 0) { throw 'Falló la prueba MCP del servidor oficial. Revisa official-stdio-integration.json y su log.' }
    $report.passed = $true
    $report.stage = 'complete'
    Write-Output 'Servidor oficial compilado, pruebas aprobadas y consulta en vivo comprobada. La instalación activa sigue usando su paquete actual; este script no la reemplaza.'
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'official-preparation.json') -Encoding utf8
    Pop-Location
}
