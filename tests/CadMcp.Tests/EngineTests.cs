using System;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class EngineTests
{
    private static BridgeRequest Request() => new BridgeRequest { Method = "write", DocumentId = "doc", Arguments = JObject.Parse("{ 'a': 1, 'b': 2 }") };
    [Fact]
    public async Task ConcurrentRetriesExecuteExactlyOnce()
    {
        var engine = new RequestEngine(); var request = Request();
        var first = engine.Submit(request);
        var attempts = new Task<BridgeResponse>[32];
        Parallel.For(0, attempts.Length, i => attempts[i] = engine.Submit(Wire.Deserialize<BridgeRequest>(Wire.Serialize(request))));
        var count = 0;
        engine.DrainOne(_ => { Interlocked.Increment(ref count); return new JObject { ["created"] = 10 }; });
        foreach (var result in await Task.WhenAll(attempts)) Assert.Same(await first, result);
        Assert.Equal(1, count); Assert.False(engine.DrainOne(_ => throw new Exception()));
        Assert.Same(await first, await engine.Submit(request));
    }
    [Fact]
    public async Task ReorderedPropertiesAndUuidCasingDoNotDuplicate()
    {
        var engine = new RequestEngine(); var request = Request(); var first = engine.Submit(request);
        var retry = Wire.Deserialize<BridgeRequest>(Wire.Serialize(request)); retry.RequestId = retry.RequestId.ToUpperInvariant();
        retry.Arguments = JObject.Parse("{'b': 2, 'a': 1}");
        var second = engine.Submit(retry); engine.DrainOne(_ => new JObject());
        Assert.Same(await first, await second);
    }
    [Fact]
    public async Task IdCannotBeReusedForDifferentPayloadOrDocument()
    {
        var engine = new RequestEngine(); var request = Request(); _ = engine.Submit(request);
        request.DocumentId = "different";
        Assert.Equal("REQUEST_ID_CONFLICT", (await engine.Submit(request)).Error!.Code);
    }
    [Fact]
    public async Task QueueExpirationPreventsExecution()
    {
        var now = DateTimeOffset.UtcNow;
        var engine = new RequestEngine(utcNow: () => now); var request = Request(); request.ExpiresAtUtc = now.AddMinutes(1);
        var work = engine.Submit(request);
        var expired = Request(); expired.ExpiresAtUtc = now.AddSeconds(-1);
        Assert.Equal("EXPIRED", (await engine.Submit(expired)).Error!.Code);
        now = now.AddMinutes(2);
        Assert.True(engine.DrainOne(_ => throw new Exception("Must not execute")));
        Assert.Equal("EXPIRED", (await work).Error!.Code);
        Assert.False(engine.DrainOne(_ => throw new Exception("Must not execute")));
    }
    [Fact]
    public async Task StopRejectsQueuedAndFutureWork()
    {
        var engine = new RequestEngine(); var work = engine.Submit(Request()); engine.Stop();
        Assert.Equal("BRIDGE_STOPPED", (await work).Error!.Code);
        Assert.Equal("BRIDGE_STOPPED", (await engine.Submit(Request())).Error!.Code);
    }
    [Fact]
    public async Task QueueAndHistoryHaveIndependentBounds()
    {
        var engine = new RequestEngine(capacity: 2, queueCapacity: 1);
        _ = engine.Submit(Request());
        Assert.Equal("QUEUE_FULL", (await engine.Submit(Request())).Error!.Code);
        engine.DrainOne(_ => new JObject()); _ = engine.Submit(Request()); engine.DrainOne(_ => new JObject());
        Assert.Equal("JOURNAL_FULL", (await engine.Submit(Request())).Error!.Code);
    }
    [Fact]
    public async Task StatusWorksWhileCadOperationIsRunning()
    {
        var engine = new RequestEngine(); var request = Request(); var work = engine.Submit(request);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var draining = Task.Run(() => engine.DrainOne(_ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return new JObject { ["count"] = 1 }; }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var status = await engine.Submit(new BridgeRequest { Method = "request.status", Arguments = new JObject { ["requestId"] = request.RequestId } });
        Assert.Equal("running", status.Result!["state"]!.Value<string>());
        release.Set(); await draining; Assert.True((await work).Ok);
        status = await engine.Submit(new BridgeRequest { Method = "request.status", Arguments = new JObject { ["requestId"] = request.RequestId } });
        Assert.Equal("completed", status.Result!["state"]!.Value<string>());
        Assert.True(status.Result["response"]!["ok"]!.Value<bool>());
    }
    [Fact]
    public async Task FailureIsRememberedAndNotReexecuted()
    {
        var engine = new RequestEngine(); var request = Request(); var work = engine.Submit(request);
        engine.DrainOne(_ => throw new CadFault("UNIT_MISMATCH", "meters"));
        Assert.Equal("UNIT_MISMATCH", (await work).Error!.Code);
        Assert.Same(await work, await engine.Submit(request));
    }
    [Fact]
    public async Task QueuedPayloadIsImmutableToCaller()
    {
        var engine = new RequestEngine(); var request = Request(); var work = engine.Submit(request);
        request.Arguments["a"] = 999;
        engine.DrainOne(r => { Assert.Equal(1, r.Arguments["a"]!.Value<int>()); return new JObject(); });
        Assert.True((await work).Ok);
    }
    [Fact]
    public async Task CompletedReadsReleaseHistoryWhileWritesRemainDeduplicated()
    {
        var engine = new RequestEngine(capacity: 1, isMutation: method => method == "write");
        var read = Request(); read.Method = "read";
        var work = engine.Submit(read); engine.DrainOne(_ => new JObject()); Assert.True((await work).Ok);
        var write = Request(); work = engine.Submit(write); engine.DrainOne(_ => new JObject()); Assert.True((await work).Ok);
        Assert.Equal("JOURNAL_FULL", (await engine.Submit(Request())).Error!.Code);
        Assert.Same(await work, await engine.Submit(write));
    }
    [Fact]
    public async Task MutationMemoryIsReservedBeforeAcceptance()
    {
        var engine = new RequestEngine(journalByteCapacity: Wire.MaxFrameBytes);
        var first = engine.Submit(Request());
        Assert.Equal("JOURNAL_FULL", (await engine.Submit(Request())).Error!.Code);
        engine.DrainOne(_ => new JObject()); Assert.True((await first).Ok);
    }
}
