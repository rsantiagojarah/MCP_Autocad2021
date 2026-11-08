using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;

namespace CadMcp.Core;

public static class PipeNames
{
    public static string Prefix
    {
        get
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CadMcp requiere Windows.");
#endif
            using (var identity = WindowsIdentity.GetCurrent())
            using (var hash = SHA256.Create())
            {
                var sid = identity.User?.Value ?? throw new InvalidOperationException("No se obtuvo el SID del usuario.");
                return "cadmcp-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sid))).Replace("-", "").Substring(0, 16).ToLowerInvariant() + "-";
            }
        }
    }
}

public sealed class PipeBridgeServer : IDisposable
{
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> streams = new ConcurrentDictionary<NamedPipeServerStream, byte>();
    private readonly Func<NamedPipeServerStream> create;
    private readonly RequestEngine engine;
    public PipeBridgeServer(Func<NamedPipeServerStream> create, RequestEngine engine) { this.create = create; this.engine = engine; }
    public void Start(int listeners = 4)
    {
        for (var i = 0; i < listeners; i++) _ = Task.Run(ListenAsync);
    }
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = create(); streams.TryAdd(pipe, 0);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                while (!stop.IsCancellationRequested && pipe.IsConnected)
                {
                    var request = await Framing.ReadAsync<BridgeRequest>(pipe, stop.Token).ConfigureAwait(false);
                    if (request == null) break;
                    var work = engine.Submit(request);
                    var remaining = request.ExpiresAtUtc - DateTimeOffset.UtcNow;
                    var delay = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(30000, remaining.TotalMilliseconds))), stop.Token);
                    var completed = await Task.WhenAny(work, delay).ConfigureAwait(false);
                    var response = completed == work ? await work.ConfigureAwait(false)
                        : BridgeResponse.Fail(request.RequestId, "PENDING", "No hay resultado definitivo. Consulta request.status con el mismo identificador; no repitas la modificación con otro UUID.");
                    await Framing.WriteAsync(pipe, response, stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is CadFault || ex is Newtonsoft.Json.JsonException || ex is ArgumentException)
            { /* Connection failure never retries the submitted operation. */ }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("CadMcp pipe: " + ex);
                try { await Task.Delay(250, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            finally
            {
                if (pipe != null) { streams.TryRemove(pipe, out _); pipe.Dispose(); }
            }
        }
    }
    public void Dispose()
    {
        stop.Cancel(); engine.Stop();
        foreach (var pipe in streams.Keys) pipe.Dispose();
        // CTS remains alive until async listeners observe cancellation.
    }
}
