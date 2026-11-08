param([switch]$Install, [switch]$Compatibility, [string]$ServerRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$variant = if ($Compatibility) { 'compatibility-net8' } else { 'official-net10' }
$version = if ($Compatibility) { '0.4.0-compat' } else { '0.4.0' }
$serverRoot = if ($ServerRoot) { [IO.Path]::GetFullPath($ServerRoot) } else { Join-Path $projectRoot "artifacts\$variant\server" }
if (-not (Test-Path -LiteralPath (Join-Path $serverRoot 'CadMcp.Server.dll'))) { throw "No existe el servidor $variant publicado. Ejecuta scripts/Prepare-Official.ps1 para el servidor oficial o scripts/build.ps1 -Offline para compatibilidad." }
$dotnetHost = if (-not $Compatibility -and (Test-Path -LiteralPath (Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'))) {
    Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'
} else { (Get-Command dotnet -ErrorAction Stop).Source }
$runtimeMajor = if ($Compatibility) { '8' } else { '10' }
$runtimes = & $dotnetHost --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($runtimes | Where-Object { $_ -match "^Microsoft.NETCore.App $runtimeMajor\." })) { throw "El host $dotnetHost no tiene el runtime .NET $runtimeMajor." }
if (-not $Compatibility -and -not ($runtimes | Where-Object { $_ -match '^Microsoft.AspNetCore.App 10\.' })) { throw 'El servidor oficial necesita también el runtime Microsoft.AspNetCore.App 10.' }
$marketplaceRoot = Join-Path $projectRoot 'desktop-marketplace'
$pluginFolder = if ($Compatibility) { 'autocad-civil3d-2021' } else { 'autocad-civil3d-2021-net10' }
$pluginRoot = Join-Path $marketplaceRoot "plugins\$pluginFolder"
$catalogRoot = Join-Path $marketplaceRoot '.agents\plugins'
New-Item -ItemType Directory -Path $pluginRoot,$catalogRoot,(Join-Path $pluginRoot 'server'),(Join-Path $pluginRoot '.codex-plugin') -Force | Out-Null
foreach ($file in @('plugin.json','mcp.json','.mcp.json')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "packaging\chatgpt-plugin\$file") -Destination $pluginRoot -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\chatgpt-plugin\.codex-plugin\plugin.json') -Destination (Join-Path $pluginRoot '.codex-plugin\plugin.json') -Force
foreach ($file in @('mcp.json','.mcp.json')) {
    $path = Join-Path $pluginRoot $file
    $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $config.mcpServers.cad2021.command = $dotnetHost
    $config | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $path -Encoding utf8
}
foreach ($file in @('plugin.json','.codex-plugin\plugin.json')) {
    $path = Join-Path $pluginRoot $file
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $manifest.version = $version
    if ($Compatibility) {
        $ui = if ($file -eq 'plugin.json') { $manifest.extensions.'com.openai'.interface } else { $manifest.interface }
        $ui.longDescription = 'Herramientas locales para Autodesk 2021. Servidor .NET 8 con adaptador MCP de compatibilidad. Requiere el puente CadMcp cargado en Autodesk.'
    }
    $manifest | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $path -Encoding utf8
}
Get-ChildItem -LiteralPath $serverRoot -File | Copy-Item -Destination (Join-Path $pluginRoot 'server') -Force
$serverDescription = if ($Compatibility) { '.NET 8 con adaptador MCP de compatibilidad' } else { '.NET 10 con SDK oficial MCP para C# 1.2.0' }
$readme = @"
# AutoCAD y Civil 3D 2021

Complemento local para ChatGPT de escritorio y Codex, versión $version. Servidor: $serverDescription.
Host .NET seleccionado: $dotnetHost. Conserva esa instalación y la carpeta del proyecto en su ubicación actual.

Abre AutoCAD o Civil 3D 2021, carga CadMcp.AutoCAD.dll mediante NETLOAD y comprueba CADMCPSTATUS. Ese plugin está en el paquete Autodesk original del proyecto. Abre un chat local nuevo y selecciona este complemento para consultar sesiones y documentos.

No convierte unidades ni coordenadas implícitamente. Las escrituras requieren WCS, unidades coincidentes y un UUID por operación. El historial de operaciones solo dura hasta reiniciar el puente. Los casos de prueba de AutoCAD y Civil 3D están documentados en artifacts/test-results del proyecto; no se ha validado el rendimiento de terrenos grandes.

Este paquete está preparado para instalarse desde el catálogo local Herramientas CAD locales. No es una publicación en el directorio público ni conecta un servidor remoto.
"@
$readme | Set-Content -LiteralPath (Join-Path $pluginRoot 'README.md') -Encoding utf8
$licenseFile = Join-Path $env:USERPROFILE '.nuget\packages\newtonsoft.json\13.0.1\LICENSE.md'
if (Test-Path -LiteralPath $licenseFile) { Copy-Item -LiteralPath $licenseFile -Destination (Join-Path $pluginRoot 'Newtonsoft.Json.LICENSE.md') -Force }
$catalog = [ordered]@{
    name = 'cad-local'
    interface = @{ displayName = 'Herramientas CAD locales' }
    plugins = @(@{
        name = 'autocad-civil3d-2021'
        source = @{ source = 'local'; path = "./plugins/$pluginFolder" }
        policy = @{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
        category = 'Productivity'
    })
}
$catalog | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $catalogRoot 'marketplace.json') -Encoding utf8
Write-Output "Complemento generado: $pluginRoot"
Write-Output "Versión $version; servidor $serverDescription; host $dotnetHost"
if ($Install) {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ($currentUser -match '\\CodexSandbox') {
        throw 'El paquete está generado, pero esta consola pertenece al usuario aislado de Codex. Para instalarlo en tu ChatGPT de escritorio ejecuta este mismo script con -Install desde tu PowerShell normal.'
    }
    $marketplacesText = & codex plugin marketplace list --json
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron consultar los catálogos del usuario.' }
    $marketplaces = ($marketplacesText -join [Environment]::NewLine) | ConvertFrom-Json
    $existing = $marketplaces.marketplaces | Where-Object name -EQ 'cad-local'
    if ($existing) {
        $existingRoot = $existing.root -replace '^\\\\\?\\', ''
        if ([IO.Path]::GetFullPath($existingRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($marketplaceRoot).TrimEnd('\')) { throw 'cad-local apunta a otra carpeta. No se modificó ese registro.' }
    } else {
        & codex plugin marketplace add $marketplaceRoot --json
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo registrar el catálogo local.' }
    }
    & codex plugin add 'autocad-civil3d-2021@cad-local' --json
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo instalar el complemento desde el catálogo local.' }
    & codex plugin list --marketplace cad-local --json
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo verificar la instalación del complemento.' }
    $cacheRoot = Join-Path $env:USERPROFILE ".codex\plugins\cache\cad-local\autocad-civil3d-2021\$version"
    $cachedConfigPath = Join-Path $cacheRoot '.mcp.json'
    if (-not (Test-Path -LiteralPath $cachedConfigPath)) { throw "No se encontró la versión $version en la caché del usuario. Se conservó la instalación anterior; revisa la salida de codex plugin add." }
    $cachedConfig = Get-Content -LiteralPath $cachedConfigPath -Raw | ConvertFrom-Json
    if ($cachedConfig.mcpServers.cad2021.command -ne $dotnetHost) { throw 'La instalación no apunta al host .NET seleccionado.' }
    foreach ($file in Get-ChildItem -LiteralPath $serverRoot -File) {
        $cachedFile = Join-Path $cacheRoot "server\$($file.Name)"
        if (-not (Test-Path -LiteralPath $cachedFile) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $cachedFile).Hash) { throw "La copia instalada no coincide: $($file.Name)" }
    }
    $reportPath = Join-Path $projectRoot 'artifacts\test-results\desktop-installation.json'
    [ordered]@{recordedUtc=[DateTime]::UtcNow.ToString('o'); windowsUser=$currentUser; passed=$true; version=$version; variant=$variant; dotnetHost=$dotnetHost; cacheRoot=$cacheRoot; filesVerified=$true; clientReloadRequired=$true} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "Instalación verificada: $version ($variant). Reinicia ChatGPT o abre una sesión nueva de Codex para cargarla."
}
