using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly:CommandClass(typeof(CadMcp.LineGraphics.Native.LineCheckCommand))]
namespace CadMcp.LineGraphics.Native;

public sealed class LineCheckCommand
{
    [CommandMethod("CADMCPLINECHECK",CommandFlags.Session)]
    public static void Execute()
    {
        var directory=Path.GetDirectoryName(typeof(LineCheckCommand).Assembly.Location)!;
        var reportPath=Path.Combine(directory,"line-graphics-native.json");
        var report=new JObject { ["passed"]=false,["recordedUtc"]=DateTimeOffset.UtcNow.ToString("o"),["transport"]="native-in-process; linked production sources" };
        using(var sha=System.Security.Cryptography.SHA256.Create()) report["assemblySha256"]=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(typeof(LineCheckCommand).Assembly.Location))).Replace("-","");
        var checks=new JArray(); report["checks"]=checks;
        try
        {
            var manager=AcApp.DocumentManager; var original=manager.MdiActiveDocument;
            report["originalDrawing"]=original?.Name;
            string template;
            try { template=HostApplicationServices.Current.FindFile("acadiso.dwt",original?.Database,FindFileHint.Default); }
            catch(Autodesk.AutoCAD.Runtime.Exception)
            {
                template=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Autodesk","C3D 2021","enu","Template","AutoCAD Template","acadiso.dwt");
            }
            if(!File.Exists(template)) throw new InvalidOperationException("No se encontró acadiso.dwt. No se modificó ningún dibujo.");
            var doc=manager.Add(template); manager.MdiActiveDocument=doc;
            report["testDrawing"]=doc.Name;
            var registry=new OperationRegistry(); new CadOperations(registry).Register(); var engine=new RequestEngine(isMutation:registry.IsMutation);
            BridgeRequest Request(string method,JObject a)=>new BridgeRequest { Method=method,DocumentId=DocumentAccess.Id(doc),Arguments=a };
            BridgeResponse Send(BridgeRequest r) { var result=engine.Submit(r); if(!result.IsCompleted) engine.DrainOne(registry.Execute); return result.GetAwaiter().GetResult(); }
            JToken Call(string method,JObject a) { var result=Send(Request(method,a)); if(!result.Ok) throw new InvalidOperationException(method+": "+result.Error!.Code+" "+result.Error.Message); return result.Result!; }
            void Check(bool ok,string name) { if(!ok) throw new InvalidOperationException(name); checks.Add(name); }
            void Reject(string method,JObject a,string code) { var result=Send(Request(method,a)); Check(!result.Ok && result.Error!.Code==code,method+" rejects "+code); }
            var settings=(JObject)Call("cad.line_settings",new JObject());
            var weights=(JArray)Call("cad.lineweights",new JObject())["lineweights"]!;
            Check(weights.Any(w=>w["code"]!.Value<int>()==25 && w["millimetres"]!.Value<double>()==.25),"0.25 mm native code listed");
            var defs=Call("cad.linetype_definitions",new JObject { ["file"]="acadiso.lin",["limit"]=500 });
            Check(((JArray)defs["definitions"]!).Any(x=>(string?)x["name"]=="DASHED"),"DASHED found in Autodesk LIN");
            var loadRequest=Request("cad.load_linetypes",new JObject { ["file"]="acadiso.lin",["names"]=new JArray("DASHED","CENTER") });
            var loaded=Send(loadRequest); Check(loaded.Ok,"Native DASHED and CENTER loaded atomically");
            Check(ReferenceEquals(loaded,Send(loadRequest)),"LIN retry deduplicated");
            var repeated=Call("cad.load_linetypes",loadRequest.Arguments);
            Check(((JArray)repeated["loaded"]!).Count==0 && ((JArray)repeated["alreadyLoaded"]!).Count==2,"Existing shared linetypes preserved");
            var layer="MCP_LINE_"+Guid.NewGuid().ToString("N").Substring(0,8);
            Call("cad.upsert_layers",new JObject { ["layers"]=new JArray(new JObject { ["name"]=layer,["colorIndex"]=3,["linetype"]="CENTER",["lineweight"]=50 }) });
            var create=new JObject { ["coordinateSpace"]="WCS",["units"]=doc.Database.Insunits.ToString(),["entities"]=new JArray(Enumerable.Range(0,3).Select(i=>new JObject
            { ["type"]="line",["layer"]=layer,["points"]=new JArray(new JObject { ["x"]=0,["y"]=i*40,["z"]=0 },new JObject { ["x"]=200,["y"]=i*40,["z"]=0 }) })) };
            var handles=(JArray)Call("cad.create_entities",create)["handles"]!; var h0=(string)handles[0]!; var h1=(string)handles[1]!; var h2=(string)handles[2]!;
            JObject Props(string h)=>(JObject)Call("cad.line_properties",new JObject { ["handles"]=new JArray(h) })["entities"]![0]!;
            var initial=Props(h2);
            Call("cad.set_line_properties",new JObject { ["handles"]=new JArray(h0),["lineweight"]=25,["linetype"]="DASHED",["linetypeScale"]=2,["colorIndex"]=1 });
            var explicitProps=Props(h0);
            Check(explicitProps["lineweightMm"]!.Value<double>()==.25 && (string?)explicitProps["linetype"]=="DASHED" && explicitProps["linetypeScale"]!.Value<double>()==2,"Explicit thickness, pattern and object scale applied");
            Check(Call("cad.entity_geometry",new JObject { ["handle"]=h0 })["length"]!.Value<double>()==200,"Native line geometry and handle preserved");
            Check(JToken.DeepEquals(initial,Props(h2)),"Unselected entity unchanged");
            Call("cad.set_line_properties",new JObject { ["handles"]=new JArray(h1),["lineweight"]=-1,["linetype"]="ByLayer" });
            Call("cad.set_layer_line_properties",new JObject { ["layers"]=new JArray(layer),["lineweight"]=70,["linetype"]="CENTER" });
            var inherited=Props(h1);
            Check(inherited["effectiveLineweightMm"]!.Value<double>()==.7 && (string?)inherited["effectiveLinetype"]=="CENTER","ByLayer inherits native layer thickness and type");
            Check(Props(h0)["lineweightMm"]!.Value<double>()==.25 && (string?)Props(h0)["linetype"]=="DASHED","Layer update preserves entity overrides");
            Call("cad.set_line_properties",new JObject { ["handles"]=new JArray(h2),["lineweight"]=-2,["linetype"]="ByBlock" });
            Check(Props(h2)["effectiveLineweightMm"]!.Type==JTokenType.Null && Props(h2)["effectiveLinetype"]!.Type==JTokenType.Null,"ByBlock reported without inventing an insertion context");
            Call("cad.update_entities",new JObject { ["entities"]=new JArray(new JObject { ["handle"]=h2,["lineweight"]=35,["linetype"]="Continuous",["linetypeScale"]=.5 }) });
            Check(Props(h2)["lineweightMm"]!.Value<double>()==.35 && Props(h2)["linetypeScale"]!.Value<double>()==.5,"Legacy update_entities accepts new properties");
            var settingRequest=Request("cad.set_line_settings",new JObject { ["globalScale"]=2.5,["newEntityScale"]=3,["displayLineweight"]=true });
            var settingResult=Send(settingRequest); Check(settingResult.Ok && ReferenceEquals(settingResult,Send(settingRequest)),"Global settings retry deduplicated");
            var updatedSettings=Call("cad.line_settings",new JObject());
            Check(updatedSettings["globalScale"]!.Value<double>()==2.5 && updatedSettings["newEntityScale"]!.Value<double>()==3 && updatedSettings["displayLineweight"]!.Value<bool>(),"LTSCALE, CELTSCALE and lineweight display changed");
            Check(Props(h0)["combinedLinetypeScale"]!.Value<double>()==5,"Global and object scales combined correctly");
            var before=Props(h0);
            Reject("cad.set_line_properties",new JObject { ["handles"]=new JArray(h0),["lineweight"]=27 },"INVALID_LINEWEIGHT");
            Reject("cad.set_line_properties",new JObject { ["handles"]=new JArray(h0),["linetypeScale"]=0 },"INVALID_ARGUMENT");
            Reject("cad.set_line_properties",new JObject { ["handles"]=new JArray(h0),["linetype"]="__MCP_NOT_LOADED__" },"LINETYPE_NOT_LOADED");
            Reject("cad.set_layer_line_properties",new JObject { ["layers"]=new JArray(layer),["lineweight"]=-1 },"INVALID_LINEWEIGHT");
            Reject("cad.set_layer_line_properties",new JObject { ["layers"]=new JArray(layer),["linetype"]="ByLayer" },"INVALID_LINETYPE");
            Reject("cad.set_line_settings",new JObject { ["globalScale"]=0,["newEntityScale"]=20 },"INVALID_ARGUMENT");
            Check(JToken.DeepEquals(updatedSettings,Call("cad.line_settings",new JObject())),"Invalid setting batch leaves all settings unchanged");
            Reject("cad.update_entities",new JObject { ["entities"]=new JArray(new JObject { ["handle"]=h0,["lineweight"]=100 },new JObject { ["handle"]="NOTHEX",["lineweight"]=50 }) },"INVALID_ARGUMENT");
            Check(JToken.DeepEquals(before,Props(h0)),"Failed entity batch rolled back");
            using(doc.LockDocument()) using(var tr=doc.Database.TransactionManager.StartTransaction())
            { var layers=(LayerTable)tr.GetObject(doc.Database.LayerTableId,OpenMode.ForRead); ((LayerTableRecord)tr.GetObject(layers[layer],OpenMode.ForWrite)).IsLocked=true; tr.Commit(); }
            Reject("cad.set_line_properties",new JObject { ["handles"]=new JArray(h0),["lineweight"]=50 },"LAYER_LOCKED");
            using(doc.LockDocument()) using(var tr=doc.Database.TransactionManager.StartTransaction())
            { var layers=(LayerTable)tr.GetObject(doc.Database.LayerTableId,OpenMode.ForRead); ((LayerTableRecord)tr.GetObject(layers[layer],OpenMode.ForWrite)).IsLocked=false; tr.Commit(); }
            var token=Guid.NewGuid().ToString("N").Substring(0,8); var good="MCP_GOOD_"+token; var bad="MCP_BAD_"+token;
            var badPath=Path.Combine(directory,"invalid-batch-"+token+".lin");
            File.WriteAllText(badPath,"*"+good+",Valid first definition\nA,10,-5\n*"+bad+",Malformed second definition\nA,NOT_A_NUMBER\n",System.Text.Encoding.ASCII);
            Reject("cad.load_linetypes",new JObject { ["file"]=badPath,["names"]=new JArray(good,bad) },"LINETYPE_LOAD_FAILED");
            var types=(JArray)Call("cad.linetypes",new JObject { ["limit"]=500 })["linetypes"]!;
            Check(!types.Any(t=>(string?)t["name"]==good || (string?)t["name"]==bad),"Malformed LIN leaves neither definition in the drawing");
            Reject("cad.load_linetypes",new JObject { ["file"]="acadiso.lin",["names"]=new JArray("__MCP_MISSING__") },"LINETYPE_NOT_IN_FILE");
            var queried=(JArray)Call("cad.query_entities",new JObject { ["layer"]=layer })["entities"]!;
            Check(queried.Count==3 && queried.All(e=>(string?)e["dxfType"]=="LINE" && e["lineweight"]!=null && e["linetype"]!=null),"Native LINE objects expose properties in standard query");
            Call("cad.set_line_settings",settings);
            Check(JToken.DeepEquals(settings,Call("cad.line_settings",new JObject())),"Test drawing global settings restored");
            report["properties"]=Call("cad.line_properties",new JObject { ["handles"]=handles });
            report["loadedLinetypes"]=loaded.Result; report["testLayer"]=layer; report["document"]=Call("cad.document_info",new JObject());
            using(var view=doc.Editor.GetCurrentView()) { view.Target=Point3d.Origin; view.ViewDirection=Vector3d.ZAxis; view.ViewTwist=0; view.CenterPoint=new Point2d(100,40); view.Width=300; view.Height=180; doc.Editor.SetCurrentView(view); }
            doc.Editor.Regen(); report["passed"]=true;
        }
        catch(System.Exception ex) { report["error"]=ex.ToString(); }
        File.WriteAllText(reportPath,report.ToString());
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCADMCP_LINE_CHECK "+report.ToString(Newtonsoft.Json.Formatting.None)+"\n");
    }
}
