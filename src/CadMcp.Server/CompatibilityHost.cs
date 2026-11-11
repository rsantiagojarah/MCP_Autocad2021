using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using CadMcp.Server;
using Newtonsoft.Json.Linq;

// Offline compatibility adapter, kept separate from the official SDK adapter.
// Implements the tools-only MCP 2025-11-25 stdio subset; not HTTP, Tasks or resources.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var gateway = new CadGateway();
using var output = new SemaphoreSlim(1, 1);
using var slots = new SemaphoreSlim(16, 16);
var pending = new ConcurrentDictionary<string, CancellationTokenSource>();
var tasks = new ConcurrentDictionary<long, Task>();
var initialized = false;
long sequence = 0;
using var input = Console.OpenStandardInput();

async Task Write(object value)
{
    await output.WaitAsync();
    try { await Console.Out.WriteLineAsync(Wire.Serialize(value)); await Console.Out.FlushAsync(); }
    finally { output.Release(); }
}
async Task Handle(JObject message, CancellationToken token)
{
    var id = message["id"]!;
    try
    {
        object result;
        switch (message.Value<string>("method"))
        {
            case "ping": result = new { }; break;
            case "tools/list": result = new { tools = ToolCatalog.All.Select(t => t.Protocol()).ToArray() }; break;
            case "tools/call":
                var parameters = message["params"] as JObject;
                if (parameters?["name"]?.Type != JTokenType.String || (parameters["arguments"] != null && parameters["arguments"] is not JObject))
                { await Write(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "tools/call requiere name y arguments de tipo objeto." } }); return; }
                var response = await gateway.CallAsync(parameters?.Value<string>("name") ?? "", parameters?["arguments"] as JObject ?? new JObject(), token);
                result = new { content = new[] { new { type = "text", text = Wire.Serialize(response) } }, isError = !response.Ok }; break;
            default: await Write(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Método no soportado." } }); return;
        }
        await Write(new { jsonrpc = "2.0", id, result });
    }
    catch (OperationCanceledException) { await Write(new { jsonrpc = "2.0", id, error = new { code = -32800, message = "Solicitud cancelada; comprueba el estado de cualquier modificación enviada." } }); }
    catch (Exception ex) { await Write(new { jsonrpc = "2.0", id, error = new { code = -32603, message = ex.Message } }); }
}

while (true)
{
    // Bound input bytes before parsing: ReadLineAsync would allow unbounded allocation.
    string? line;
    try { line = await BoundedLine.ReadAsync(input, Wire.MaxFrameBytes); }
    catch (Exception ex) { await Console.Error.WriteLineAsync(ex.Message); break; }
    if (line == null) break;
    JObject message;
    try { message = Wire.Deserialize<JObject>(line); }
    catch (Exception) { await Write(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "JSON inválido." } }); continue; }
    var method = message["method"]?.Type == JTokenType.String ? message.Value<string>("method") : null;
    var id = message["id"];
    if (message["jsonrpc"]?.Type != JTokenType.String || message.Value<string>("jsonrpc") != "2.0" || method == null || (message["params"] != null && message["params"] is not JObject) || (id != null && id.Type != JTokenType.String && id.Type != JTokenType.Integer))
    { await Write(new { jsonrpc = "2.0", id = id?.Type == JTokenType.String || id?.Type == JTokenType.Integer ? id : null, error = new { code = -32600, message = "Solicitud JSON-RPC inválida." } }); continue; }
    if (id == null)
    {
        if (method == "notifications/cancelled")
        {
            var target = message["params"]?["requestId"]?.ToString(Newtonsoft.Json.Formatting.None);
            if (target != null && pending.TryGetValue(target, out var cancellation)) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        }
        continue;
    }
    if (method == "initialize")
    {
        var initial = message["params"] as JObject;
        if (initial?["protocolVersion"]?.Type != JTokenType.String || initial["capabilities"] is not JObject || initial["clientInfo"] is not JObject)
        { await Write(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "initialize requiere protocolVersion, capabilities y clientInfo." } }); continue; }
        initialized = true;
        var offered = message["params"]?.Value<string>("protocolVersion");
        var supported = new[] { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25" };
        await Write(new { jsonrpc = "2.0", id, result = new { protocolVersion = supported.Contains(offered) ? offered : "2025-11-25", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "cadmcp-2021-compatibility", version = typeof(CadGateway).Assembly.GetName().Version!.ToString(3) }, instructions = "Consulta sesiones, documentos y unidades antes de dibujar. Las herramientas de texto/HATCH requieren puente 0.5.0 y las de grosores/tipos/escalas de línea 0.6.0. El historial de UUID dura solo hasta reiniciar el puente. Usa cad_get_request_status ante resultados inciertos." } });
        continue;
    }
    if (!initialized) { await Write(new { jsonrpc = "2.0", id, error = new { code = -32002, message = "Primero llama initialize." } }); continue; }
    var key = id.ToString(Newtonsoft.Json.Formatting.None);
    var cts = new CancellationTokenSource();
    if (!pending.TryAdd(key, cts)) { cts.Dispose(); await Write(new { jsonrpc = "2.0", id, error = new { code = -32600, message = "ID ya en curso." } }); continue; }
    if (!await slots.WaitAsync(0)) { pending.TryRemove(key, out _); cts.Dispose(); await Write(new { jsonrpc = "2.0", id, error = new { code = -32000, message = "Demasiadas solicitudes concurrentes." } }); continue; }
    var number = Interlocked.Increment(ref sequence);
    var work = Task.Run(async () =>
    {
        try { await Handle(message, cts.Token); }
        finally { pending.TryRemove(key, out _); cts.Dispose(); slots.Release(); }
    });
    tasks[number] = work;
    _ = work.ContinueWith(_ => { tasks.TryRemove(number, out var ignored); }, TaskScheduler.Default);
}
await Task.WhenAll(tasks.Values);

internal static class BoundedLine
{
    private static readonly byte[] Buffer = new byte[8192];
    private static int position;
    private static int length;
    public static async Task<string?> ReadAsync(Stream stream, int max)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (position == length) { length = await stream.ReadAsync(Buffer, 0, Buffer.Length); position = 0; if (length == 0) return line.Length == 0 ? null : new UTF8Encoding(false, true).GetString(line.ToArray()); }
            var b = Buffer[position++];
            if (b == 10) return new UTF8Encoding(false, true).GetString(line.ToArray()).TrimEnd('\r');
            if (line.Length >= max) throw new InvalidDataException("Entrada MCP superior a 4 MiB.");
            line.WriteByte(b);
        }
    }
}
