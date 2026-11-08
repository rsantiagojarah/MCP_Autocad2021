using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using CadMcp.AutoCAD;
using CadMcp.Civil3D;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.Civil3D.Smoke.SectionCheckCommand))]
namespace CadMcp.Civil3D.Smoke;

// Opt-in native fixture. Never execute in a project drawing.
public sealed class SectionCheckCommand
{
    [CommandMethod("CADMCPSECTIONCHECK", CommandFlags.Session)]
    public static void Execute()
    {
        var report = new JObject { ["passed"] = false, ["recordedUtc"] = DateTimeOffset.UtcNow, ["nativeExecution"] = true };
        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No hay dibujo activo.");
            var registry = new OperationRegistry(); new CadOperations(registry).Register(); new CivilModule().Register(registry);
            JToken Call(string method, JObject args) => registry.Execute(new BridgeRequest { Method = method, DocumentId = DocumentAccess.Id(doc), Arguments = args });
            JObject Coords() => new JObject { ["coordinateSpace"] = "WCS", ["units"] = "Meters" };
            var before = Call("cad.query_entities", new JObject());
            var civil = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(doc.Database);
            if (((JArray)before["entities"]!).Count != 0 || before["nextOffset"]!.Type != JTokenType.Null || civil.GetAlignmentIds().Count != 0 || civil.GetSurfaceIds().Count != 0 || civil.CogoPoints.Count != 0)
                throw new InvalidOperationException("Usa un dibujo nuevo vacío. No se modificó el dibujo del proyecto.");
            using (doc.LockDocument()) using (var tr = doc.Database.TransactionManager.StartTransaction()) { doc.Database.Insunits = UnitsValue.Meters; tr.Commit(); }
            var name = "SECTION_CHECK_" + Guid.NewGuid().ToString("N");
            var terrainArgs = Coords(); terrainArgs["name"] = name + "_TIN";
            terrainArgs["points"] = JArray.Parse("[{'x':0,'y':0,'z':0},{'x':100,'y':0,'z':10},{'x':0,'y':100,'z':20},{'x':100,'y':100,'z':30}]");
            var terrain = Call("civil.create_tin_surface", terrainArgs);
            var pathArgs = Coords(); pathArgs["entities"] = JArray.Parse("[{'type':'polyline','layer':'0','points':[{'x':10,'y':50,'z':0},{'x':90,'y':50,'z':0}]}]");
            var path = Call("cad.create_entities", pathArgs)["handles"]![0];
            var axisArgs = Coords(); axisArgs["name"] = name + "_AXIS"; axisArgs["sourceHandle"] = path;
            var axis = Call("civil.create_alignment", axisArgs);
            var boxArgs = Coords(); boxArgs["type"] = "box"; boxArgs["position"] = JObject.Parse("{'x':30,'y':45,'z':9}");
            boxArgs["length"] = 20; boxArgs["width"] = 10; boxArgs["height"] = 5; boxArgs["layer"] = "NATIVE_SECTION_BOX";
            var box = Call("cad.create_solid", boxArgs);
            var args = Coords(); args["alignmentHandle"] = axis["handle"]; args["name"] = name; args["interval"] = 9;
            args["startStation"] = 21; args["endStation"] = 39; args["leftWidth"] = 8; args["rightWidth"] = 8;
            args["elevationMin"] = 8; args["elevationMax"] = 25; args["position"] = JObject.Parse("{'x':150,'y':150,'z':0}");
            args["surfaceHandles"] = new JArray(terrain["handle"]!); args["solidHandles"] = new JArray(box["handle"]!);
            var created = Call("civil.generate_cross_sections", args);
            var views = (JArray)created["views"]!;
            if (views.Count != 3 || created["nativeSectionViewGroup"]!.Value<bool>() != true) throw new InvalidOperationException("Faltan vistas o grupo nativo.");
            foreach (var view in views)
            {
                var result = view["solidSections"]!["results"]![0]!;
                if ((string?)result["status"] != "cut" || Math.Abs(result["area"]!.Value<double>()-50)>1e-6 || result["curveCount"]!.Value<int>() != 4)
                    throw new InvalidOperationException("Corte de caja incorrecto: debe ser rectángulo de área 50 y cuatro curvas.");
            }
            var sources = Call("civil.section_sources", new JObject { ["sampleLineGroupHandle"] = created["sampleLineGroupHandle"] });
            if (!((JArray)sources["sources"]!).Any(s => (string?)s["sourceHandle"] == (string?)terrain["handle"] && s["sampled"]!.Value<bool>() && (string?)s["updateMode"] == "Dynamic"))
                throw new InvalidOperationException("No se configuró el terreno como fuente dinámica.");
            var lineArgs = Coords(); lineArgs["entities"] = JArray.Parse("[{'type':'line','layer':'0','points':[{'x':140,'y':140,'z':0},{'x':141,'y':141,'z':0}]}]");
            var userLine = Call("cad.create_entities", lineArgs)["handles"]![0]!.Value<string>()!;
            var oldHandles = views.SelectMany(v => v["solidSections"]!["generatedHandles"]!.Values<string>()).ToArray();
            var update = Coords(); update["sectionViewHandles"] = new JArray(views.Select(v => v["handle"])); update["elevationMin"] = 10; update["elevationMax"] = 13;
            var updated = Call("civil.update_section_views", update);
            foreach (var view in (JArray)updated["views"]!)
                if (Math.Abs(view["solidSections"]!["results"]![0]!["area"]!.Value<double>()-30)>1e-6) throw new InvalidOperationException("Recorte a rango de elevaciones incorrecto.");
            Call("cad.entity_geometry", new JObject { ["handle"] = userLine });
            foreach (var handle in oldHandles)
            {
                try { Call("cad.entity_geometry", new JObject { ["handle"] = handle }); throw new InvalidOperationException("El refresco dejó contornos duplicados."); }
                catch (CadFault ex) when (ex.Code == "OBJECT_NOT_FOUND") { }
            }
            // An invalid style fails after native objects were inserted; their transaction must roll back.
            var entityCountBefore = ((JArray)Call("cad.query_entities", new JObject { ["limit"] = 500 })["entities"]!).Count;
            var invalid = (JObject)args.DeepClone(); invalid["name"] = name + "_ABORT"; invalid["bandSetStyleHandle"] = "FFFFFFFFFFFFFF";
            try { Call("civil.generate_cross_sections", invalid); throw new InvalidOperationException("Se aceptó un estilo inexistente."); }
            catch (CadFault ex) when (ex.Code == "OBJECT_NOT_FOUND" || ex.Code == "INVALID_ARGUMENT") { }
            var entityCountAfter = ((JArray)Call("cad.query_entities", new JObject { ["limit"] = 500 })["entities"]!).Count;
            if (entityCountAfter != entityCountBefore) throw new InvalidOperationException("La operación fallida dejó entidades parciales.");
            var sourceInfo = Call("cad.entity_geometry", new JObject { ["handle"] = box["handle"] });
            if (Math.Abs(sourceInfo["volume"]!.Value<double>()-1000)>1e-6) throw new InvalidOperationException("Se modificó el sólido fuente.");
            report["passed"] = true; report["created"] = created; report["updated"] = updated;
            report["checks"] = new JArray("native_section_view_group", "dynamic_surface_source", "three_exact_box_cuts", "elevation_clipping", "refresh_without_duplicates", "unrelated_geometry_preserved", "atomic_rollback", "source_solid_preserved");
        }
        catch (System.Exception ex) { report["error"] = ex.ToString(); }
        var directory = Path.GetDirectoryName(typeof(SectionCheckCommand).Assembly.Location)!;
        var output = Path.Combine(directory, "section-native-check.json");
        File.WriteAllText(output, report.ToString());
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCADMCPSECTIONCHECK: " + output + " | passed=" + report["passed"] + "\n");
    }
}
