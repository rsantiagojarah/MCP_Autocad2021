using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CadMcp.Contracts;
using CadMcp.Core;
using CadMcp.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void SchemaRejectsMissingCoordinatesAndUnknownProperties()
    {
        var schema = ToolCatalog.All.Single(t => t.Name == "cad_create_entities").Schema;
        var args = JObject.Parse("{'sessionId':'s','documentId':'d','operationId':'" + Guid.NewGuid() + "','coordinateSpace':'WCS','units':'Meters','entities':[{'type':'point','layer':'0','points':[{'x':0,'y':0}]}]}");
        Assert.Throws<CadFault>(() => ToolCatalog.Validate(args, schema));
        args["entities"]![0]!["points"]![0]!["z"] = 0;
        ToolCatalog.Validate(args, schema);
        args["executeCode"] = "forbidden"; Assert.Throws<CadFault>(() => ToolCatalog.Validate(args, schema));
    }
    [Theory]
    [InlineData("UCS", 0.0, 1.0)] [InlineData("WCS", double.NaN, 1.0)] [InlineData("WCS", 0.0, double.PositiveInfinity)]
    public void GeometryRejectsUnsupportedCoordinatesOrNonfiniteNumbers(string space, double x, double end)
    {
        var args = new CreateEntitiesArgs { CoordinateSpace = space, Units = "Meters", Entities = new List<EntitySpec> { new EntitySpec { Type = "line", Points = new List<Vec3> { new Vec3 { X = x }, new Vec3 { X = end } } } } };
        Assert.Throws<CadFault>(() => Validation.Geometry(args));
    }
    [Fact]
    public void GeometryRejectsNonPlanarPolyline()
    {
        var args = new CreateEntitiesArgs { CoordinateSpace = "WCS", Units = "Meters", Entities = new List<EntitySpec> { new EntitySpec { Type = "polyline", Points = new List<Vec3> { new Vec3(), new Vec3 { X = 1, Z = 1 } } } } };
        Assert.Throws<CadFault>(() => Validation.Geometry(args));
    }
    [Fact]
    public void QueryRequiresCompleteOrderedBounds()
    {
        Assert.Throws<CadFault>(() => Validation.Query(new QueryEntitiesArgs { Min = new Vec3() }));
        Assert.Throws<CadFault>(() => Validation.Query(new QueryEntitiesArgs { Min = new Vec3 { X = 1 }, Max = new Vec3() }));
    }
    [Fact]
    public void ToolNamesAreUniqueAndEveryMutationRequiresOperationId()
    {
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Name).Distinct().Count());
        foreach (var tool in ToolCatalog.All.Where(t => t.Mutates)) Assert.Contains("operationId", tool.Schema["required"]!.Values<string>());
    }
    [Fact]
    public async Task InvalidOperationIdShapeReturnsErrorInsteadOfThrowing()
    {
        using var gateway = new CadGateway(() => Array.Empty<string>());
        var result = await gateway.CallAsync("cad_create_entities", new JObject { ["operationId"] = new JObject() }, default);
        Assert.False(result.Ok); Assert.Equal("INVALID_ARGUMENT", result.Error!.Code);
    }
}
