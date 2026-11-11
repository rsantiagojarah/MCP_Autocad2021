using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using CadMcp.Core;
using CadMcp.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class TextHatchTests
{
    private static JObject Schema(string name) => ToolCatalog.All.Single(t=>t.Name==name).Schema;
    private static JObject WriteArgs() => new JObject { ["sessionId"]="session", ["documentId"]="doc", ["operationId"]=Guid.NewGuid().ToString("D"), ["coordinateSpace"]="WCS", ["units"]="Meters" };
    [Theory]
    [InlineData("cad_upsert_text_style")]
    [InlineData("cad_set_text_style")]
    [InlineData("cad_update_text")]
    [InlineData("cad_create_hatch")]
    [InlineData("cad_update_hatch")]
    public void EveryNewMutationRequiresUuidAndDocument(string name)
    {
        var schema=Schema(name); var required=schema["required"]!.Values<string>().ToArray();
        Assert.Contains("operationId",required); Assert.Contains("documentId",required);
        Assert.Equal("uuid",(string?)schema["properties"]!["operationId"]!["format"]);
        Assert.True(ToolCatalog.All.Single(t=>t.Name==name).Mutates);
    }
    [Fact] public void LegacyCreateTextPayloadStillValidAndNewStyleAndMtextAreAccepted()
    {
        var a=WriteArgs(); a["position"]=JObject.Parse("{'x':0,'y':0,'z':0}"); a["text"]="Legacy"; a["height"]=1; a["layer"]="0";
        ToolCatalog.Validate(a,Schema("cad_create_text"));
        a["styleHandle"]="ABC"; a["type"]="mtext"; a["alignment"]="middleCenter"; ToolCatalog.Validate(a,Schema("cad_create_text"));
        a["type"]="lines"; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_create_text")));
    }
    [Fact] public void SharedStyleUpdateIsExplicitAndAssignmentCannotChangeDefinition()
    {
        var a=WriteArgs(); a["name"]="ArialLocal"; a["font"]="Arial"; a["updateExisting"]=true;
        ToolCatalog.Validate(a,Schema("cad_upsert_text_style")); a["updateExisting"]="yes";
        Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_upsert_text_style")));
        var assignment=new JObject { ["sessionId"]="s", ["documentId"]="d", ["operationId"]=Guid.NewGuid().ToString("D"), ["styleHandle"]="A", ["handles"]=new JArray("B") };
        ToolCatalog.Validate(assignment,Schema("cad_set_text_style")); assignment["font"]="Another font";
        Assert.Throws<CadFault>(()=>ToolCatalog.Validate(assignment,Schema("cad_set_text_style")));
    }
    [Fact] public void HatchContoursColorsAndUnknownArgumentsAreBounded()
    {
        var a=WriteArgs(); a["outerHandles"]=new JArray("A"); a["islandHandles"]=new JArray("B"); a["pattern"]="ANSI31"; a["layer"]="0"; a["colorIndex"]=256;
        ToolCatalog.Validate(a,Schema("cad_create_hatch"));
        a["colorIndex"]=257; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_create_hatch"))); a["colorIndex"]=3;
        a["outerHandles"]=new JArray(); Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_create_hatch")));
        a["outerHandles"]=new JArray(Enumerable.Range(1,51).Select(x=>x.ToString("X"))); Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_create_hatch")));
        a["outerHandles"]=new JArray("A"); a["angle"]=double.NaN; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_create_hatch")));
        a["angle"]=0; a["command"]="HATCH"; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_create_hatch")));
    }
    [Fact] public async Task HatchRetriesDeduplicateAndGatewayRejectsInvalidUuid()
    {
        BridgeRequest? received=null; var engine=new RequestEngine();
        // The core engine preserves a completed hatch creation under exactly the same contract as other mutations.
        var req=new BridgeRequest { Method="cad.create_hatch", DocumentId="doc", Arguments=new JObject { ["outerHandles"]=new JArray("A"), ["pattern"]="ANSI31" } };
        var first=engine.Submit(req); engine.DrainOne(r=>{received=r; return new JObject { ["handle"]="C" };});
        Assert.NotNull(received); Assert.Equal(req.RequestId,received!.RequestId);
        Assert.Same(await first,await engine.Submit(req));
        var altered=Wire.Deserialize<BridgeRequest>(Wire.Serialize(req)); altered.Arguments["pattern"]="SOLID";
        Assert.Equal("REQUEST_ID_CONFLICT",(await engine.Submit(altered)).Error!.Code);
        using var gateway=new CadGateway(()=>Array.Empty<string>()); var a=WriteArgs(); a["handle"]="A"; a["scale"]=2; a["operationId"]="bad";
        Assert.Equal("INVALID_ARGUMENT",(await gateway.CallAsync("cad_update_hatch",a,CancellationToken.None)).Error!.Code);
    }
}
