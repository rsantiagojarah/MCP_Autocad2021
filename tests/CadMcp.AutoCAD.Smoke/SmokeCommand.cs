using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Runtime;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.AutoCAD.Smoke.SmokeCommand))]
namespace CadMcp.AutoCAD.Smoke;

// Test-only assembly. Run in a new disposable Core Console drawing, never a user DWG.
public sealed class SmokeCommand
{
    [CommandMethod("CADMCPSMOKE", CommandFlags.Session)]
    public static void Execute()
    {
        var report = new JObject { ["passed"] = false };
        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No hay documento.");
            var registry = new OperationRegistry(); new CadOperations(registry).Register();
            JToken Call(string method, JObject arguments) => registry.Execute(new BridgeRequest { Method = method, DocumentId = DocumentAccess.Id(doc), Arguments = arguments });
            var initial = Call("cad.query_entities", new JObject());
            if (((JArray)initial["entities"]!).Count != 0 || initial["nextOffset"]!.Type != JTokenType.Null)
                throw new InvalidOperationException("Usa un dibujo de ensayo nuevo con ModelSpace vacío. No se modificó el dibujo.");
            var layer = "CADMCP_SMOKE_" + Guid.NewGuid().ToString("N");
            Call("cad.upsert_layers", new JObject { ["layers"] = new JArray(new JObject { ["name"] = layer, ["colorIndex"] = 3 }) });
            var payload = JObject.Parse("{'coordinateSpace':'WCS','entities':[{'type':'line','points':[{'x':0,'y':0,'z':0},{'x':3,'y':4,'z':0}]},{'type':'polyline','closed':true,'points':[{'x':0,'y':0,'z':0},{'x':10,'y':0,'z':0},{'x':10,'y':10,'z':0},{'x':0,'y':10,'z':0}]},{'type':'point','points':[{'x':5,'y':5,'z':0}]}]}");
            payload["units"] = doc.Database.Insunits.ToString();
            foreach (var spec in (JArray)payload["entities"]!) spec["layer"] = layer;
            var request = new BridgeRequest { Method = "cad.create_entities", DocumentId = DocumentAccess.Id(doc), Arguments = payload };
            var engine = new RequestEngine(); var first = engine.Submit(request); engine.DrainOne(registry.Execute);
            if (!first.Result.Ok) throw new InvalidOperationException(first.Result.Error!.Message);
            if (engine.Submit(request).Result != first.Result) throw new InvalidOperationException("Deduplicación fallida.");
            var rows = (JArray)Call("cad.query_entities", new JObject { ["layer"] = layer })["entities"]!;
            if (rows.Count != 3) throw new InvalidOperationException("Cantidad incorrecta.");
            if (Math.Abs(rows.Single(r => (string?)r["dxfType"] == "LINE")["length"]!.Value<double>() - 5) > 1e-9) throw new InvalidOperationException("Longitud incorrecta.");
            if (Math.Abs(rows.Single(r => (string?)r["dxfType"] == "LWPOLYLINE")["area"]!.Value<double>() - 100) > 1e-9) throw new InvalidOperationException("Área incorrecta.");
            var handle = rows[0]["handle"]!.Value<string>();
            try
            {
                Call("cad.update_entities", new JObject { ["entities"] = new JArray(new JObject { ["handle"] = handle, ["colorIndex"] = 1 }, new JObject { ["handle"] = "INVALID", ["colorIndex"] = 2 }) });
                throw new InvalidOperationException("Se aceptó un handle inválido.");
            }
            catch (CadFault ex) when (ex.Code == "INVALID_ARGUMENT") { }
            rows = (JArray)Call("cad.query_entities", new JObject { ["layer"] = layer })["entities"]!;
            if (rows.Single(r => (string?)r["handle"] == handle)["colorIndex"]!.Value<int>() != 256) throw new InvalidOperationException("El lote fallido dejó cambios parciales.");
            report["passed"] = true; report["created"] = rows.Count; report["lineLength"] = 5; report["polylineArea"] = 100;
            report["rollback"] = true; report["deduplication"] = true;
            // Exercise actual modeler geometry and independent dimensions/volumes.
            var unit = doc.Database.Insunits.ToString();
            JObject GeometryArgs() => new JObject { ["coordinateSpace"] = "WCS", ["units"] = unit };
            var boxArgs = GeometryArgs(); boxArgs["type"] = "box"; boxArgs["position"] = JObject.Parse("{'x':20,'y':30,'z':40}");
            boxArgs["length"] = 2; boxArgs["width"] = 3; boxArgs["height"] = 4; boxArgs["layer"] = layer;
            var box = Call("cad.create_solid", boxArgs); var boxGeometry = Call("cad.entity_geometry", new JObject { ["handle"] = box["handle"] });
            if (Math.Abs(boxGeometry["volume"]!.Value<double>() - 24) > 1e-7 || Math.Abs(boxGeometry["min"]!["z"]!.Value<double>() - 40) > 1e-7 || Math.Abs(boxGeometry["max"]!["x"]!.Value<double>() - 22) > 1e-7) throw new InvalidOperationException("Caja: posición o volumen incorrectos.");
            var sourceArgs = GeometryArgs();
            sourceArgs["entities"] = JArray.Parse("[{'type':'polyline','layer':'" + layer + "','points':[{'x':0,'y':0,'z':0},{'x':10,'y':0,'z':0},{'x':10,'y':5,'z':0}]}]");
            var source = Call("cad.create_entities", sourceArgs)["handles"]![0]!.Value<string>();
            var wallArgs = GeometryArgs(); wallArgs["sourceHandle"] = source; wallArgs["stemThickness"] = .25; wallArgs["stemHeight"] = 3; wallArgs["heel"] = 2; wallArgs["toe"] = 2; wallArgs["footingThickness"] = .5;
            var wall = Call("cad.create_retaining_wall", wallArgs);
            if (Math.Abs(wall["stemVolume"]!.Value<double>() - 11.25) > 1e-7 || Math.Abs(wall["footingVolume"]!.Value<double>() - 31.875) > 1e-7) throw new InvalidOperationException("Muro: volumen incorrecto en esquina.");
            var wallStem = Call("cad.entity_geometry", new JObject { ["handle"] = wall["stemHandle"] });
            var wallBase = Call("cad.entity_geometry", new JObject { ["handle"] = wall["footingHandle"] });
            if (Math.Abs(wallStem["max"]!["z"]!.Value<double>() - 3) > 1e-7 || Math.Abs(wallBase["min"]!["z"]!.Value<double>() + .5) > 1e-7) throw new InvalidOperationException("Muro: cotas incorrectas.");
            var beforeFailure = ((JArray)Call("cad.query_entities", new JObject())["entities"]!).Count;
            var invalidWall = (JObject)wallArgs.DeepClone(); invalidWall["stemLayer"] = "CADMCP_ROLLBACK"; invalidWall["footingLayer"] = "INVALID|LAYER";
            try { Call("cad.create_retaining_wall", invalidWall); throw new InvalidOperationException("Se aceptó capa inválida."); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            if (((JArray)Call("cad.query_entities", new JObject())["entities"]!).Count != beforeFailure) throw new InvalidOperationException("El muro fallido dejó sólidos parciales.");
            report["solidGeometry"] = boxGeometry; report["retainingWall"] = wall; report["retainingWallRollback"] = true;
            report["document"] = Call("cad.document_info", new JObject());
        }
        catch (System.Exception ex) { report["error"] = ex.ToString(); }
        var path = Environment.GetEnvironmentVariable("CADMCP_SMOKE_REPORT");
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Path.GetDirectoryName(typeof(SmokeCommand).Assembly.Location)!, "autocad-check.json");
        File.WriteAllText(path, report.ToString());
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCADMCP_SMOKE " + report.ToString(Newtonsoft.Json.Formatting.None) + "\n");
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nReporte: " + path + "\n");
    }
}
