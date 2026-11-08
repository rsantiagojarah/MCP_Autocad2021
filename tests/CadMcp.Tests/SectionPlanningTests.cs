using System;
using System.Linq;
using CadMcp.Contracts;
using CadMcp.Core;
using CadMcp.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class SectionPlanningTests
{
    [Fact] public void OneMetreSectionsIncludeBothEndsOnce()
        => Assert.Equal(Enumerable.Range(0, 16).Select(i => (double)i), SectionPlanning.Stations(0, 15, 1));
    [Fact] public void NonDivisibleIntervalIncludesLastStation()
        => Assert.Equal(new[] { 100d, 106d, 112d, 115d }, SectionPlanning.Stations(100, 115, 6));
    [Fact] public void FloatingPointEndpointDoesNotCreateDuplicateSampleLines()
    {
        var values = SectionPlanning.Stations(0, 0.07000000000000001, .01);
        Assert.Equal(8, values.Length); Assert.Equal(.07000000000000001, values.Last());
        Assert.All(values.Zip(values.Skip(1), (a, b) => b-a), gap => Assert.True(gap > .009));
    }
    [Fact] public void SingleStationHasOneView() => Assert.Equal(new[] { 12d }, SectionPlanning.Stations(12, 12, 1));
    [Theory] [InlineData(0, 1, 0)] [InlineData(2, 1, 1)] [InlineData(0, 1, -1)] [InlineData(0, 1000, 1)]
    [InlineData(0, double.PositiveInfinity, 1)] [InlineData(0, 10, double.NaN)]
    public void InvalidOrOversizedStationSetsAreRejected(double start, double end, double step)
        => Assert.Throws<CadFault>(() => SectionPlanning.Stations(start, end, step));
    [Fact] public void MaximumIsCheckedIncludingExtraEndpoint()
    {
        Assert.Equal(200, SectionPlanning.Stations(0, 199, 1).Length);
        Assert.Throws<CadFault>(() => SectionPlanning.Stations(0, 199.1, 1));
    }
    [Fact] public void FourColumnsPlaceFifthSectionInNextRow()
    {
        var point = SectionPlanning.Position(new Vec3 { X=100, Y=200, Z=0 }, 4, 4, 20, 15);
        Assert.Equal(100, point.X); Assert.Equal(185, point.Y); Assert.Equal(0, point.Z);
    }
    [Theory] [InlineData(-1, 5, 0, 3)] [InlineData(5, 0, 0, 3)] [InlineData(5, 5, 3, 0)] [InlineData(5, 5, 1, 1)]
    public void InvalidViewRangesFailBeforeCadMutation(double left, double right, double min, double max)
        => Assert.Throws<CadFault>(() => SectionPlanning.Ranges(left,right,min,max));
    [Fact] public void GenerateSchemaRequiresSourcesAtBackendAndRejectsExecutablePayloads()
    {
        var schema = ToolCatalog.All.Single(t => t.Name=="civil_generate_cross_sections").Schema;
        var args = JObject.Parse("{'sessionId':'session','documentId':'doc','operationId':'a5634bfd-46aa-4090-9a18-c66c0f699121','coordinateSpace':'WCS','units':'Meters','alignmentHandle':'A','name':'SECTIONS','interval':1,'position':{'x':100,'y':100,'z':0},'leftWidth':5,'rightWidth':5,'elevationMin':-1,'elevationMax':4,'surfaceHandles':['B'],'solidHandles':['C']}");
        ToolCatalog.Validate(args,schema);
        args["executeCode"]="not permitted"; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(args,schema)); args.Remove("executeCode");
        args["position"]!["z"] = double.NaN; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(args,schema));
    }
    [Fact] public void SolidsToolRejectsMissingUuidAndTooManyViews()
    {
        var schema = ToolCatalog.All.Single(t => t.Name=="civil_add_solid_sections").Schema;
        var args = JObject.Parse("{'sessionId':'s','documentId':'d','coordinateSpace':'WCS','units':'Meters','sectionViewHandles':['A'],'solidHandles':['B']}");
        Assert.Throws<CadFault>(()=>ToolCatalog.Validate(args,schema)); args["operationId"]=Guid.NewGuid().ToString("D"); ToolCatalog.Validate(args,schema);
        args["sectionViewHandles"] = new JArray(Enumerable.Range(0,201).Select(i=>i.ToString("X"))); Assert.Throws<CadFault>(()=>ToolCatalog.Validate(args,schema));
    }
    [Fact] public void ClearingSourcesRequiresExplicitReplaceButEmptyListIsValid()
    {
        var schema = ToolCatalog.All.Single(t=>t.Name=="civil_set_section_sources").Schema;
        var args = JObject.Parse("{'sessionId':'s','documentId':'d','operationId':'a5634bfd-46aa-4090-9a18-c66c0f699121','sampleLineGroupHandle':'A','sourceHandles':[],'replace':true}");
        ToolCatalog.Validate(args,schema);
        args["sourceHandles"]=new JArray(JValue.CreateNull()); Assert.Throws<CadFault>(()=>ToolCatalog.Validate(args,schema));
    }
}
