function Resolve-CadCodexCli {
    [CmdletBinding()]
    param([string[]]$CandidatePaths)

    if (-not $CandidatePaths) {
        $desktopBin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
        $CandidatePaths = @(
            if ($env:CODEX_CLI_PATH) { $env:CODEX_CLI_PATH }
            if (Test-Path -LiteralPath $desktopBin) {
                # The unversioned binary may be left over from an older desktop release.
                Join-Path $desktopBin 'codex.exe'
                Get-ChildItem -LiteralPath $desktopBin -Directory | ForEach-Object {
                    Join-Path $_.FullName 'codex.exe'
                }
            }
            $onPath = Get-Command codex -ErrorAction SilentlyContinue
            if ($onPath) { $onPath.Source }
        )
    }
    $candidates = @(foreach ($candidatePath in ($CandidatePaths | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { continue }
        try {
            $versionOutput = & $candidatePath --version 2>$null
            if ($LASTEXITCODE -ne 0) { continue }
            $versionText = ($versionOutput -join ' ').Trim()
            if ($versionText -notmatch '^codex-cli (\d+\.\d+\.\d+)(?:[-+].*)?$') { continue }
            [pscustomobject]@{
                Path = $candidatePath
                Version = [version]$Matches[1]
                VersionText = $versionText
                LastWriteTimeUtc = (Get-Item -LiteralPath $candidatePath).LastWriteTimeUtc
            }
        } catch {
            Write-Verbose "CLI no disponible: $candidatePath : $($_.Exception.Message)"
        }
    })
    $selected = $candidates | Sort-Object Version, LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $selected) { throw 'No se encontró una CLI de Codex válida. Revisa la instalación de la aplicación Codex.' }
    return $selected
}
