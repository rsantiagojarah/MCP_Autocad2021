param(
    [switch]$AllowEmptyDrawingWriteTest,
    [switch]$RegisterCodex,
    [string]$SessionId,
    [switch]$Benchmark,
    [string]$ServerPath,
    [string]$DotnetHost,
    [string]$SurfaceReferenceFile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$server = if ($ServerPath) { $ServerPath } else { Join-Path $projectRoot 'artifacts\compatibility-net8\server\CadMcp.Server.dll' }
$outputDir = Join-Path $projectRoot 'artifacts\test-results'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$outputFile = Join-Path $outputDir 'live-check.json'
$report = [ordered]@{
    recordedUtc = [DateTime]::UtcNow.ToString('o')
    windowsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    passed = $false
    writeTests = [bool]$AllowEmptyDrawingWriteTest
    benchmarkRequested = [bool]$Benchmark
    checks = @()
    pending = @()
}
$process = $null
$script:cadMcpCheckId = 0
$stderrTask = $null
function Exchange-Mcp([string]$Method, [hashtable]$Parameters) {
    $script:cadMcpCheckId++
    $id = $script:cadMcpCheckId
    $message = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters }
    $process.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 40 -Compress))
    $process.StandardInput.Flush()
    while ($true) {
        $read = $process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(45000)) { throw "Sin respuesta de $Method tras 45 segundos. No repitas una modificación con otro UUID; conserva el reporte." }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'El servidor MCP cerró stdout antes de responder.' }
        $response = $line | ConvertFrom-Json
        if ($null -eq $response.id) { continue }
        if ($response.id -ne $id) { throw 'El servidor devolvió una respuesta no correlacionada.' }
        if ($null -ne $response.error) { throw ($response.error | ConvertTo-Json -Compress) }
        return $response.result
    }
}
function Invoke-CadTool([string]$Name, [hashtable]$Arguments, [switch]$ExpectError) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $result = Exchange-Mcp 'tools/call' @{ name = $Name; arguments = $Arguments }
    $payload = $result.content[0].text | ConvertFrom-Json
    $report.checks += [ordered]@{ tool = $Name; ok = [bool]$payload.ok; requestId = $payload.requestId; error = $payload.error; elapsedMilliseconds = $timer.Elapsed.TotalMilliseconds; queueMilliseconds = $payload.queueMilliseconds; executionMilliseconds = $payload.executionMilliseconds }
    if (-not $payload.ok -and -not $ExpectError) { throw "$Name : $($payload.error.code) - $($payload.error.message)" }
    if ($payload.ok -and $ExpectError) { throw "$Name debía rechazar el lote de prueba." }
    return $payload
}
function Get-CadMemory([int]$CadProcessId) {
    try {
        $cadProcess = Get-Process -Id $CadProcessId -ErrorAction Stop
        return @{ workingSetBytes = [long]$cadProcess.WorkingSet64; privateMemoryBytes = [long]$cadProcess.PrivateMemorySize64 }
    } catch { return @{ unavailable = $_.Exception.Message } }
}
try {
    if (-not (Test-Path -LiteralPath $server)) { throw 'Falta el servidor publicado. Ejecuta scripts/build.ps1 -Offline.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = if ($DotnetHost) { $DotnetHost } else { (Get-Command dotnet -ErrorAction Stop).Source }
    $start.Arguments = '"' + $server + '"'
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    [void]$process.Start()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $initial = Exchange-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'CadMcpLiveCheck'; version = '0.1.0' } }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $catalog = Exchange-Mcp 'tools/list' @{}
    if (@($catalog.tools).Count -ne 71) { throw 'El catálogo no contiene las 71 herramientas esperadas.' }
    $sessions = (Invoke-CadTool 'cad_list_sessions' @{}).result.sessions
    $candidates = @($sessions | Where-Object { -not $SessionId -or $_.sessionId -eq $SessionId })
    if ($candidates.Count -eq 0) { throw 'No se detectó un puente del usuario actual. Abre AutoCAD/Civil 3D, carga CadMcp.AutoCAD.dll mediante NETLOAD y ejecuta CADMCPSTART. Ejecuta esta prueba desde tu PowerShell normal, con el mismo usuario de AutoCAD.' }
    if ($candidates.Count -gt 1) { throw 'Hay varias sesiones. Repite con -SessionId seguido del sessionId deseado.' }
    $session = $candidates[0]
    $SessionId = $session.sessionId
    $report.session = $session
    $report.serverAssembly = $server
    $report.memoryBefore = Get-CadMemory $session.processId
    $docs = (Invoke-CadTool 'cad_list_documents' @{ sessionId = $SessionId }).result.documents
    $active = @($docs | Where-Object { $_.active })
    if ($active.Count -ne 1) { throw 'Se necesita un único documento activo en la sesión.' }
    $documentId = $active[0].documentId
    $report.document = (Invoke-CadTool 'cad_get_document_info' @{ sessionId = $SessionId; documentId = $documentId }).result
    [void](Invoke-CadTool 'cad_list_layers' @{ sessionId = $SessionId; documentId = $documentId; limit = 100 })
    $before = (Invoke-CadTool 'cad_query_entities' @{ sessionId = $SessionId; documentId = $documentId; limit = 10 }).result
    if ($AllowEmptyDrawingWriteTest) {
        if (@($before.entities).Count -ne 0 -or $null -ne $before.nextOffset) { throw 'La prueba de escritura exige ModelSpace vacío. Se rechazó antes de modificar el dibujo.' }
        $layer = 'CADMCP_TEST_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        [void](Invoke-CadTool 'cad_upsert_layers' @{ sessionId = $SessionId; documentId = $documentId; operationId = [Guid]::NewGuid().ToString('D'); layers = @(@{ name = $layer; colorIndex = 3 }) })
        $operationId = [Guid]::NewGuid().ToString('D')
        $report.geometryOperationId = $operationId
        $geometryArgs = @{
            sessionId = $SessionId; documentId = $documentId; operationId = $operationId
            coordinateSpace = 'WCS'; units = $report.document.units
            entities = @(
                @{ type = 'line'; layer = $layer; points = @(@{x=0;y=0;z=0}, @{x=3;y=4;z=0}) },
                @{ type = 'polyline'; layer = $layer; closed = $true; points = @(@{x=0;y=0;z=0}, @{x=10;y=0;z=0}, @{x=10;y=10;z=0}, @{x=0;y=10;z=0}) },
                @{ type = 'point'; layer = $layer; points = @(@{x=5;y=5;z=0}) }
            )
        }
        $created = Invoke-CadTool 'cad_create_entities' $geometryArgs
        $retry = Invoke-CadTool 'cad_create_entities' $geometryArgs
        if (($created.result.handles -join ',') -ne ($retry.result.handles -join ',')) { throw 'Falló la deduplicación del lote.' }
        $rows = (Invoke-CadTool 'cad_query_entities' @{ sessionId = $SessionId; documentId = $documentId; layer = $layer }).result.entities
        if (@($rows).Count -ne 3) { throw 'El lote no creó exactamente tres entidades.' }
        $line = @($rows | Where-Object { $_.dxfType -eq 'LINE' })[0]
        $polyline = @($rows | Where-Object { $_.dxfType -eq 'LWPOLYLINE' })[0]
        if ([Math]::Abs($line.length - 5) -gt 1e-9 -or [Math]::Abs($polyline.area - 100) -gt 1e-9) { throw 'Longitud o área incorrecta.' }
        $failed = Invoke-CadTool 'cad_update_entities' @{ sessionId = $SessionId; documentId = $documentId; operationId = [Guid]::NewGuid().ToString('D'); entities = @(@{handle=$line.handle;colorIndex=1}, @{handle='INVALID';colorIndex=2}) } -ExpectError
        if ($failed.error.code -ne 'INVALID_ARGUMENT') { throw 'El lote fallido devolvió un error inesperado.' }
        $after = (Invoke-CadTool 'cad_query_entities' @{ sessionId = $SessionId; documentId = $documentId; layer = $layer }).result.entities
        $afterLine = @($after | Where-Object { $_.handle -eq $line.handle })[0]
        if ($afterLine.colorIndex -ne $line.colorIndex) { throw 'La reversión dejó un cambio parcial.' }
        $status = (Invoke-CadTool 'cad_get_request_status' @{ sessionId = $SessionId; requestId = $operationId }).result
        if ($status.state -ne 'completed') { throw 'El historial no contiene la operación completada.' }
        $report.geometryVerified = @{ created = 3; lineLength = $line.length; polylineArea = $polyline.area; deduplication = $true; rollback = $true; layer = $layer }
    }
    if ($session.civil3d) {
        $surfaces = (Invoke-CadTool 'civil_list_surfaces' @{sessionId=$SessionId;documentId=$documentId;limit=100}).result
        [void](Invoke-CadTool 'civil_list_alignments' @{sessionId=$SessionId;documentId=$documentId;limit=100})
        $report.surfaceCountFirstPage = @($surfaces.surfaces).Count
        if ($AllowEmptyDrawingWriteTest) {
            $cogoId = [Guid]::NewGuid().ToString('D'); $report.cogoOperationId = $cogoId
            $points = Invoke-CadTool 'civil_create_cogo_points' @{sessionId=$SessionId;documentId=$documentId;operationId=$cogoId;coordinateSpace='WCS';units=$report.document.units;description='CADMCP_TEST';points=@(@{x=20;y=20;z=1},@{x=30;y=20;z=2})}
            if ($points.result.created -ne 2) { throw 'No se crearon dos puntos COGO.' }
            $report.cogoVerified = $true
        }
        if ($SurfaceReferenceFile) {
            $reference = Get-Content -LiteralPath $SurfaceReferenceFile -Raw | ConvertFrom-Json
            if (-not $reference.surfaceHandle -or @($reference.points).Count -eq 0 -or $reference.tolerance -le 0) { throw 'Referencia de superficie inválida: especifica surfaceHandle, points y tolerance positivo.' }
            foreach ($point in $reference.points) {
                foreach ($field in @('x','y','expectedElevation')) {
                    if ($null -eq $point.$field -or [double]::IsNaN([double]$point.$field) -or [double]::IsInfinity([double]$point.$field)) { throw "La referencia requiere $field finito en cada punto." }
                }
            }
            $surfacePoints = @($reference.points | ForEach-Object { @{x=$_.x;y=$_.y;z=0} })
            $sampled = Invoke-CadTool 'civil_get_surface_elevations' @{sessionId=$SessionId;documentId=$documentId;surfaceHandle=$reference.surfaceHandle;coordinateSpace='WCS';units=$report.document.units;points=$surfacePoints}
            if (@($sampled.result.results).Count -ne $surfacePoints.Count) { throw 'La superficie devolvió una cantidad de muestras incorrecta.' }
            for ($sampleIndex=0; $sampleIndex -lt $surfacePoints.Count; $sampleIndex++) {
                $sample = @($sampled.result.results)[$sampleIndex]
                if ($sample.error -or $null -eq $sample.elevation -or [Math]::Abs($sample.elevation - @($reference.points)[$sampleIndex].expectedElevation) -gt $reference.tolerance) { throw "Elevación incorrecta en la muestra $sampleIndex." }
            }
            $report.surfaceElevationsVerified = $true
        } else { $report.pending += 'Elevaciones: aporta -SurfaceReferenceFile con superficie, coordenadas y elevaciones conocidas; también existe la prueba nativa CADMCPCIVILCHECK con superficie sintética.' }
    } else { $report.pending += 'Pruebas Civil 3D: repetir en Civil 3D 2021 con el módulo cargado.' }
    if (-not $AllowEmptyDrawingWriteTest) { $report.pending += 'Pruebas de escritura/reversión: repetir sobre dibujo vacío con -AllowEmptyDrawingWriteTest.' }
    if ($Benchmark) {
        $samples = @()
        foreach ($iteration in (1..10)) {
            $queryWatch = [Diagnostics.Stopwatch]::StartNew()
            [void](Invoke-CadTool 'cad_query_entities' @{sessionId=$SessionId;documentId=$documentId;limit=500})
            $samples += $queryWatch.Elapsed.TotalMilliseconds
        }
        $sorted = @($samples | Sort-Object)
        $report.queryBenchmark = @{ samplesMilliseconds = $samples; p50Milliseconds = $sorted[4]; p95Milliseconds = $sorted[9]; note = 'Consulta de primera página (máximo 500), diez muestras; incluye comunicación y espera del hilo principal.' }
        if ($AllowEmptyDrawingWriteTest) {
            $batches = @()
            foreach ($size in @(100,1000,10000)) {
                $batchId = [Guid]::NewGuid().ToString('D')
                $specs = @(for ($pointIndex=0; $pointIndex -lt $size; $pointIndex++) { @{type='point';layer=$layer;points=@(@{x=$pointIndex;y=$size;z=0})} })
                $memoryBeforeBatch = Get-CadMemory $session.processId
                $batchWatch = [Diagnostics.Stopwatch]::StartNew()
                $batch = Invoke-CadTool 'cad_create_entities' @{sessionId=$SessionId;documentId=$documentId;operationId=$batchId;coordinateSpace='WCS';units=$report.document.units;entities=$specs}
                $elapsed = $batchWatch.Elapsed.TotalMilliseconds
                if ($batch.result.created -ne $size) { throw "El lote de $size puntos devolvió una cantidad incorrecta." }
                $batches += @{ entities=$size; requestId=$batchId; elapsedMilliseconds=$elapsed; queueMilliseconds=$batch.queueMilliseconds; executionMilliseconds=$batch.executionMilliseconds; memoryBefore=$memoryBeforeBatch; memoryAfter=(Get-CadMemory $session.processId) }
            }
            $report.batchBenchmark = @{ batches = $batches; note = 'Una muestra por tamaño, en un dibujo que acumula las entidades de prueba. No es una comparación sobre dibujos idénticos ni un P95 de creación. Se dejan 11100 puntos adicionales en el dibujo de ensayo.' }
        }
    }
    $report.memoryAfter = Get-CadMemory $session.processId
    if ($RegisterCodex) {
        & (Join-Path $PSScriptRoot 'Register-CodexMcp.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Falló el registro en Codex.' }
        $report.codexRegistered = $true
    }
    $report.passed = $true
    Write-Output 'Comprobaciones solicitadas correctas. Consulta pending en el reporte para las pruebas restantes.'
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) { $process.StandardInput.Close(); if (-not $process.WaitForExit(2000)) { $process.Kill() } }
            if ($null -ne $stderrTask -and $stderrTask.IsCompleted) { $stderrTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $outputDir 'live-server.stderr.log') -Encoding utf8 }
        } catch { }
        $process.Dispose()
    }
    $report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $outputFile -Encoding utf8
    Write-Output "Reporte: $outputFile"
}
