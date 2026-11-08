using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using CadMcp.Core;
using CadMcp.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task DiscoveryDiagnosesAnotherWindowsUserWithoutConnectingToItsPipe()
    {
        var foreignHash = PipeNames.Prefix.Contains("0000000000000000") ? "1111111111111111" : "0000000000000000";
        var foreignName = "cadmcp-" + foreignHash + "-123";
        using var gateway = new CadGateway(() => new[] { foreignName, foreignName, "cadmcp-invalid", "unrelated" });
        var result = await gateway.CallAsync("cad_list_sessions", new JObject(), default);
        Assert.True(result.Ok);
        Assert.Empty((JArray)result.Result!["sessions"]!);
        Assert.Equal("windows_user_mismatch", result.Result["connectionState"]!.Value<string>());
        Assert.Equal(1, result.Result["otherUserBridgeCount"]!.Value<int>());
        Assert.Equal(0, result.Result["unavailable"]!.Value<int>());
        // The mismatch never authorizes a subsequent call to that foreign session.
        var denied = await gateway.CallAsync("cad_list_documents", new JObject { ["sessionId"] = foreignName }, default);
        Assert.Equal("INVALID_ARGUMENT", denied.Error!.Code);
    }

    [Fact]
    public async Task EmptyDiscoveryReportsBridgeNotStarted()
    {
        using var gateway = new CadGateway(() => Array.Empty<string>());
        var result = await gateway.CallAsync("cad_list_sessions", new JObject(), default);
        Assert.Equal("bridge_not_started", result.Result!["connectionState"]!.Value<string>());
        Assert.Equal(0, result.Result["otherUserBridgeCount"]!.Value<int>());
    }

    [Fact]
    public async Task DiscoveryReportsConnectedWhenOwnBridgeResponds()
    {
        var name = PipeNames.Prefix + (Environment.ProcessId + 200000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var service = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Framing.ReadAsync<BridgeRequest>(server, timeout.Token);
            await Framing.WriteAsync(server, BridgeResponse.Success(request!.RequestId, new { sessionId = name }), timeout.Token);
        });
        using var gateway = new CadGateway(() => new[] { name, "cadmcp-0000000000000000-123" });
        var result = await gateway.CallAsync("cad_list_sessions", new JObject(), timeout.Token);
        Assert.Equal("connected", result.Result!["connectionState"]!.Value<string>());
        Assert.Single((JArray)result.Result["sessions"]!);
        await service;
    }

    private sealed class FragmentedStream : MemoryStream
    {
        public FragmentedStream(byte[] bytes) : base(bytes) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => base.ReadAsync(buffer, offset, Math.Min(count, 1), token);
    }
    [Fact]
    public async Task FramesHandleFragmentationUnicodeAndMultipleMessages()
    {
        using var stream = new MemoryStream();
        var a = new BridgeRequest { Method = "consulta-ñ-地形" }; var b = new BridgeRequest { Method = "second" };
        await Framing.WriteAsync(stream, a, default); await Framing.WriteAsync(stream, b, default);
        using var fragmented = new FragmentedStream(stream.ToArray());
        Assert.Equal(a.Method, (await Framing.ReadAsync<BridgeRequest>(fragmented, default))!.Method);
        Assert.Equal(b.Method, (await Framing.ReadAsync<BridgeRequest>(fragmented, default))!.Method);
        Assert.Null(await Framing.ReadAsync<BridgeRequest>(fragmented, default));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(Wire.MaxFrameBytes + 1)]
    public async Task InvalidLengthsAreRejectedBeforeAllocatingPayload(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        Assert.Equal("INVALID_FRAME", (await Assert.ThrowsAsync<CadFault>(() => Framing.ReadAsync<BridgeRequest>(stream, default))).Code);
    }
    [Fact]
    public async Task TruncatedFramesFail()
    {
        using var stream = new MemoryStream(new byte[] { 3, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync<BridgeRequest>(stream, default));
    }
    [Fact]
    public async Task PersistentRealPipeCarriesMultipleCorrelatedRequests()
    {
        var name = PipeNames.Prefix + Environment.ProcessId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var service = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            for (var i = 0; i < 3; i++)
            {
                var request = await Framing.ReadAsync<BridgeRequest>(server, timeout.Token);
                Assert.NotNull(request);
                await Framing.WriteAsync(server, BridgeResponse.Success(request!.RequestId, new { sequence = i }), timeout.Token);
            }
        });
        using var gateway = new CadGateway(() => new[] { name });
        for (var i = 0; i < 3; i++)
        {
            var result = await gateway.CallAsync("cad_list_documents", new JObject { ["sessionId"] = name }, timeout.Token);
            Assert.True(result.Ok, result.Error?.Message); Assert.Equal(i, result.Result!["sequence"]!.Value<int>());
        }
        await service;
    }
    [Fact]
    public async Task BrokenPipeAfterAcceptanceNeverSilentlyRetries()
    {
        var name = PipeNames.Prefix + (Environment.ProcessId + 100000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); var request = await Framing.ReadAsync<BridgeRequest>(server, timeout.Token); server.Disconnect(); return request; });
        using var gateway = new CadGateway(() => new[] { name });
        var id = Guid.NewGuid().ToString("D");
        var result = await gateway.CallAsync("cad_upsert_layers", new JObject { ["sessionId"] = name, ["documentId"] = "doc", ["operationId"] = id,
            ["layers"] = new JArray(new JObject { ["name"] = "Test", ["colorIndex"] = 7 }) }, timeout.Token);
        Assert.Equal("OUTCOME_UNKNOWN", result.Error!.Code); Assert.Equal(id, result.RequestId);
        Assert.Equal(id, (await accepted)!.RequestId);
    }
}
