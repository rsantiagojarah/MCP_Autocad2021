using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CadMcp.AutoCAD;
using CadMcp.Civil3D;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadMcp.Civil3D.Smoke.CivilCheckCommand))]
namespace CadMcp.Civil3D.Smoke;

// Test-only DLL. This is NOT registered as a production MCP tool.
public sealed class CivilCheckCommand
{
    [CommandMethod("CADMCPCIVILCHECK", CommandFlags.Session)]
    public static void Execute()
    {
        var report = new JObject { ["passed"] = false, ["transport"] = "native-in-process", ["recordedUtc"] = DateTimeOffset.UtcNow };
        var watch = Stopwatch.StartNew();
        try
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No hay documento activo.");
            var registry = new OperationRegistry(); new CadOperations(registry).Register(); new CivilModule().Register(registry);
            JToken Call(string method, JObject args) => registry.Execute(new BridgeRequest { Method = method, DocumentId = DocumentAccess.Id(doc), Arguments = args });
            var before = Call("cad.query_entities", new JObject());
            var civil = CivilDocument.GetCivilDocument(doc.Database);
            if (((JArray)before["entities"]!).Count != 0 || before["nextOffset"]!.Type != JTokenType.Null || civil.CogoPoints.Count != 0 || civil.GetSurfaceIds().Count != 0 || civil.GetAlignmentIds().Count != 0)
                throw new InvalidOperationException("La prueba exige un dibujo nuevo sin entidades, puntos COGO, superficies ni alineamientos. No se modificó el dibujo.");
            var name = "CADMCP_CHECK_" + Guid.NewGuid().ToString("N");
            ObjectId surfaceId;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                surfaceId = TinSurface.Create(doc.Database, name);
                var surface = (TinSurface)tr.GetObject(surfaceId, OpenMode.ForWrite);
                using (var vertices = new Point3dCollection(new[] { new Point3d(0, 0, 0), new Point3d(100, 0, 10), new Point3d(0, 100, 20) })) surface.AddVertices(vertices);
                surface.Rebuild(); tr.Commit();
            }
            var listed = (JArray)Call("civil.surfaces", new JObject())["surfaces"]!;
            if (!listed.Any(s => (string?)s["handle"] == surfaceId.Handle.ToString())) throw new InvalidOperationException("La superficie creada no aparece en el listado.");
            var elevations = Call("civil.surface_elevations", new JObject
            {
                ["surfaceHandle"] = surfaceId.Handle.ToString(), ["coordinateSpace"] = "WCS", ["units"] = doc.Database.Insunits.ToString(),
                ["points"] = new JArray(new JObject { ["x"] = 10, ["y"] = 10, ["z"] = 0 }, new JObject { ["x"] = 20, ["y"] = 10, ["z"] = 0 }, new JObject { ["x"] = -10, ["y"] = -10, ["z"] = 0 })
            });
            var rows = (JArray)elevations["results"]!;
            if (Math.Abs(rows[0]["elevation"]!.Value<double>() - 3) > 1e-8 || Math.Abs(rows[1]["elevation"]!.Value<double>() - 4) > 1e-8)
                throw new InvalidOperationException("Las elevaciones no coinciden con el plano z=0.1*x+0.2*y.");
            if ((string?)rows[2]["error"] != "OUTSIDE_SURFACE") throw new InvalidOperationException("No se identificó la muestra fuera de superficie.");
            report["surface"] = new JObject { ["handle"] = surfaceId.Handle.ToString(), ["name"] = name, ["listed"] = true, ["samples"] = elevations, ["expectedElevations"] = new JArray(3.0, 4.0), ["outsideVerified"] = true };
            var cogo = Call("civil.create_cogo_points", new JObject
            {
                ["coordinateSpace"] = "WCS", ["units"] = doc.Database.Insunits.ToString(), ["description"] = "CADMCP_CHECK",
                ["points"] = new JArray(new JObject { ["x"] = 10, ["y"] = 10, ["z"] = 3 }, new JObject { ["x"] = 20, ["y"] = 10, ["z"] = 4 })
            });
            if ((int?)cogo["created"] != 2 || civil.CogoPoints.Count != 2) throw new InvalidOperationException("Cantidad COGO incorrecta.");
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var row in (JArray)cogo["points"]!)
                {
                    var point = (CogoPoint)tr.GetObject(DocumentAccess.ObjectId(doc.Database, row["handle"]!.Value<string>()!), OpenMode.ForRead);
                    var expectedZ = 0.1 * point.Easting + 0.2 * point.Northing;
                    if (Math.Abs(point.Elevation - expectedZ) > 1e-8 || point.RawDescription != "CADMCP_CHECK") throw new InvalidOperationException("Coordenadas o descripción COGO incorrectas.");
                }
            }
            var countBeforeAbort = civil.CogoPoints.Count;
            ObjectId abortedPoint;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                abortedPoint = civil.CogoPoints.Add(new Point3d(25, 25, 7.5), "CADMCP_ABORT", true);
                // Intentionally abort after a real Civil API insertion; no Commit.
            }
            var countAfterAbort = CivilDocument.GetCivilDocument(doc.Database).CogoPoints.Count;
            if (countAfterAbort != countBeforeAbort || (abortedPoint.IsValid && !abortedPoint.IsErased)) throw new InvalidOperationException("La creación COGO no se revirtió al abortar la transacción externa.");
            report["cogo"] = cogo;
            report["cogoRollback"] = new JObject { ["verified"] = true, ["before"] = countBeforeAbort, ["after"] = countAfterAbort };
            report["alignments"] = Call("civil.alignments", new JObject());
            // Additional tools are tested only in the disposable empty drawing
            // required at entry. Never run this command against a project DWG.
            JObject Coordinates() => new JObject { ["coordinateSpace"] = "WCS", ["units"] = doc.Database.Insunits.ToString() };
            var terrainArgs = Coordinates(); terrainArgs["name"] = name + "_TIN";
            terrainArgs["points"] = JArray.Parse("[{'x':0,'y':0,'z':0},{'x':100,'y':0,'z':10},{'x':0,'y':100,'z':20},{'x':100,'y':100,'z':30}]");
            var terrain = Call("civil.create_tin_surface", terrainArgs);
            var terrainInfo = Call("civil.object_info", new JObject { ["handle"] = terrain["handle"] });
            if (terrainInfo["tin"]!["numberOfTriangles"]!.Value<int>() != 2) throw new InvalidOperationException("TIN: triangulación incorrecta.");
            var comparisonArgs = (JObject)terrainArgs.DeepClone(); comparisonArgs["name"] = name + "_COMPARISON";
            foreach (var point in (JArray)comparisonArgs["points"]!) point["z"] = point["z"]!.Value<double>() + 1;
            var comparison = Call("civil.create_tin_surface", comparisonArgs);
            var volume = Call("civil.create_volume_surface", new JObject { ["name"] = name + "_VOLUME", ["baseSurfaceHandle"] = terrain["handle"], ["comparisonSurfaceHandle"] = comparison["handle"] });
            if (Math.Abs(volume["volumes"]!["unadjustedFillVolume"]!.Value<double>() - 10000) > 1e-5) throw new InvalidOperationException("Volumen de comparación incorrecto.");
            var pathArgs = Coordinates(); pathArgs["entities"] = JArray.Parse("[{'type':'polyline','layer':'0','points':[{'x':10,'y':10,'z':0},{'x':80,'y':10,'z':0}]}]");
            var pathHandle = Call("cad.create_entities", pathArgs)["handles"]![0];
            var alignArgs = Coordinates(); alignArgs["name"] = name + "_AXIS"; alignArgs["sourceHandle"] = pathHandle; alignArgs["startingStation"] = 100;
            var axis = Call("civil.create_alignment", alignArgs);
            if (Math.Abs(axis["startingStation"]!.Value<double>() - 100) > 1e-7 || Math.Abs(axis["endingStation"]!.Value<double>() - 170) > 1e-7) throw new InvalidOperationException("Estacionamiento del eje incorrecto.");
            var profileArgs = Coordinates(); profileArgs["name"] = name + "_FG"; profileArgs["alignmentHandle"] = axis["handle"];
            profileArgs["pvis"] = JArray.Parse("[{'station':100,'elevation':3},{'station':170,'elevation':10}]");
            var profile = Call("civil.create_layout_profile", profileArgs);
            var sampleArgs = Coordinates(); sampleArgs["profileHandle"] = profile["handle"]; sampleArgs["stations"] = new JArray(135);
            var profileSample = Call("civil.sample_profile", sampleArgs);
            if (Math.Abs(profileSample["samples"]![0]!["elevation"]!.Value<double>() - 6.5) > 1e-7) throw new InvalidOperationException("Interpolación de rasante incorrecta.");
            report["extendedTerrain"] = terrainInfo; report["volumeComparison"] = volume; report["extendedAlignment"] = axis; report["layoutProfileSample"] = profileSample;
            report["passed"] = true;
        }
        catch (System.Exception ex) { report["error"] = ex.ToString(); }
        report["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds;
        var path = Path.Combine(Path.GetDirectoryName(typeof(CivilCheckCommand).Assembly.Location)!, "civil-check.json");
        File.WriteAllText(path, report.ToString());
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCADMCPCIVILCHECK " + report.ToString(Newtonsoft.Json.Formatting.None) + "\nReporte: " + path + "\n");
    }
}
