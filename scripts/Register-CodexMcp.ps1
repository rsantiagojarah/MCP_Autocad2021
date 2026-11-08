param([switch]$Official, [string]$ServerPath, [switch]$UpdateExisting)
$ErrorActionPreference = 'Stop'
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($currentUser -match '\\CodexSandbox') {
    throw 'Ejecuta este registro desde tu PowerShell normal. El registro hecho bajo CodexSandbox solo afecta al usuario aislado y no al perfil de tu ChatGPT de escritorio.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$variant = if ($Official) { 'official-net10' } else { 'compatibility-net8' }
$server = if ($ServerPath) { [IO.Path]::GetFullPath($ServerPath) } else { Join-Path $projectRoot "artifacts\$variant\server\CadMcp.Server.dll" }
if (-not (Test-Path -LiteralPath $server)) { throw "No existe el servidor publicado: $server" }
$localHost = Join-Path $projectRoot '.tools\dotnet10\dotnet.exe'
$dotnet = if ($Official -and (Test-Path -LiteralPath $localHost)) { $localHost } else { (Get-Command dotnet -ErrorAction Stop).Source }
. (Join-Path $PSScriptRoot 'Resolve-CadCodexCli.ps1')
$cli = Resolve-CadCodexCli
$codex = $cli.Path
Write-Output "CLI seleccionada: $($cli.VersionText) ($codex)"
$serverName = 'autocad-civil3d-2021'
# A load error is not evidence that this server is missing. Validate first.
$preflightText = & $codex mcp list --json 2>&1
if ($LASTEXITCODE -ne 0) { throw "La CLI no pudo leer la configuración de Codex. No se intentó registrar el MCP.`n$($preflightText -join [Environment]::NewLine)" }
$existingText = & $codex mcp get $serverName --json 2>$null
if ($LASTEXITCODE -eq 0) {
    $existing = ($existingText -join [Environment]::NewLine) | ConvertFrom-Json
    $existingCommand = $existing.transport.command
    $existingArgs = @($existing.transport.args)
    if (($existingCommand -eq $dotnet -or $existingCommand -eq 'dotnet') -and $existingArgs.Count -eq 1 -and $existingArgs[0] -eq $server) {
        Write-Output 'El servidor ya está registrado con esta configuración.'
        exit 0
    }
    if (-not $UpdateExisting) { throw 'Ya existe un servidor con ese nombre y otra configuración. Se conservó sin cambios; revisa codex mcp get autocad-civil3d-2021.' }
    if ($existingArgs.Count -ne 1 -or -not ([IO.Path]::GetFullPath($existingArgs[0])).StartsWith($projectRoot.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($existingArgs[0]) -ne 'CadMcp.Server.dll') {
        throw 'El servidor existente apunta fuera de este proyecto; no se reemplazó.'
    }
    $configFile = Join-Path $env:USERPROFILE '.codex\config.toml'
    if (Test-Path -LiteralPath $configFile) { Copy-Item -LiteralPath $configFile -Destination ($configFile+'.expanded-'+[Guid]::NewGuid().ToString('N')+'.bak') }
    & $codex mcp remove $serverName
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo actualizar el registro MCP anterior.' }
    & $codex mcp add $serverName -- $dotnet $server
    if ($LASTEXITCODE -ne 0) {
        & $codex mcp add $serverName -- $existingCommand @existingArgs
        throw 'Falló el registro de la nueva versión; se intentó restaurar la entrada anterior.'
    }
    & $codex mcp get $serverName --json
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo verificar el registro actualizado.' }
    Write-Output 'Registro MCP actualizado. Abre una sesión nueva para cargar las herramientas del servidor seleccionado.'
    exit 0
}
& $codex mcp add $serverName -- $dotnet $server
if ($LASTEXITCODE -ne 0) { throw 'No se pudo registrar el MCP en la configuración de usuario de Codex.' }
& $codex mcp get $serverName --json
if ($LASTEXITCODE -ne 0) { throw 'Codex no pudo leer el registro creado.' }
Write-Output 'Registrado. Abre una sesión nueva de Codex para comprobar las herramientas.'
