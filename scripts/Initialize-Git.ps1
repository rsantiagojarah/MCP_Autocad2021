param(
    [string]$Message = 'Inicializa el MCP para AutoCAD y Civil 3D 2021'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Get-Command git -ErrorAction Stop | Out-Null

Push-Location -LiteralPath $projectRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot '.git'))) {
        & git init -b main
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo inicializar Git.' }
    }

    $repositoryRoot = & git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo acceder al repositorio. Ejecuta este script desde tu PowerShell normal.' }
    if ([IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($projectRoot).TrimEnd('\', '/')) {
        throw 'El repositorio detectado no corresponde a esta carpeta de proyecto.'
    }

    $authorName = & git config user.name
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($authorName)) { throw 'Configura git user.name antes de crear el commit.' }
    $authorEmail = & git config user.email
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($authorEmail)) { throw 'Configura git user.email antes de crear el commit.' }

    & git add --all
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron preparar los archivos para el commit.' }

    & git diff --cached --quiet
    $stagedExitCode = $LASTEXITCODE
    if ($stagedExitCode -eq 0) {
        Write-Output 'No hay cambios pendientes para registrar.'
        return
    }
    if ($stagedExitCode -ne 1) { throw 'No se pudieron comprobar los cambios preparados.' }

    $body = @'
Incorpora el servidor MCP y los puentes para Autodesk 2021.

- Incluye herramientas de geometría, sólidos, superficies, alineamientos, secciones y corredores.
- Añade scripts de compilación, instalación y reparación de la conexión.
- Registra las pruebas, los ejemplos y la documentación del proyecto.
- Excluye herramientas locales, binarios generados, registros y credenciales.
'@
    & git commit -m $Message -m $body
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el commit.' }

    & git log -1 --format='%h %s'
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo verificar el commit creado.' }
    & git status --short --branch
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo verificar el estado final del repositorio.' }
}
finally {
    Pop-Location
}
