using System;
using System.Linq;
using CadMcp.Contracts;
using CadMcp.Core;
using CadMcp.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadMcp.Tests;

public sealed class LineGraphicsTests
{
    private static JObject Schema(string name)=>ToolCatalog.All.Single(t=>t.Name==name).Schema;
    private static JObject Args()=>new JObject { ["sessionId"]="s",["documentId"]="d",["operationId"]=Guid.NewGuid().ToString("D") };
    [Theory] [InlineData(25)] [InlineData(0)] [InlineData(211)] [InlineData(-1)] [InlineData(-2)] [InlineData(-3)]
    public void NativeLineweightCodesAreAccepted(int code)=>LineGraphicsRules.Weight(code);
    [Theory] [InlineData(27)] [InlineData(-4)] [InlineData(212)] [InlineData(int.MaxValue)]
    public void ArbitraryMillimetreFractionsAreNotRounded(int code)=>Assert.Throws<CadFault>(()=>LineGraphicsRules.Weight(code));
    [Theory] [InlineData(-1)] [InlineData(-2)]
    public void LayersCannotInheritTheirOwnLineweight(int code)=>Assert.Throws<CadFault>(()=>LineGraphicsRules.Weight(code,true));
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void ScalesMustBePositiveFinite(double scale)=>Assert.Throws<CadFault>(()=>LineGraphicsRules.Scale(scale));
    [Theory] [InlineData("*")] [InlineData("DASH*")] [InlineData("X|CENTER")] [InlineData("")]
    public void ExplicitLinetypeNamesRejectWildcardsAndReferences(string name)=>Assert.Throws<CadFault>(()=>LineGraphicsRules.Name(name));
    [Fact] public void LegacyPayloadsRemainValidWithOptionalNewProperties()
    {
        var layer=Args(); layer["layers"]=JArray.Parse("[{'name':'WALL','colorIndex':3}]"); ToolCatalog.Validate(layer,Schema("cad_upsert_layers"));
        layer["layers"]![0]!["lineweight"]=25; layer["layers"]![0]!["linetype"]="DASHED"; ToolCatalog.Validate(layer,Schema("cad_upsert_layers"));
        layer["layers"]![0]!["lineweight"]=-1; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(layer,Schema("cad_upsert_layers")));
        var entity=Args(); entity["entities"]=JArray.Parse("[{'handle':'A','colorIndex':2}]"); ToolCatalog.Validate(entity,Schema("cad_update_entities"));
        entity["entities"]![0]!["lineweight"]=25; entity["entities"]![0]!["linetype"]="ByLayer"; entity["entities"]![0]!["linetypeScale"]=2;
        ToolCatalog.Validate(entity,Schema("cad_update_entities")); entity["entities"]![0]!["lineweight"]=27;
        Assert.Throws<CadFault>(()=>ToolCatalog.Validate(entity,Schema("cad_update_entities")));
    }
    [Theory]
    [InlineData("cad_load_linetypes")][InlineData("cad_set_line_properties")][InlineData("cad_set_layer_line_properties")][InlineData("cad_set_line_settings")]
    public void NewWritesRequireDocumentAndUuid(string name)
    {
        var t=ToolCatalog.All.Single(t=>t.Name==name); Assert.True(t.Mutates);
        Assert.Contains("operationId",t.Schema["required"]!.Values<string>()); Assert.Contains("documentId",t.Schema["required"]!.Values<string>());
        Assert.Equal("uuid",(string?)t.Schema["properties"]!["operationId"]!["format"]);
    }
    [Fact] public void LoadLinetypesRejectsEmptyNamesAndExecutableArguments()
    {
        var a=Args(); a["file"]="acadiso.lin"; a["names"]=new JArray("DASHED","CENTER"); ToolCatalog.Validate(a,Schema("cad_load_linetypes"));
        a["executeCode"]="not permitted"; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_load_linetypes"))); a.Remove("executeCode");
        a["names"]=new JArray(); Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_load_linetypes")));
    }
    [Fact] public void LayerScaleIsNotAdvertisedAsANativeLayerProperty()
    {
        var a=Args(); a["layers"]=new JArray("WALL"); a["lineweight"]=25; ToolCatalog.Validate(a,Schema("cad_set_layer_line_properties"));
        a["linetypeScale"]=2; Assert.Throws<CadFault>(()=>ToolCatalog.Validate(a,Schema("cad_set_layer_line_properties")));
    }
}
