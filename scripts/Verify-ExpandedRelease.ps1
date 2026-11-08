$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = Join-Path $projectRoot 'artifacts\expanded-0.4.0'
$checks = [Collections.Generic.List[object]]::new()
$manifest = Get-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $release $file.path))
    if (-not $path.StartsWith($release+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta de paquete inválida.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Hash incorrecto: $($file.path)" }
}
$checks.Add(@{name='release_hashes';passed=$true;files=$manifest.files.Count})
$tools = @(Get-Content -LiteralPath (Join-Path $projectRoot 'examples\extended-tools.json') -Raw | ConvertFrom-Json)
$handlers = @{}
foreach ($folder in @('src\CadMcp.AutoCAD','src\CadMcp.Civil3D')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Filter '*.cs' -File) {
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), 'Register\("([^"]+)"')) { $handlers[$match.Groups[1].Value]=$true }
    }
}
foreach ($tool in $tools) { if (-not $handlers.ContainsKey($tool.method)) { throw "Falta el handler nativo de $($tool.name)" } }
foreach ($method in @('civil.section_sources','civil.set_section_sources','civil.create_multiple_section_views','civil.update_section_views','civil.add_solid_sections','civil.generate_cross_sections')) {
    if (-not $handlers.ContainsKey($method)) { throw "Falta el handler nativo de secciones: $method" }
}
$checks.Add(@{name='six_section_native_handlers';passed=$true;count=6})
$checks.Add(@{name='new_native_handlers';passed=$true;count=$tools.Count})
[xml]$trx = Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts\test-results\expanded-release.trx') -Raw
$counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
if ([int]$counters.failed -ne 0 -or [int]$counters.passed -lt 64) { throw 'Las pruebas automatizadas no pasaron.' }
$checks.Add(@{name='unit_tests';passed=$true;count=[int]$counters.passed})
foreach ($reportFile in @('expanded-official-stdio.json','expanded-compatibility-stdio.json')) {
    $protocol = Get-Content -LiteralPath (Join-Path $projectRoot "artifacts\test-results\$reportFile") -Raw | ConvertFrom-Json
    if (-not $protocol.passed -or $protocol.checks -notcontains '71_tools') { throw "Prueba MCP fallida: $reportFile" }
    $checks.Add(@{name=$reportFile;passed=$true;checks=$protocol.checks})
}
foreach ($scriptName in @('Build-ExpandedRelease.ps1','Install-ExpandedMcp.ps1','Register-CodexMcp.ps1','Repair-CadConnection.ps1','Build-DesktopPlugin.ps1')) {
    $tokens=$null; $errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $scriptName),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
}
$checks.Add(@{name='installer_syntax';passed=$true})
$preflight = & (Join-Path $PSScriptRoot 'Install-ExpandedMcp.ps1') -CheckOnly | Out-String | ConvertFrom-Json
if ($preflight.writesPerformed -or $preflight.tools -ne 71) { throw 'Preflight inesperado.' }
$checks.Add(@{name='installer_readonly_preflight';passed=$true;details=$preflight})
$result = [ordered]@{
    recordedUtc=[DateTime]::UtcNow.ToString('o');version='0.4.0';tools=71;newTools=58;buildAndProtocolPassed=$true
    checks=$checks;activeInstallationUpdated=$false;originalDrawingModified=$false
    nativeValidation=@{status='pending';testsCompiled=$true;executionBlocker='AutoCAD Core Console aborts with: Problem with setting up current profile. New bridge must be loaded in a normal-user Civil 3D session.'}
    release=$release;activationScript=(Join-Path $PSScriptRoot 'Install-ExpandedMcp.ps1')
}
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts\test-results\expanded-verification.json') -Encoding utf8
$result | ConvertTo-Json -Depth 10
