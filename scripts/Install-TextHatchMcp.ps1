param([switch]$BridgeOnly, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Install-ExpandedMcp.ps1') -ReleasePath (Join-Path $projectRoot 'artifacts\expanded-0.5.0') -BridgeOnly:$BridgeOnly -CheckOnly:$CheckOnly
