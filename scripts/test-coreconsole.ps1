param([string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2021')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    & dotnet build tests/CadMcp.AutoCAD.Smoke/CadMcp.AutoCAD.Smoke.csproj -c Release -p:ModernTargetFramework=net8.0 -p:UseInstalledFrameworkReferences=true "-p:AutoCADDir=$AutoCADDir" --configfile NuGet.offline.config --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba AutoCAD.' }
    $output = Join-Path $projectRoot 'artifacts\coreconsole'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $plugin = Join-Path $projectRoot 'tests\CadMcp.AutoCAD.Smoke\bin\Release\net48\CadMcp.AutoCAD.Smoke.dll'
    $env:CADMCP_SMOKE_REPORT = Join-Path $output ('report-' + [Guid]::NewGuid().ToString('N') + '.json')
    # Does not change SECURELOAD or TRUSTEDPATHS. Core Console receives a fresh drawing.
    $scriptFile = Join-Path $output 'smoke.scr'
    @('_NETLOAD', ('"' + $plugin.Replace('\','/') + '"'), 'CADMCPSMOKE', '_QUIT', '_N', '') | Set-Content -LiteralPath $scriptFile -Encoding ascii
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $AutoCADDir 'accoreconsole.exe'
    $startInfo.Arguments = '/s "' + $scriptFile + '" /l en-US'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Core Console superó el límite de 60 segundos.' }
    $log = $stdoutTask.GetAwaiter().GetResult() + $stderrTask.GetAwaiter().GetResult()
    $process.Dispose()
    $log | Set-Content -LiteralPath (Join-Path $output 'coreconsole.log') -Encoding utf8
    if (-not (Test-Path -LiteralPath $env:CADMCP_SMOKE_REPORT)) { throw "Core Console no generó un reporte. Consulta artifacts/coreconsole/coreconsole.log. $log" }
    $report = Get-Content -LiteralPath $env:CADMCP_SMOKE_REPORT -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw $report.error }
    Write-Output "Prueba AutoCAD correcta: $env:CADMCP_SMOKE_REPORT"
} finally { Pop-Location }
