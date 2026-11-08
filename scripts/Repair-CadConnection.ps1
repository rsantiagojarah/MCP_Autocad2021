param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentUser = $currentIdentity.Name
$sandboxUser = $currentUser -match '\\CodexSandbox'
$hostPath = Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'
$serverPath = Join-Path $projectRoot 'artifacts\official-net10\server\CadMcp.Server.dll'
$bundleSource = Join-Path $projectRoot 'artifacts\compatibility-net8\CadMcp2021.bundle'
if (Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts\expanded-0.3.0\release-manifest.json')) {
    $serverPath = Join-Path $projectRoot 'artifacts\expanded-0.3.0\server\CadMcp.Server.dll'
    $bundleSource = Join-Path $projectRoot 'artifacts\expanded-0.3.0\CadMcp2021.bundle'
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts\expanded-0.4.0\release-manifest.json')) {
    $serverPath = Join-Path $projectRoot 'artifacts\expanded-0.4.0\server\CadMcp.Server.dll'
    $bundleSource = Join-Path $projectRoot 'artifacts\expanded-0.4.0\CadMcp2021.bundle'
}
$bundleTarget = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\CadMcp2021.bundle'
. (Join-Path $PSScriptRoot 'Resolve-CadCodexCli.ps1')
$cli = Resolve-CadCodexCli
$desktopCli = $cli.Path
$reportPath = Join-Path $projectRoot 'artifacts\test-results\connection-repair.json'
$sha = [Security.Cryptography.SHA256]::Create()
try { $sidHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($currentIdentity.User.Value)))).Replace('-', '').Substring(0, 16).ToLowerInvariant() }
finally { $sha.Dispose(); $currentIdentity.Dispose() }
$prefix = "cadmcp-$sidHash-"
$pipes = @([IO.Directory]::GetFiles('\\.\pipe\', 'cadmcp-*') | ForEach-Object { [IO.Path]::GetFileName($_) })
$ownPipes = @($pipes | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
$otherPipes = @($pipes | Where-Object { -not $_.StartsWith($prefix, [StringComparison]::Ordinal) })
$report = [ordered]@{
    recordedUtc = [DateTime]::UtcNow.ToString('o')
    windowsUser = $currentUser
    checkOnly = [bool]$CheckOnly
    sandboxUser = $sandboxUser
    ownBridgeCount = $ownPipes.Count
    otherUserBridgeCount = $otherPipes.Count
    bundleTarget = $bundleTarget
    serverPath = $serverPath
    codexCli = $desktopCli
    codexCliVersion = $cli.VersionText
    installed = $false
    liveConnectionVerified = $false
    clientReloadRequired = $true
}
foreach ($path in @($hostPath, $serverPath, (Join-Path $bundleSource 'PackageContents.xml'), $desktopCli)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Falta un componente de la reparación: $path" }
}
[xml]$package = Get-Content -LiteralPath (Join-Path $bundleSource 'PackageContents.xml') -Raw
$package.ApplicationPackage.Components.ComponentEntry.SetAttribute('LoadOnAutoCADStartup', 'True')
$sourceFiles = @(Get-ChildItem -LiteralPath $bundleSource -File -Recurse)
if (-not ($sourceFiles | Where-Object Name -EQ 'CadMcp.AutoCAD.dll')) { throw 'El paquete Autodesk no contiene el puente.' }
if ($CheckOnly) {
    $report['nextStep'] = if ($sandboxUser) { 'Ejecutar este script sin -CheckOnly desde PowerShell normal, con el usuario de Autodesk.' } else { 'Ejecutar este script sin -CheckOnly para registrar el MCP e instalar la carga automática.' }
    $report | ConvertTo-Json -Depth 6
    return
}
if ($sandboxUser) { throw 'Esta consola pertenece a CodexSandbox. Ejecuta el script desde tu PowerShell normal. No se modificaron permisos ni la configuración de usuario.' }
if ($ownPipes.Count -eq 0 -and $otherPipes.Count -gt 0) { throw 'Autodesk pertenece a otro usuario. Ejecuta la reparación con el mismo usuario que Civil 3D.' }
# Preflight every file before changing the installation. Never overwrite an in-use DLL.
$updates = @()
foreach ($sourceFile in $sourceFiles) {
    if ($sourceFile.Name -eq 'PackageContents.xml') { continue }
    $relative = $sourceFile.FullName.Substring($bundleSource.Length).TrimStart('\')
    $targetFile = Join-Path $bundleTarget $relative
    if (Test-Path -LiteralPath $targetFile) {
        if ((Get-FileHash -LiteralPath $sourceFile.FullName).Hash -eq (Get-FileHash -LiteralPath $targetFile).Hash) { continue }
        try { $probe = [IO.File]::Open($targetFile, 'Open', 'ReadWrite', 'None'); $probe.Dispose() }
        catch { throw "Guarda tus dibujos y cierra Autodesk antes de actualizar $targetFile. No se cambió la instalación." }
    }
    $updates += [pscustomobject]@{ Source = $sourceFile.FullName; Target = $targetFile }
}
$configPath = Join-Path $env:USERPROFILE '.codex\config.toml'
if (Test-Path -LiteralPath $configPath) {
    # Keep the backup beside the user's configuration; avoid copying unrelated settings into the project.
    $backupPath = $configPath + '.cad-repair-' + [Guid]::NewGuid().ToString('N') + '.bak'
    Copy-Item -LiteralPath $configPath -Destination $backupPath
    $report['configBackup'] = $backupPath
}
& (Join-Path $PSScriptRoot 'Register-CodexMcp.ps1') -Official -ServerPath $serverPath -UpdateExisting
if ($LASTEXITCODE -ne 0) { throw 'No se completó el registro del servidor MCP.' }
foreach ($update in $updates) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $update.Target) -Force | Out-Null
    Copy-Item -LiteralPath $update.Source -Destination $update.Target -Force
}
New-Item -ItemType Directory -Path $bundleTarget -Force | Out-Null
$manifestTarget = Join-Path $bundleTarget 'PackageContents.xml'
if (Test-Path -LiteralPath $manifestTarget) {
    Copy-Item -LiteralPath $manifestTarget -Destination ($manifestTarget + '.cad-repair.bak') -Force
}
$package.Save($manifestTarget)
foreach ($sourceFile in $sourceFiles) {
    if ($sourceFile.Name -eq 'PackageContents.xml') { continue }
    $relative = $sourceFile.FullName.Substring($bundleSource.Length).TrimStart('\')
    if ((Get-FileHash -LiteralPath $sourceFile.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $bundleTarget $relative)).Hash) { throw "La instalación no coincide: $relative" }
}
$report.installed = $true
if ($ownPipes.Count -eq 1) {
    & (Join-Path $PSScriptRoot 'Check-Live.ps1') -SessionId $ownPipes[0] -ServerPath $serverPath -DotnetHost $hostPath
    $report.liveConnectionVerified = $true
}
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output 'Registro MCP y carga automática instalados. No se cambiaron las ACL, SECURELOAD ni el aislamiento de Codex.'
Write-Output 'Guarda tus dibujos y reinicia Civil 3D para activar la carga automática. Reinicia Codex y abre un chat nuevo para cargar las herramientas cad_list_sessions y cad_query_entities.'
Write-Output 'La conexión completa debe verificarse desde esas herramientas en el chat nuevo; no ejecutando el servidor desde la terminal aislada.'
Write-Output "Reporte: $reportPath"
