using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CadMcp.Civil3D;

public sealed class CivilModule : ICadModule
{
    public void Register(OperationRegistry registry)
    {
        registry.Register("civil.surfaces", Surfaces);
        registry.Register("civil.alignments", Alignments);
        registry.Register("civil.surface_elevations", Elevations);
        registry.Register("civil.create_cogo_points", CreatePoints, mutates: true);
        CivilExtendedOperations.Register(registry);
    }
    private static JToken Surfaces(BridgeRequest request)
    {
        var doc = DocumentAccess.Active(request);
        var offset = (int?)request.Arguments["offset"] ?? 0;
        var limit = (int?)request.Arguments["limit"] ?? 100;
        Validation.Require(offset >= 0 && offset <= 1000000 && limit > 0 && limit <= 500, "Paginación inválida.");
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var ids = CivilDocument.GetCivilDocument(doc.Database).GetSurfaceIds().Cast<ObjectId>().ToArray();
            var rows = ids.Skip(offset).Take(limit).Select(id =>
            {
                var surface = (CivilSurface)tr.GetObject(id, OpenMode.ForRead);
                return new { handle = id.Handle.ToString(), name = surface.Name, type = surface.GetType().Name };
            }).ToArray();
            return JObject.FromObject(new { surfaces = rows, nextOffset = offset + rows.Length < ids.Length ? (int?)(offset + rows.Length) : null });
        }
    }
    private static JToken Alignments(BridgeRequest request)
    {
        var doc = DocumentAccess.Active(request);
        var offset = (int?)request.Arguments["offset"] ?? 0;
        var limit = (int?)request.Arguments["limit"] ?? 100;
        Validation.Require(offset >= 0 && offset <= 1000000 && limit > 0 && limit <= 100, "Paginación inválida; limit máximo 100.");
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var ids = CivilDocument.GetCivilDocument(doc.Database).GetAlignmentIds().Cast<ObjectId>().ToArray();
            var results = new JArray();
            foreach (var id in ids.Skip(offset).Take(limit))
            {
                var alignment = (Alignment)tr.GetObject(id, OpenMode.ForRead);
                var profileIds = alignment.GetProfileIds().Cast<ObjectId>().ToArray();
                var profiles = profileIds.Take(100).Select(pid =>
                {
                    var profile = (Profile)tr.GetObject(pid, OpenMode.ForRead);
                    return new { handle = pid.Handle.ToString(), name = profile.Name };
                }).ToArray();
                results.Add(JObject.FromObject(new { handle = id.Handle.ToString(), name = alignment.Name,
                    startingStation = alignment.StartingStation, endingStation = alignment.EndingStation,
                    profiles, profileCount = profileIds.Length, profilesTruncated = profileIds.Length > 100 }));
            }
            return new JObject { ["alignments"] = results, ["nextOffset"] = offset + results.Count < ids.Length ? new JValue(offset + results.Count) : JValue.CreateNull() };
        }
    }
    private static JToken Elevations(BridgeRequest request)
    {
        var handle = (string?)request.Arguments["surfaceHandle"] ?? "";
        var points = request.Arguments["points"]?.ToObject<List<Vec3>>() ?? throw new CadFault("INVALID_ARGUMENT", "Falta points.");
        Validation.Require(points.Count > 0 && points.Count <= 10000, "Se admiten entre 1 y 10000 puntos.");
        foreach (var point in points) Validation.Point(point);
        var doc = DocumentAccess.Active(request);
        DocumentAccess.Units(doc.Database, (string?)request.Arguments["units"] ?? "", (string?)request.Arguments["coordinateSpace"] ?? "");
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var surface = tr.GetObject(DocumentAccess.ObjectId(doc.Database, handle), OpenMode.ForRead) as CivilSurface
                ?? throw new CadFault("INVALID_OBJECT", "El handle no corresponde a una superficie.");
            var results = new JArray();
            for (var i = 0; i < points.Count; i++)
            {
                try { results.Add(new JObject { ["index"] = i, ["elevation"] = surface.FindElevationAtXY(points[i].X, points[i].Y) }); }
                catch (System.Exception ex) when (ex.GetType().Name == "PointNotOnEntityException")
                { results.Add(new JObject { ["index"] = i, ["elevation"] = JValue.CreateNull(), ["error"] = "OUTSIDE_SURFACE" }); }
            }
            return new JObject { ["results"] = results, ["units"] = doc.Database.Insunits.ToString(), ["coordinateSpace"] = "WCS" };
        }
    }
    private static JToken CreatePoints(BridgeRequest request)
    {
        var args = Validation.Arguments<CogoArgs>(request.Arguments);
        Validation.Require(args.Points != null && args.Points.Count > 0 && args.Points.Count <= 10000, "Se admiten entre 1 y 10000 puntos.");
        Validation.Require(args.Description != null && args.Description.Length <= 1024, "Descripción inválida.");
        foreach (var point in args.Points!) Validation.Point(point);
        var doc = DocumentAccess.Active(request); DocumentAccess.Units(doc.Database, args.Units, args.CoordinateSpace);
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        using (var points = new Point3dCollection(args.Points.Select(CadOperations.ToPoint).ToArray()))
        {
            var civil = CivilDocument.GetCivilDocument(doc.Database);
            var ids = civil.CogoPoints.Add(points, args.Description, true);
            var result = ids.Cast<ObjectId>().Select(id =>
            {
                var point = (CogoPoint)tr.GetObject(id, OpenMode.ForRead);
                return new { handle = id.Handle.ToString(), number = point.PointNumber };
            }).ToArray();
            tr.Commit(); return JObject.FromObject(new { created = result.Length, points = result, coordinateSpace = "WCS", units = args.Units });
        }
    }
}
