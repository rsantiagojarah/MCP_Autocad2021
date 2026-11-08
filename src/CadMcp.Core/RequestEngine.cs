using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CadMcp.Contracts;
using Newtonsoft.Json.Linq;

namespace CadMcp.Core;

// Autodesk code is never called here. DrainOne must be invoked on the CAD main thread.
public sealed class RequestEngine
{
    private sealed class Entry
    {
        public BridgeRequest Request = null!;
        public string Fingerprint = "";
        public string State = "queued";
        public bool Retain;
        public long Enqueued = Stopwatch.GetTimestamp();
        public readonly TaskCompletionSource<BridgeResponse> Completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object sync = new object();
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly Queue<Entry> queue = new Queue<Entry>();
    private readonly int capacity;
    private readonly int queueCapacity;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<string, bool> isMutation;
    private readonly long journalByteCapacity;
    private long journalBytes;
    private bool stopped;

    public RequestEngine(int capacity = 10000, int queueCapacity = 128, Func<DateTimeOffset>? utcNow = null,
        Func<string, bool>? isMutation = null, long journalByteCapacity = 64L * 1024 * 1024)
    {
        this.capacity = capacity;
        this.queueCapacity = queueCapacity;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.isMutation = isMutation ?? (_ => true);
        this.journalByteCapacity = journalByteCapacity;
    }

    public Task<BridgeResponse> Submit(BridgeRequest request)
    {
        if (request.Version != Wire.Version) return Task.FromResult(BridgeResponse.Fail(request.RequestId, "PROTOCOL_MISMATCH", "Versión interna incompatible."));
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || request.Arguments == null || string.IsNullOrWhiteSpace(request.Method))
            return Task.FromResult(BridgeResponse.Fail(request.RequestId, "INVALID_ARGUMENT", "requestId debe ser UUID y method/arguments son obligatorios."));
        request.RequestId = Guid.Parse(request.RequestId).ToString("D");
        if (request.Method == "request.status") return Task.FromResult(Status(request));
        var fingerprint = Fingerprint(request);
        lock (sync)
        {
            if (entries.TryGetValue(request.RequestId, out var previous))
                return previous.Fingerprint == fingerprint ? previous.Completion.Task : Task.FromResult(BridgeResponse.Fail(request.RequestId, "REQUEST_ID_CONFLICT", "El identificador ya corresponde a otra operación."));
            if (stopped) return Task.FromResult(BridgeResponse.Fail(request.RequestId, "BRIDGE_STOPPED", "El puente está detenido."));
            if (request.ExpiresAtUtc <= utcNow()) return Task.FromResult(BridgeResponse.Fail(request.RequestId, "EXPIRED", "La solicitud venció antes de entrar a la cola."));
            if (queue.Count >= queueCapacity) return Task.FromResult(BridgeResponse.Fail(request.RequestId, "QUEUE_FULL", "Cola ocupada; la solicitud no fue aceptada."));
            if (entries.Count >= capacity) return Task.FromResult(BridgeResponse.Fail(request.RequestId, "JOURNAL_FULL", "Se alcanzó el límite de solicitudes de esta sesión; reinicia el puente después de resolver las pendientes."));
            var retain = isMutation(request.Method);
            if (retain && journalBytes + Wire.MaxFrameBytes > journalByteCapacity)
                return Task.FromResult(BridgeResponse.Fail(request.RequestId, "JOURNAL_FULL", "El historial alcanzó su presupuesto de memoria; la operación no fue aceptada."));
            // Freeze a copy: callers cannot mutate queued payloads after validation/fingerprinting.
            var entry = new Entry { Request = Wire.Deserialize<BridgeRequest>(Wire.Serialize(request)), Fingerprint = fingerprint, Retain = retain };
            if (retain) journalBytes += Wire.MaxFrameBytes; // reserve the maximum response size before accepting a write
            entries.Add(request.RequestId, entry);
            queue.Enqueue(entry);
            return entry.Completion.Task;
        }
    }

    public bool DrainOne(Func<BridgeRequest, JToken> execute)
    {
        Entry entry;
        lock (sync)
        {
            if (queue.Count == 0 || stopped) return false;
            entry = queue.Dequeue();
            entry.State = "running";
        }
        var watch = Stopwatch.StartNew();
        var wait = (Stopwatch.GetTimestamp() - entry.Enqueued) * 1000.0 / Stopwatch.Frequency;
        BridgeResponse response;
        try
        {
            response = entry.Request.ExpiresAtUtc <= utcNow()
                ? BridgeResponse.Fail(entry.Request.RequestId, "EXPIRED", "La solicitud venció en cola y no se ejecutó.")
                : BridgeResponse.Success(entry.Request.RequestId, execute(entry.Request));
        }
        catch (CadFault ex) { response = BridgeResponse.Fail(entry.Request.RequestId, ex.Code, ex.Message); }
        catch (Exception ex) { response = BridgeResponse.Fail(entry.Request.RequestId, "CAD_ERROR", ex.Message); }
        response.QueueMilliseconds = wait;
        response.ExecutionMilliseconds = watch.Elapsed.TotalMilliseconds;
        var retainedBytes = entry.Retain ? Encoding.UTF8.GetByteCount(Wire.Serialize(response)) : 0;
        lock (sync)
        {
            if (entry.Retain) journalBytes += retainedBytes - Wire.MaxFrameBytes;
            else entries.Remove(entry.Request.RequestId);
            entry.Request.Arguments = new JObject(); // release potentially large input payload after execution
            entry.State = response.Ok ? "completed" : "failed";
            entry.Completion.TrySetResult(response);
        }
        return true;
    }

    private BridgeResponse Status(BridgeRequest request)
    {
        var id = (string?)request.Arguments["requestId"] ?? "";
        if (Guid.TryParse(id, out var parsed)) id = parsed.ToString("D");
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry)) return BridgeResponse.Fail(request.RequestId, "NOT_FOUND", "No existe en el historial de este arranque del puente. No presupongas que una operación anterior no se ejecutó.");
            return BridgeResponse.Success(request.RequestId, new { requestId = id, state = entry.State,
                response = entry.Completion.Task.IsCompleted ? entry.Completion.Task.Result : null });
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            stopped = true;
            while (queue.Count > 0)
            {
                var entry = queue.Dequeue(); entry.State = "failed";
                if (entry.Retain) journalBytes -= Wire.MaxFrameBytes;
                entry.Request.Arguments = new JObject();
                entry.Completion.TrySetResult(BridgeResponse.Fail(entry.Request.RequestId, "BRIDGE_STOPPED", "El puente se detuvo antes de ejecutar la solicitud."));
            }
        }
    }

    private static string Fingerprint(BridgeRequest request)
    {
        var canonical = Canonical(new JObject { ["method"] = request.Method, ["document"] = request.DocumentId, ["arguments"] = request.Arguments }).ToString(Newtonsoft.Json.Formatting.None);
        using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JToken Canonical(JToken token)
    {
        if (token is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))));
        if (token is JArray array) return new JArray(array.Select(Canonical));
        return token.DeepClone();
    }
}
