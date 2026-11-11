param([switch]$BridgeOnly, [switch]$CheckOnly, [string]$ReleasePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = if ($ReleasePath) { [IO.Path]::GetFullPath($ReleasePath) } else { Join-Path $projectRoot 'artifacts\expanded-0.4.0' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if (-not $CheckOnly -and $identity -match '\\CodexSandbox') { throw 'Ejecuta este instalador desde tu PowerShell normal, con el mismo usuario que Civil 3D.' }
$manifest = Get-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Raw | ConvertFrom-Json
if (-not (($manifest.version -eq '0.4.0' -and $manifest.tools -eq 71) -or ($manifest.version -eq '0.5.0' -and $manifest.tools -eq 80) -or ($manifest.version -eq '0.6.0' -and $manifest.tools -eq 89))) { throw 'Manifiesto de versión inesperado.' }
foreach ($file in $manifest.files) {
    $source = [IO.Path]::GetFullPath((Join-Path $release $file.path))
    if (-not $source.StartsWith($release.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta fuera del paquete.' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw "Archivo de paquete alterado: $($file.path)" }
}
if ($CheckOnly) {
    [ordered]@{version=$manifest.version;tools=$manifest.tools;verifiedFiles=$manifest.files.Count;windowsUser=$identity;autodeskRunning=[bool](Get-Process -Name acad,accoreconsole -ErrorAction SilentlyContinue);writesPerformed=$false;normalWindowsUserRequired=($identity -match '\\CodexSandbox')} | ConvertTo-Json
    return
}
if (Get-Process -Name acad,accoreconsole -ErrorAction SilentlyContinue) { throw 'Guarda tus dibujos y cierra Autodesk antes de actualizar las DLL. No se modificó la instalación.' }
$bundleSource = Join-Path $release 'CadMcp2021.bundle'
$bundleTarget = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\CadMcp2021.bundle'
$backup = Join-Path $projectRoot ('artifacts\install-backups\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $bundleTarget) {
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Copy-Item -LiteralPath $bundleTarget -Destination $backup -Recurse
}
foreach ($file in Get-ChildItem -LiteralPath $bundleSource -Recurse -File) {
    $relative = $file.FullName.Substring($bundleSource.Length+1)
    $destination = Join-Path $bundleTarget $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
[xml]$package = Get-Content -LiteralPath (Join-Path $bundleTarget 'PackageContents.xml') -Raw
$package.ApplicationPackage.Components.ComponentEntry.SetAttribute('LoadOnAutoCADStartup','True')
$package.Save((Join-Path $bundleTarget 'PackageContents.xml'))
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $bundleSource 'Contents') -Recurse -File) {
    $relative = $file.FullName.Substring($bundleSource.Length+1)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $bundleTarget $relative)).Hash) { throw "Verificación fallida: $relative" }
}
if (-not $BridgeOnly) {
    & (Join-Path $PSScriptRoot 'Register-CodexMcp.ps1') -Official -ServerPath (Join-Path $release 'server\CadMcp.Server.dll') -UpdateExisting
    if ($LASTEXITCODE -ne 0) { throw 'No se completó la actualización del registro MCP.' }
    & (Join-Path $PSScriptRoot 'Build-DesktopPlugin.ps1') -Install -ServerRoot (Join-Path $release 'server')
}
[ordered]@{version=$manifest.version;recordedUtc=[DateTime]::UtcNow.ToString('o');bridgeInstalled=$true;clientPluginRequested=(-not $BridgeOnly);bundleTarget=$bundleTarget;backup=$backup;reloadRequired=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts\test-results\expanded-installation.json') -Encoding utf8
Write-Output "Puente $($manifest.version) instalado. Abre Autodesk, recarga el cliente MCP y comprueba bridgeVersion=$($manifest.version) y $($manifest.tools) herramientas. Consulta los reportes de validación de esta versión."
