param([string]$AutoCADDir = 'C:\Program Files\Autodesk\AutoCAD 2021', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    if (-not $SkipBuild) {
        & (Join-Path $projectRoot '.tools\dotnet10\dotnet.exe') build tests/CadMcp.AutoCAD.Smoke/CadMcp.AutoCAD.Smoke.csproj -c Release -m:1 -nodeReuse:false -p:UseInstalledFrameworkReferences=true -p:UseSharedCompilation=false --configfile NuGet.offline.config --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de la prueba nativa.' }
    }
    $output = Join-Path $projectRoot ('artifacts\text-hatch-native\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $plugin = Join-Path $projectRoot 'tests\CadMcp.AutoCAD.Smoke\bin\Release\net48\CadMcp.AutoCAD.Smoke.dll'
    $reportPath = Join-Path $output 'text-hatch-native.json'
    $scriptPath = Join-Path $output 'test.scr'
    @('_NETLOAD',('"'+$plugin.Replace('\','/')+'"'),'CADMCPTEXTHATCHCHECK','CADMCPTEXTHATCHVERIFY','_QUIT','_N','') | Set-Content -LiteralPath $scriptPath -Encoding ascii
    # A separate Core Console process gets a fresh drawing. No running Autodesk process is addressed.
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $AutoCADDir 'accoreconsole.exe'
    $info.Arguments = '/s "'+$scriptPath+'" /l en-US'
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.EnvironmentVariables['CADMCP_TEXT_HATCH_REPORT']=$reportPath
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.StandardOutputEncoding=[Text.Encoding]::Unicode
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    [void]$process.Start()
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(55000)) { $process.Kill(); throw "La instancia de ensayo excedió 55 segundos. Carpeta: $output" }
    $exitCode=$process.ExitCode; $process.Dispose()
    ($stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath (Join-Path $output 'coreconsole.log') -Encoding utf8
    if (-not (Test-Path -LiteralPath $reportPath)) { throw "Core Console no ejecutó la prueba (salida $exitCode). Consulta $output\coreconsole.log" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw "Prueba nativa fallida: $($report.error). Reporte: $reportPath" }
    Write-Output "Prueba nativa aprobada: $reportPath"
    Write-Output "Dibujo de ensayo: $($report.drawing)"
} finally { Pop-Location }
