using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.AutoCAD.Smoke.TextHatchCheckCommand))]
namespace CadMcp.AutoCAD.Smoke;

public sealed class TextHatchCheckCommand
{
    [CommandMethod("CADMCPTEXTHATCHCHECK", CommandFlags.Session)]
    [CommandMethod("CADMCPTEXTHATCHCHECK2", CommandFlags.Session)]
    public static void Execute()
    {
        var report = new JObject { ["passed"] = false, ["recordedUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["transport"] = "native-in-process" };
        using (var sha = System.Security.Cryptography.SHA256.Create())
            report["assemblySha256"] = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(typeof(TextHatchCheckCommand).Assembly.Location))).Replace("-", "");
        var checks = new JArray(); report["checks"] = checks;
        var reportPath = Environment.GetEnvironmentVariable("CADMCP_TEXT_HATCH_REPORT") ?? Path.Combine(Path.GetDirectoryName(typeof(TextHatchCheckCommand).Assembly.Location)!,"text-hatch-native.json");
        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No hay dibujo.");
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId,OpenMode.ForRead);
                if (((BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],OpenMode.ForRead)).Cast<ObjectId>().Any())
                    throw new InvalidOperationException("La prueba solo se permite en un dibujo nuevo con ModelSpace vacío.");
            }
            var registry = new OperationRegistry(); new CadOperations(registry).Register();
            var engine = new RequestEngine(isMutation: registry.IsMutation);
            BridgeRequest Request(string method,JObject a) => new BridgeRequest { Method = method, DocumentId = DocumentAccess.Id(doc), Arguments = a };
            BridgeResponse Send(BridgeRequest req) { var task = engine.Submit(req); if (!task.IsCompleted) engine.DrainOne(registry.Execute); return task.GetAwaiter().GetResult(); }
            JToken Call(string method,JObject a)
            {
                var result = Send(Request(method,a));
                if (!result.Ok) throw new InvalidOperationException(method+": "+result.Error!.Code+" "+result.Error.Message);
                return result.Result!;
            }
            void Check(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
            void Reject(string method,JObject a,string code)
            {
                var result = Send(Request(method,a)); Check(!result.Ok && result.Error!.Code==code,method+" rejects "+code);
            }
            JObject Args() => new JObject { ["coordinateSpace"] = "WCS", ["units"] = doc.Database.Insunits.ToString() };
            JObject With(JObject extra) { var a = Args(); foreach(var p in extra.Properties().ToArray()) a[p.Name] = p.Value; return a; }
            var suffix = Guid.NewGuid().ToString("N").Substring(0,8);
            var t1 = Call("cad.create_text",With(JObject.Parse("{'position':{'x':0,'y':20,'z':0},'text':'Existing TEXT','height':1,'layer':'0'}")))["handle"]!.Value<string>()!;
            var t2 = Call("cad.create_text",With(JObject.Parse("{'position':{'x':0,'y':23,'z':0},'text':'Existing MTEXT','height':1,'layer':'0','type':'mtext'}")))["handle"]!.Value<string>()!;
            var untouched = Call("cad.create_text",With(JObject.Parse("{'position':{'x':0,'y':26,'z':0},'text':'Unchanged','height':1,'layer':'0'}")));
            var styleArgs = With(new JObject { ["name"] = "MCP_ARIAL_"+suffix, ["font"] = "Arial", ["widthFactor"] = .9, ["obliqueAngle"] = .1 });
            var styleRequest = Request("cad.upsert_text_style",styleArgs); var styleResult = Send(styleRequest);
            Check(styleResult.Ok,"Arial TTF style created"); Check(ReferenceEquals(styleResult,Send(styleRequest)),"Style retry deduplicated");
            var style = styleResult.Result!["handle"]!.Value<string>()!;
            Reject("cad.upsert_text_style",styleArgs,"STYLE_EXISTS");
            var sharedUpdate = (JObject)styleArgs.DeepClone(); sharedUpdate["updateExisting"] = true; sharedUpdate["widthFactor"] = .8;
            Check(Call("cad.upsert_text_style",sharedUpdate)["widthFactor"]!.Value<double>()==.8,"Explicit shared style update accepted");
            var badFont = (JObject)styleArgs.DeepClone(); badFont["name"] = "MCP_BAD_"+suffix; badFont["font"] = "__MCP_FONT_NOT_INSTALLED__";
            Reject("cad.upsert_text_style",badFont,"FONT_NOT_FOUND");
            var assign = Call("cad.set_text_style",new JObject { ["handles"] = new JArray(t1,t2), ["styleHandle"] = style });
            Check(((JArray)assign["texts"]!).All(t=>(string?)t["styleHandle"]==style),"Arial assigned to existing TEXT and MTEXT");
            Check(!(bool)assign["styleDefinitionChanged"]!,"Assignment does not change shared style");
            var shx = Call("cad.upsert_text_style",With(new JObject { ["name"] = "MCP_SHX_"+suffix, ["font"] = "txt.shx" }));
            Check(((string)shx["fontFile"]!).EndsWith("txt.shx",StringComparison.OrdinalIgnoreCase),"SHX style created from available txt.shx");
            var edit = With(new JObject { ["handles"] = new JArray(t1,t2), ["text"] = "Updated native text", ["height"] = 1.5, ["rotation"] = .25, ["alignment"] = "middleCenter" });
            var changed = Call("cad.update_text",edit);
            Check(((JArray)changed["texts"]!).All(t=>(string?)t["text"]=="Updated native text" && Math.Abs(t["height"]!.Value<double>()-1.5)<1e-8),"TEXT/MTEXT edited in place");
            Check(((JArray)changed["texts"]!).Select(t=>(string)t["handle"]!).SequenceEqual(new[]{t1,t2}),"Text handles preserved");
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var other = CadExecution.Entity<DBText>(doc,tr,(string)untouched["handle"]!);
                Check(other.TextString=="Unchanged" && other.TextStyleId.Handle.ToString()==(string)untouched["styleHandle"]!,"Unselected text and style unchanged");
                Check(CadExecution.Entity<Entity>(doc,tr,t1).GetRXClass().DxfName=="TEXT" && CadExecution.Entity<Entity>(doc,tr,t2).GetRXClass().DxfName=="MTEXT","Native TEXT/MTEXT types confirmed");
            }
            var explicitStyle = Call("cad.create_text",With(new JObject { ["position"] = JObject.Parse("{'x':0,'y':29,'z':0}"), ["text"] = "SHX native", ["height"] = 1, ["layer"] = "0", ["styleHandle"] = shx["handle"] }));
            Check((string?)explicitStyle["styleHandle"]==(string?)shx["handle"],"cad_create_text selects existing style");
            string Rectangle(double x,double y,double size,double z=0)
            {
                var points = new JArray(new JObject{{"x",x},{"y",y},{"z",z}},new JObject{{"x",x+size},{"y",y},{"z",z}},new JObject{{"x",x+size},{"y",y+size},{"z",z}},new JObject{{"x",x},{"y",y+size},{"z",z}});
                return Call("cad.create_entities",With(new JObject { ["entities"] = new JArray(new JObject { ["type"] = "polyline", ["closed"] = true, ["layer"] = "0", ["points"] = points }) }))["handles"]![0]!.Value<string>()!;
            }
            var outer = Rectangle(0,0,10); var island = Rectangle(3,3,2); var solidBoundary = Rectangle(15,0,5);
            JObject HatchArgs(string pattern,string boundary) => With(new JObject { ["outerHandles"] = new JArray(boundary), ["pattern"] = pattern, ["layer"] = "0", ["scale"] = 1, ["angle"] = .15, ["associative"] = true, ["colorIndex"] = 3 });
            var hatchRequest = Request("cad.create_hatch",HatchArgs("ANSI31",outer)); var hatchResult = Send(hatchRequest);
            Check(hatchResult.Ok,"Native ANSI31 created"); Check(ReferenceEquals(hatchResult,Send(hatchRequest)),"HATCH retry deduplicated");
            var ansi = hatchResult.Result!;
            var solid = Call("cad.create_hatch",HatchArgs("SOLID",solidBoundary)); Check((string?)solid["dxfType"]=="HATCH","Native SOLID created");
            var withIsland = HatchArgs("ANSI31",outer); withIsland["islandHandles"] = new JArray(island);
            var cutout = Call("cad.create_hatch",withIsland);
            Check(cutout["loopCount"]!.Value<int>()==2,"Native HATCH contains exterior and island loops");
            var update = With(new JObject { ["handle"] = ansi["handle"], ["scale"] = 2, ["angle"] = .5, ["colorIndex"] = 5, ["associative"] = false });
            var edited = Call("cad.update_hatch",update);
            Check(edited["scale"]!.Value<double>()==2 && Math.Abs(edited["angle"]!.Value<double>()-.5)<1e-8 && !(bool)edited["associative"]!,"HATCH scale/angle/color/associativity updated");
            Check((string?)edited["handle"]==(string?)ansi["handle"],"HATCH identity preserved");
            var enable = With(new JObject { ["handle"] = ansi["handle"], ["associative"] = true });
            Check((bool)Call("cad.update_hatch",enable)["associative"]!,"Nonassociative HATCH reattached to saved native contours");
            var solidUpdate = With(new JObject { ["handle"] = solid["handle"], ["pattern"] = "ANSI31", ["scale"] = 1.25 });
            Check((string?)Call("cad.update_hatch",solidUpdate)["pattern"]=="ANSI31","SOLID pattern changed in place");
            var before = ((JArray)Call("cad.query_entities",new JObject())["entities"]!).Count;
            var invalidPattern = HatchArgs("__MCP_MISSING_PATTERN__",outer); Reject("cad.create_hatch",invalidPattern,"INVALID_HATCH_PATTERN");
            var nonplanar = HatchArgs("ANSI31",outer); nonplanar["islandHandles"] = new JArray(Rectangle(4,4,1,2));
            var failedPlane = Send(Request("cad.create_hatch",nonplanar)); Check(!failedPlane.Ok,"Noncoplanar boundaries rejected");
            var outsideIsland = HatchArgs("SOLID",outer); outsideIsland["islandHandles"] = new JArray(solidBoundary);
            Check(!Send(Request("cad.create_hatch",outsideIsland)).Ok,"Island outside exterior rejected");
            var rollback = With(new JObject { ["handles"] = new JArray(t1,outer), ["text"] = "Must not persist" });
            Check(!Send(Request("cad.update_text",rollback)).Ok,"Mixed invalid text batch rejected atomically");
            Check((string?)Call("cad.entity_geometry",new JObject { ["handle"] = t1 })["text"]=="Updated native text","Failed text update rolled back");
            // One extra rectangle was intentionally created for the noncoplanar test; failed hatches added none.
            Check(((JArray)Call("cad.query_entities",new JObject())["entities"]!).Count==before+1,"Failed hatch operations leave no partial entities");
            report["styles"] = new JArray(styleResult.Result,shx); report["hatches"] = new JArray(ansi,solid,cutout);
            report["document"] = Call("cad.document_info",new JObject());
            var dwg = Path.ChangeExtension(reportPath,"dwg");
            using (doc.LockDocument()) doc.Database.SaveAs(dwg,DwgVersion.Current);
            report["drawing"] = dwg; report["passed"] = true; report["nativeAreaPending"] = true;
        }
        catch (System.Exception ex) { report["error"] = ex.ToString(); }
        File.WriteAllText(reportPath,report.ToString());
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCADMCP_TEXT_HATCH_CHECK " + report.ToString(Newtonsoft.Json.Formatting.None)+"\n");
    }

    [CommandMethod("CADMCPTEXTHATCHVERIFY", CommandFlags.Session)]
    public static void VerifyAfterCommand()
    {
        var reportPath = Environment.GetEnvironmentVariable("CADMCP_TEXT_HATCH_REPORT") ?? Path.Combine(Path.GetDirectoryName(typeof(TextHatchCheckCommand).Assembly.Location)!,"text-hatch-native.json");
        var report = JObject.Parse(File.ReadAllText(reportPath));
        try
        {
            if (!(bool)report["passed"]!) throw new InvalidOperationException("La primera fase de la prueba no está aprobada.");
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No hay dibujo.");
            if ((string?)report["document"]?["documentId"] != DocumentAccess.Id(doc)) throw new InvalidOperationException("El dibujo de ensayo ya no está activo.");
            doc.Editor.Regen();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var cutout = CadExecution.Entity<Hatch>(doc,tr,(string)report["hatches"]![2]!["handle"]!);
                if (cutout.NumberOfLoops != 2 || cutout.HatchStyle != HatchStyle.Normal)
                    throw new InvalidOperationException("Bucles o estilo de isla incorrectos.");
                using var outerIds = cutout.GetAssociatedObjectIdsAt(0);
                using var islandIds = cutout.GetAssociatedObjectIdsAt(1);
                var outer = (Polyline)tr.GetObject(outerIds[0],OpenMode.ForRead);
                var island = (Polyline)tr.GetObject(islandIds[0],OpenMode.ForRead);
                var boundaryArea = outer.Area-island.Area;
                if (Math.Abs(boundaryArea-96)>1e-6) throw new InvalidOperationException("Área de contornos incorrecta: "+boundaryArea);
                report["exactBoundaryNetArea"] = boundaryArea;
                try
                {
                    var area = cutout.Area;
                    if (Math.Abs(area-96)>1e-6) throw new InvalidOperationException("Área nativa de isla incorrecta: "+area);
                    report["nativeIslandArea"] = area;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    report["nativeAreaUnavailable"] = ex.ErrorStatus.ToString();
                    report["warnings"] = new JArray("Hatch.Area is unavailable; native Normal hatch loops and exact boundary areas 100/4 were independently verified.");
                }
                ((JArray)report["checks"]!).Add("Native HATCH island loops and exact boundary areas verified (100 - 4 = 96)");
                report["nativeAreaPending"] = false;
            }
        }
        catch (System.Exception ex) { report["passed"] = false; report["error"] = ex.ToString(); }
        File.WriteAllText(reportPath,report.ToString());
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCADMCP_TEXT_HATCH_VERIFY " + report.ToString(Newtonsoft.Json.Formatting.None)+"\n");
    }
}
