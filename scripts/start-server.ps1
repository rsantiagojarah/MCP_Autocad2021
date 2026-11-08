param([switch]$Compatibility)
$ErrorActionPreference = 'Stop'
$variant = if ($Compatibility) { 'compatibility-net8' } else { 'official-net10' }
$assembly = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\$variant\server\CadMcp.Server.dll"
if (-not (Test-Path -LiteralPath $assembly)) { throw "No existe el servidor publicado. Ejecuta scripts/build.ps1 $(if ($Compatibility) { '-Offline' })." }
& dotnet $assembly
exit $LASTEXITCODE
