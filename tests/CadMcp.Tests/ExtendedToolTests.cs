using System;
using System.Linq;
using CadMcp.Contracts;
using CadMcp.Core;
using CadMcp.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class ExtendedToolTests
{
    private static JObject Base(bool write = true) => new JObject { ["sessionId"] = "cadmcp-0000000000000000-1", ["documentId"] = "doc", ["operationId"] = Guid.NewGuid().ToString(), ["coordinateSpace"] = "WCS", ["units"] = "Meters" };
    private static JObject Schema(string name) => ToolCatalog.All.Single(t => t.Name == name).Schema;
    [Fact] public void ExtendedCatalogIsCompleteAndMethodsUnique()
    {
        Assert.Equal(71, ToolCatalog.All.Count);
        Assert.Equal(71, ToolCatalog.All.Select(t => t.Method).Distinct().Count());
        var all = ToolCatalog.All.Where(t => t.Mutates);
        foreach (var tool in all) Assert.Contains("operationId", tool.Schema["required"]!.Values<string>());
    }
    [Theory]
    [InlineData("cad_create_retaining_wall")]
    [InlineData("civil_create_tin_surface")]
    [InlineData("civil_create_alignment")]
    [InlineData("civil_create_layout_profile")]
    [InlineData("civil_add_network_parts")]
    [InlineData("civil_create_corridor")]
    public void NewMutationsRejectMissingArgumentsAndArbitraryCode(string name)
    {
        var a = Base(); Assert.Throws<CadFault>(() => ToolCatalog.Validate(a, Schema(name)));
        a["executeCode"] = "not allowed"; Assert.Throws<CadFault>(() => ToolCatalog.Validate(a, Schema(name)));
    }
    [Fact] public void WallRequiresExplicitDimensionsAndSource()
    {
        var a = Base(); a["sourceHandle"] = "6D43";
        foreach (var pair in new[] { ("stemThickness", .25), ("stemHeight", 3.0), ("heel", 2.0), ("toe", 2.0), ("footingThickness", .5) }) a[pair.Item1] = pair.Item2;
        ToolCatalog.Validate(a, Schema("cad_create_retaining_wall"));
        a["coordinateSpace"] = "UCS"; Assert.Throws<CadFault>(() => ToolCatalog.Validate(a, Schema("cad_create_retaining_wall")));
        a["coordinateSpace"] = "WCS"; a.Remove("toe"); Assert.Throws<CadFault>(() => ToolCatalog.Validate(a, Schema("cad_create_retaining_wall")));
    }
    [Fact] public void CatalogRejectsMissingNestedPipeCoordinates()
    {
        var a = Base(); a["networkHandle"] = "A";
        a["pipes"] = JArray.Parse("[{'familyHandle':'B','sizeHandle':'C','start':{'x':0,'y':0,'z':0},'end':{'x':10,'y':0}}]");
        Assert.Throws<CadFault>(() => ToolCatalog.Validate(a, Schema("civil_add_network_parts")));
        a["pipes"]![0]!["end"]!["z"] = 0; ToolCatalog.Validate(a, Schema("civil_add_network_parts"));
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void PositiveDimensionsRejectInvalidValues(double number)
    { Assert.Throws<CadFault>(() => ToolArgs.Number(new JObject { ["height"] = number }, "height", positive: true)); }
    [Fact] public void InternalArgumentsRejectNullListsDuplicatesAndMalformedPoints()
    {
        Assert.Throws<CadFault>(() => ToolArgs.Handles(JObject.Parse("{'handles':['a','A']}")));
        Assert.Throws<CadFault>(() => ToolArgs.Handles(JObject.Parse("{'handles':[null]}")));
        Assert.Throws<CadFault>(() => ToolArgs.Point(JObject.Parse("{'position':{'x':0,'y':0}}"), "position"));
        Assert.Throws<CadFault>(() => ToolArgs.Page(JObject.Parse("{'limit':501}")));
        Assert.Throws<CadFault>(() => ToolArgs.Page(JObject.Parse("{'offset':1.2}")));
    }
    [Fact] public void ReadToolsNeverRequireOperationId()
    {
        foreach (var tool in ToolCatalog.All.Where(t => !t.Mutates)) Assert.DoesNotContain("operationId", tool.Schema["required"]!.Values<string>());
    }
    [Fact] public void NewNestedSpecsDeserializeWithStrictUnknownPropertyRejection()
    {
        Assert.Throws<CadFault>(() => Validation.Arguments<StationOffsetSpec>(JObject.Parse("{'station':0,'offset':2,'code':'x'}")));
        Assert.Throws<CadFault>(() => Validation.Arguments<StationOffsetSpec>(JObject.Parse("{'station':0}")));
        Assert.Throws<CadFault>(() => Validation.Arguments<Vec3>(JObject.Parse("{'x':0,'y':0}")));
        Assert.Throws<CadFault>(() => Validation.Arguments<PipeSpec>(JObject.Parse("{'familyHandle':'A','sizeHandle':'B'}")));
        Assert.Equal(2, Validation.Arguments<StationOffsetSpec>(JObject.Parse("{'station':0,'offset':2}")).Offset);
    }
}
