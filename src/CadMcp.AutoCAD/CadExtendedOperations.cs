using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;

namespace CadMcp.AutoCAD;

public static class CadExtendedOperations
{
    public static void Register(OperationRegistry r)
    {
        r.Register("cad.entity_geometry", Geometry);
        r.Register("cad.create_polyline3d", Polyline3D, true);
        r.Register("cad.create_circle", CircleEntity, true);
        r.Register("cad.create_text", CadTextOperations.Create, true);
        r.Register("cad.create_solid", Solid, true);
        r.Register("cad.transform_entities", Transform, true);
        r.Register("cad.delete_entities", Delete, true);
        r.Register("cad.offset_curve", Offset, true);
        r.Register("cad.extrude_polyline", Extrude, true);
        r.Register("cad.boolean_solids", Boolean, true);
        r.Register("cad.create_retaining_wall", Wall, true);
        r.Register("cad.set_view", View, true);
        CadTextOperations.Register(r);
        CadHatchOperations.Register(r);
        CadLineGraphicsOperations.Register(r);
    }
    private static JToken Geometry(BridgeRequest r) => CadExecution.Run(r, false, (doc, tr) =>
    {
        var e = CadExecution.Entity<Entity>(doc, tr, ToolArgs.Text(r.Arguments, "handle"));
        var row = CadOperations.Describe(e, tr);
        try { var bounds = e.GeometricExtents; row["min"] = CadOperations.Point(bounds.MinPoint); row["max"] = CadOperations.Point(bounds.MaxPoint); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { row["boundsUnavailable"] = true; }
        var page = ToolArgs.Page(r.Arguments, 1000);
        if (e is Polyline p)
        {
            row["normal"] = CadOperations.Point(new Point3d(p.Normal.X, p.Normal.Y, p.Normal.Z));
            row["elevation"] = p.Elevation;
            row["vertices"] = new JArray(Enumerable.Range(page.offset, Math.Max(0, Math.Min(page.limit, p.NumberOfVertices - page.offset))).Select(i =>
                new JObject { ["index"] = i, ["point"] = CadOperations.Point(p.GetPoint3dAt(i)), ["bulge"] = p.GetBulgeAt(i), ["startWidth"] = p.GetStartWidthAt(i), ["endWidth"] = p.GetEndWidthAt(i) }));
            row["nextOffset"] = page.offset + page.limit < p.NumberOfVertices ? new JValue(page.offset + page.limit) : JValue.CreateNull();
        }
        else if (e is Polyline3d p3)
        {
            var ids = p3.Cast<ObjectId>().ToArray(); row["closed"] = p3.Closed; row["length"] = p3.Length; row["vertexCount"] = ids.Length;
            row["vertices"] = new JArray(ids.Skip(page.offset).Take(page.limit).Select(id => CadOperations.Point(((PolylineVertex3d)tr.GetObject(id, OpenMode.ForRead)).Position)));
            row["nextOffset"] = page.offset + page.limit < ids.Length ? new JValue(page.offset + page.limit) : JValue.CreateNull();
        }
        else if (e is Solid3d s) { var m = s.MassProperties; row["volume"] = m.Volume; row["centroid"] = CadOperations.Point(m.Centroid); }
        return row;
    });
    private static List<Vec3> Points(JObject a, string key = "points", int min = 2)
    {
        var points = ToolArgs.List<Vec3>(a, key, min, 10000);
        foreach (var p in points) Validation.Point(p);
        for (var i = 1; i < points.Count; i++) Validation.Require(ToPoint(points[i]).DistanceTo(ToPoint(points[i - 1])) > 1e-9, "Puntos consecutivos coincidentes.");
        return points;
    }
    private static Point3d ToPoint(Vec3 p) => CadOperations.ToPoint(p);
    private static JToken Polyline3D(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var points = Points(a); var closed = ToolArgs.Flag(a, "closed");
        Validation.Require(!closed || points.Count >= 3, "Una polilínea cerrada necesita tres vértices.");
        Validation.Require(!closed || ToPoint(points[0]).DistanceTo(ToPoint(points[points.Count - 1])) > 1e-9, "No repitas el primer vértice.");
        using var collection = new Point3dCollection(points.Select(ToPoint).ToArray());
        using var poly = new Polyline3d(Poly3dType.SimplePoly, collection, closed);
        CadExecution.Append(doc, tr, poly, CadExecution.Layer(doc, tr, ToolArgs.Text(a, "layer")));
        return CadExecution.Identity(poly);
    }, true);
    private static JToken CircleEntity(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments;
        using var circle = new Circle(ToPoint(ToolArgs.Point(a, "center")), Vector3d.ZAxis, ToolArgs.Number(a, "radius", positive: true));
        CadExecution.Append(doc, tr, circle, CadExecution.Layer(doc, tr, ToolArgs.Text(a, "layer"))); return CadExecution.Identity(circle);
    }, true);
    private static JToken Solid(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var type = ToolArgs.Text(a, "type"); var position = ToPoint(ToolArgs.Point(a, "position"));
        using var solid = new Solid3d();
        var height = ToolArgs.Number(a, "height", positive: true);
        if (type == "box")
        {
            var length = ToolArgs.Number(a, "length", positive: true); var width = ToolArgs.Number(a, "width", positive: true);
            Validation.Require(a["radius"] == null, "box no utiliza radius.");
            solid.CreateBox(length, width, height);
        }
        else if (type == "cylinder")
        {
            Validation.Require(a["length"] == null && a["width"] == null, "cylinder no utiliza length/width.");
            var radius = ToolArgs.Number(a, "radius", positive: true); solid.CreateFrustum(height, radius, radius, radius);
        }
        else throw new CadFault("INVALID_ARGUMENT", "type debe ser box o cylinder.");
        // Native primitives are centered around the origin. Normalize the public
        // placement contract using their actual extents, not a presumed origin.
        var nativeBase = type == "box" ? solid.GeometricExtents.MinPoint : new Point3d(0, 0, solid.GeometricExtents.MinPoint.Z);
        solid.TransformBy(Matrix3d.Displacement(position - nativeBase));
        CadExecution.Append(doc, tr, solid, CadExecution.Layer(doc, tr, ToolArgs.Text(a, "layer")));
        return new JObject { ["handle"] = solid.Handle.ToString(), ["volume"] = solid.MassProperties.Volume };
    }, true);
    private static void Plain(Entity e)
    {
        Validation.Require(e.GetType().Assembly == typeof(Entity).Assembly && !e.GetRXClass().DxfName.StartsWith("AECC", StringComparison.OrdinalIgnoreCase), "Usa las herramientas Civil nativas para editar este objeto.");
    }
    private static JToken Transform(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var handles = ToolArgs.Handles(a); var copy = ToolArgs.Flag(a, "copy");
        var origin = ToPoint(ToolArgs.Point(a, "basePoint", new Vec3())); var move = ToPoint(ToolArgs.Point(a, "displacement", new Vec3())) - Point3d.Origin;
        var matrix = Matrix3d.Displacement(move) * Matrix3d.Rotation(ToolArgs.Number(a, "rotation", 0), Vector3d.ZAxis, origin) * Matrix3d.Scaling(ToolArgs.Number(a, "scale", 1, true), origin);
        var result = new JArray();
        foreach (var h in handles)
        {
            var source = CadExecution.Entity<Entity>(doc, tr, h, !copy); Plain(source);
            if (copy)
            {
                using var clone = source.GetTransformedCopy(matrix);
                CadExecution.Append(doc, tr, clone, CadExecution.Layer(doc, tr, source.Layer)); result.Add(clone.Handle.ToString());
            }
            else { source.TransformBy(matrix); result.Add(h); }
        }
        return new JObject { ["handles"] = result, ["copied"] = copy };
    }, true);
    private static JToken Delete(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var handles = ToolArgs.Handles(r.Arguments);
        foreach (var h in handles) { var e = CadExecution.Entity<Entity>(doc, tr, h, true); Plain(e); e.Erase(); }
        return new JObject { ["deleted"] = handles.Length, ["handles"] = new JArray(handles) };
    });
    private static JToken Offset(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var curve = CadExecution.Entity<Curve>(doc, tr, ToolArgs.Text(a, "handle")); Plain(curve);
        var distance = ToolArgs.Number(a, "distance"); Validation.Require(Math.Abs(distance) > 1e-9, "distance no puede ser cero.");
        var layer = CadExecution.Layer(doc, tr, ToolArgs.Text(a, "layer", curve.Layer)); var result = new JArray();
        var offsets = curve.GetOffsetCurves(distance);
        try { foreach (Entity entity in offsets) result.Add(CadExecution.Append(doc, tr, entity, layer)); }
        finally { foreach (DBObject entity in offsets) entity.Dispose(); offsets.Dispose(); }
        Validation.Require(result.Count > 0, "No se pudo generar el offset.");
        return new JObject { ["handles"] = result };
    }, true);
    internal static Solid3d Extruded(Polyline poly, double height)
    {
        Validation.Require(poly.Closed && poly.NumberOfVertices >= 3, "Se requiere una polilínea cerrada.");
        using var curves = new DBObjectCollection(); curves.Add(poly);
        var regions = Region.CreateFromCurves(curves);
        try
        {
            Validation.Require(regions.Count == 1 && regions[0] is Region region && region.Area > 1e-9, "El contorno debe definir una única región válida sin cruces.");
            var solid = new Solid3d();
            try
            {
                var profile = (Region)regions[0];
                // Region normals follow contour winding. The public height is
                // measured along the source polyline normal, in either winding.
                var signedHeight = profile.Normal.DotProduct(poly.Normal) < 0 ? -height : height;
                solid.Extrude(profile, signedHeight, 0);
                Validation.Require(solid.MassProperties.Volume > 1e-12, "Sólido sin volumen."); return solid;
            }
            catch { solid.Dispose(); throw; }
        }
        finally { foreach (DBObject obj in regions) obj.Dispose(); regions.Dispose(); }
    }
    private static JToken Extrude(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var source = CadExecution.Entity<Polyline>(doc, tr, ToolArgs.Text(a, "handle")); var height = ToolArgs.Number(a, "height");
        Validation.Require(Math.Abs(height) > 1e-9, "height no puede ser cero.");
        using var solid = Extruded(source, height);
        CadExecution.Append(doc, tr, solid, CadExecution.Layer(doc, tr, ToolArgs.Text(a, "layer", source.Layer)));
        return new JObject { ["handle"] = solid.Handle.ToString(), ["volume"] = solid.MassProperties.Volume, ["sourcePreserved"] = true };
    }, true);
    private static JToken Boolean(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var targetHandle = ToolArgs.Text(a, "targetHandle"); var handles = ToolArgs.Handles(a, "toolHandles", 100);
        Validation.Require(!handles.Contains(targetHandle, StringComparer.OrdinalIgnoreCase), "El sólido objetivo no puede ser una herramienta.");
        var operation = ToolArgs.Text(a, "operation");
        var op = operation == "union" ? BooleanOperationType.BoolUnite : operation == "subtract" ? BooleanOperationType.BoolSubtract : operation == "intersect" ? BooleanOperationType.BoolIntersect : throw new CadFault("INVALID_ARGUMENT", "Operación booleana inválida.");
        var target = CadExecution.Entity<Solid3d>(doc, tr, targetHandle, true);
        foreach (var h in handles)
        {
            var tool = CadExecution.Entity<Solid3d>(doc, tr, h);
            using var clone = (Solid3d)tool.Clone(); target.BooleanOperation(op, clone);
        }
        return new JObject { ["handle"] = targetHandle, ["volume"] = target.MassProperties.Volume, ["toolsPreserved"] = true };
    });
    // The native offset engine preserves straight and arc segments and miters corners.
    private static Polyline OffsetPolyline(Polyline source, double distance)
    {
        var curves = source.GetOffsetCurves(distance);
        try
        {
            Validation.Require(curves.Count == 1 && curves[0] is Polyline, "Offset discontinuo o imposible; revisa radios y ancho del muro.");
            return (Polyline)((Polyline)curves[0]).Clone();
        }
        finally { foreach (DBObject obj in curves) obj.Dispose(); curves.Dispose(); }
    }
    private static Polyline Ribbon(Polyline source, double left, double right, double elevation)
    {
        using var probe = OffsetPolyline(source, left);
        var tangent = source.GetFirstDerivative(source.StartParam);
        var sign = tangent.CrossProduct(probe.StartPoint - source.StartPoint).Z > 0 ? 1 : -1;
        using var a = OffsetPolyline(source, sign * left); using var b = OffsetPolyline(source, -sign * right); b.ReverseCurve();
        var result = new Polyline(a.NumberOfVertices + b.NumberOfVertices) { Closed = true, Elevation = elevation };
        var index = 0;
        foreach (var p in new[] { a, b }) for (var i = 0; i < p.NumberOfVertices; i++)
            result.AddVertexAt(index++, p.GetPoint2dAt(i), i == p.NumberOfVertices - 1 ? 0 : p.GetBulgeAt(i), 0, 0);
        return result;
    }
    private static JToken Wall(BridgeRequest r) => CadExecution.Run(r, true, (doc, tr) =>
    {
        var a = r.Arguments; var source = CadExecution.Entity<Polyline>(doc, tr, ToolArgs.Text(a, "sourceHandle"));
        Validation.Require(!source.Closed && source.NumberOfVertices >= 2 && source.NumberOfVertices <= 1000 && source.Normal.IsEqualTo(Vector3d.ZAxis), "Usa una polilínea abierta horizontal en WCS con 2 a 1000 vértices.");
        var thickness = ToolArgs.Number(a, "stemThickness", positive: true); var height = ToolArgs.Number(a, "stemHeight", positive: true);
        var heel = ToolArgs.Number(a, "heel", positive: true); var toe = ToolArgs.Number(a, "toe", positive: true); var footing = ToolArgs.Number(a, "footingThickness", positive: true);
        var top = ToolArgs.Number(a, "footingTopElevation", source.Elevation);
        using var stemPlan = Ribbon(source, thickness / 2, thickness / 2, top);
        using var basePlan = Ribbon(source, heel + thickness / 2, toe + thickness / 2, top - footing);
        using var stem = Extruded(stemPlan, height); using var slab = Extruded(basePlan, footing);
        CadExecution.Append(doc, tr, stem, CadExecution.Layer(doc, tr, ToolArgs.Text(a, "stemLayer", "MURO_PANTALLA"), 8));
        CadExecution.Append(doc, tr, slab, CadExecution.Layer(doc, tr, ToolArgs.Text(a, "footingLayer", "MURO_ZAPATA"), 9));
        return new JObject { ["sourceHandle"] = source.Handle.ToString(), ["sourcePreserved"] = true, ["pathLength"] = source.Length,
            ["footingWidth"] = heel + thickness + toe, ["totalHeight"] = footing + height, ["footingTopElevation"] = top,
            ["heelSide"] = "left in source traversal direction", ["stemHandle"] = stem.Handle.ToString(), ["footingHandle"] = slab.Handle.ToString(),
            ["stemVolume"] = stem.MassProperties.Volume, ["footingVolume"] = slab.MassProperties.Volume, ["units"] = doc.Database.Insunits.ToString() };
    }, true);
    private static JToken View(BridgeRequest r) => CadExecution.Run(r, false, (doc, tr) =>
    {
        var orientation = ToolArgs.Text(r.Arguments, "orientation");
        using var view = doc.Editor.GetCurrentView();
        view.ViewDirection = orientation == "top" ? Vector3d.ZAxis : orientation == "isometric" ? new Vector3d(1, -1, 1) : throw new CadFault("INVALID_ARGUMENT", "orientation debe ser top o isometric.");
        var db = doc.Database; db.UpdateExt(true); var ext = new Extents3d(db.Extmin, db.Extmax);
        var transform = Matrix3d.PlaneToWorld(view.ViewDirection); transform = Matrix3d.Displacement(view.Target - Point3d.Origin) * transform; transform = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * transform;
        ext.TransformBy(transform.Inverse());
        var aspect = view.Width / view.Height; var width = Math.Max(1, ext.MaxPoint.X - ext.MinPoint.X) * 1.15; var height = Math.Max(1, ext.MaxPoint.Y - ext.MinPoint.Y) * 1.15;
        view.Width = Math.Max(width, height * aspect); view.Height = view.Width / aspect; view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
        doc.Editor.SetCurrentView(view); return new JObject { ["orientation"] = orientation };
    });
}
