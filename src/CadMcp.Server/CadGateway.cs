using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;

namespace CadMcp.Server;

public sealed class CadGateway : IDisposable
{
    private sealed class Connection : IDisposable
    {
        private readonly string name;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream? pipe;
        public Connection(string name) { this.name = name; }
        public async Task<BridgeResponse> SendAsync(BridgeRequest request, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            var sent = false;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                if (pipe == null || !pipe.IsConnected)
                {
                    pipe?.Dispose();
                    pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(2000, deadline.Token).ConfigureAwait(false);
                }
                // Once the first byte can be sent, failure is an uncertain outcome.
                sent = true;
                await Framing.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
                var response = await Framing.ReadAsync<BridgeResponse>(pipe, deadline.Token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("El puente cerró la conexión.");
                if (response.RequestId != request.RequestId) throw new IOException("Respuesta no correlacionada.");
                return response;
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException || ex is UnauthorizedAccessException || ex is CadFault)
            {
                pipe?.Dispose(); pipe = null;
                return BridgeResponse.Fail(request.RequestId, sent ? "OUTCOME_UNKNOWN" : "CONNECTION_FAILED",
                    sent ? "No se recibió resultado definitivo. Consulta cad_get_request_status con este requestId. No repitas una modificación con otro UUID. " + ex.Message : ex.Message);
            }
            finally { gate.Release(); }
        }
        public void Dispose() { pipe?.Dispose(); }
    }
    private readonly ConcurrentDictionary<string, Connection> connections = new ConcurrentDictionary<string, Connection>(StringComparer.Ordinal);
    private readonly Func<string[]> discover;
    public CadGateway() : this(() => Directory.GetFiles(@"\\.\pipe\", "cadmcp-*").Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray()) { }
    public CadGateway(Func<string[]> discover) { this.discover = discover; }

    public async Task<BridgeResponse> CallAsync(string toolName, JObject arguments, CancellationToken token)
    {
        var definition = ToolCatalog.All.FirstOrDefault(t => t.Name == toolName);
        if (definition == null) return BridgeResponse.Fail("", "UNKNOWN_TOOL", "No existe la herramienta " + toolName);
        try
        {
            ToolCatalog.Validate(arguments, definition.Schema);
            if (toolName == "cad_list_sessions") return await Sessions(token).ConfigureAwait(false);
            var session = arguments.Value<string>("sessionId")!;
            if (!session.StartsWith(PipeNames.Prefix, StringComparison.Ordinal) || !int.TryParse(session.Substring(PipeNames.Prefix.Length), out var pid) || pid <= 0)
                throw new CadFault("INVALID_ARGUMENT", "sessionId debe provenir de cad_list_sessions y pertenecer al usuario actual.");
            var args = (JObject)arguments.DeepClone(); args.Remove("sessionId"); args.Remove("documentId"); args.Remove("operationId");
            var request = new BridgeRequest { Method = definition.Method, DocumentId = arguments.Value<string>("documentId"),
                RequestId = definition.Mutates ? Guid.Parse(arguments.Value<string>("operationId")!).ToString("D") : Guid.NewGuid().ToString("D"), Arguments = args };
            return await connections.GetOrAdd(session, s => new Connection(s)).SendAsync(request, token).ConfigureAwait(false);
        }
        catch (CadFault ex) { return BridgeResponse.Fail(arguments["operationId"]?.Type == JTokenType.String ? arguments.Value<string>("operationId")! : "", ex.Code, ex.Message); }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is OverflowException)
        { return BridgeResponse.Fail("", "INVALID_ARGUMENT", ex.Message); }
    }
    private async Task<BridgeResponse> Sessions(CancellationToken token)
    {
        string[] names;
        try { names = discover(); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { return BridgeResponse.Fail("", "DISCOVERY_FAILED", ex.Message); }
        // Discover foreign names only for diagnosis. Never connect across Windows identities.
        var prefix = PipeNames.Prefix;
        names = names.Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^cadmcp-[0-9a-f]{16}-[1-9][0-9]*$")).Distinct(StringComparer.Ordinal).ToArray();
        var foreignCount = names.Count(name => !name.StartsWith(prefix, StringComparison.Ordinal));
        var ownNames = names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Take(32).ToArray();
        var results = await Task.WhenAll(ownNames.Select(name => connections.GetOrAdd(name, s => new Connection(s))
            .SendAsync(new BridgeRequest { Method = "session.info", ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(3) }, token))).ConfigureAwait(false);
        var connected = results.Count(r => r.Ok);
        return BridgeResponse.Success("", new { sessions = results.Where(r => r.Ok).Select(r => r.Result).ToArray(),
            unavailable = results.Count(r => !r.Ok), otherUserBridgeCount = foreignCount,
            connectionState = connected > 0 ? "connected" : ownNames.Length > 0 ? "bridge_unavailable" : foreignCount > 0 ? "windows_user_mismatch" : "bridge_not_started",
            note = connected > 0 ? "Conexión establecida con el puente del usuario actual."
                : ownNames.Length > 0 ? "El puente del usuario actual no respondió. Termina el comando activo en Autodesk y vuelve a consultar."
                : foreignCount > 0 ? "El puente está iniciado bajo otro usuario de Windows. Usa el MCP registrado en Codex desde el mismo usuario que Autodesk. Una consulta ejecutada desde CodexSandbox no puede acceder a ese puente; no cambies las ACL. Ejecuta scripts/Repair-CadConnection.ps1 desde tu PowerShell normal y abre una sesión nueva de Codex."
                : "No hay puentes iniciados. Carga CadMcp.AutoCAD.dll y ejecuta CADMCPSTART, o instala la carga automática con scripts/Repair-CadConnection.ps1." });
    }
    public void Dispose() { foreach (var connection in connections.Values) connection.Dispose(); }
}
