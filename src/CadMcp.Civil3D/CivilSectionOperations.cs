using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilSection = Autodesk.Civil.DatabaseServices.Section;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace CadMcp.Civil3D;

public static partial class CivilExtendedOperations
{
    private const string CutRecord = "CADMCP_SOLID_SECTIONS_V1";
    private const string CutOwner = "CADMCP_SOLID_CUT_OWNER_V1";

    private static string[] OptionalHandles(JObject a, string key, int max = 100)
        => a[key] == null || a[key] is JArray empty && empty.Count == 0 ? Array.Empty<string>() : ToolArgs.Handles(a, key, max);

    private static Alignment GroupAlignment(Document d, Transaction tr, SampleLineGroup group)
    {
        foreach (ObjectId id in Civil(d).GetAlignmentIds())
        {
            var alignment = (Alignment)tr.GetObject(id, OpenMode.ForRead);
            if (alignment.GetSampleLineGroupIds().Cast<ObjectId>().Contains(group.ObjectId)) return alignment;
        }
        throw new CadFault("OBJECT_NOT_FOUND", "No se encontró el alineamiento del grupo de muestreo.");
    }

    private static JArray SourceRows(Transaction tr, SampleLineGroup group) => new JArray(group.GetSectionSources().Cast<SectionSource>().Select(source =>
    {
        var obj = tr.GetObject(source.SourceId, OpenMode.ForRead);
        return new JObject { ["sourceHandle"] = Handle(source.SourceId), ["name"] = obj is Autodesk.Civil.DatabaseServices.Entity e ? e.Name : obj.GetType().Name,
            ["sourceType"] = source.SourceType.ToString(), ["sampled"] = source.IsSampled, ["updateMode"] = source.UpdateMode.ToString(),
            ["styleHandle"] = Handle(source.StyleId), ["sectionHandles"] = new JArray(source.GetSectionIds().Cast<ObjectId>().Select(Handle)) };
    }));

    private static JToken SectionSourcesRead(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var group = Entity<SampleLineGroup>(d, tr, r.Arguments, "sampleLineGroupHandle");
        return new JObject { ["sampleLineGroupHandle"] = Handle(group.ObjectId), ["sources"] = SourceRows(tr, group) };
    });

    private static void ConfigureSources(Document d, Transaction tr, SampleLineGroup group, string[] handles, bool replace, bool dynamic, JObject args)
    {
        var sources = group.GetSectionSources().Cast<SectionSource>().ToArray();
        var ids = handles.Select(h => DocumentAccess.ObjectId(d.Database, h)).ToArray();
        Validation.Require(ids.All(id => sources.Any(s => s.SourceId == id)), "Fuente no disponible en este grupo. Consulta civil_get_section_sources.");
        var style = args["sectionStyleHandle"] == null ? ObjectId.Null : Style(d, tr, args, "sectionStyleHandle", "section");
        foreach (var source in sources)
        {
            var selected = ids.Contains(source.SourceId);
            if (replace || selected) source.IsSampled = selected;
            if (!selected) continue;
            source.UpdateMode = dynamic ? SectionUpdateType.Dynamic : SectionUpdateType.Static;
            if (!style.IsNull) source.StyleId = style;
            foreach (ObjectId id in source.GetSectionIds())
            {
                var section = CadExecution.Entity<CivilSection>(d, tr, id.Handle.ToString(), true);
                section.UpdateMode = source.UpdateMode;
                if (!style.IsNull) section.StyleId = style;
            }
        }
    }

    private static JToken SectionSourcesSet(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var group = Entity<SampleLineGroup>(d, tr, a, "sampleLineGroupHandle", true);
        ConfigureSources(d, tr, group, OptionalHandles(a, "sourceHandles"), ToolArgs.Flag(a, "replace"), ToolArgs.Flag(a, "dynamic", true), a);
        return new JObject { ["sampleLineGroupHandle"] = Handle(group.ObjectId), ["sources"] = SourceRows(tr, group) };
    });

    private static void ConfigureView(Document d, Transaction tr, SectionView view, JObject a)
    {
        var left = ToolArgs.Number(a, "leftWidth", -view.OffsetLeft, true);
        var right = ToolArgs.Number(a, "rightWidth", view.OffsetRight, true);
        var min = ToolArgs.Number(a, "elevationMin", view.ElevationMin);
        var max = ToolArgs.Number(a, "elevationMax", view.ElevationMax);
        SectionPlanning.Ranges(left, right, min, max);
        if (a["styleHandle"] != null) view.StyleId = Style(d, tr, a, "styleHandle", "sectionView");
        if (a["leftWidth"] != null || a["rightWidth"] != null)
        {
            view.IsOffsetRangeAutomatic = false; view.OffsetLeft = -left; view.OffsetRight = right;
        }
        if (a["elevationMin"] != null || a["elevationMax"] != null)
        {
            view.IsElevationRangeAutomatic = false;
            // Expand first, then contract: setters must never see an inverted intermediate range.
            if (min >= view.ElevationMax) view.ElevationMax = max;
            if (max <= view.ElevationMin) view.ElevationMin = min;
            view.ElevationMin = min; view.ElevationMax = max;
        }
        if (a["bandSetStyleHandle"] != null) view.Bands.ImportBandSetStyle(Style(d, tr, a, "bandSetStyleHandle", "sectionViewBandSet"));
    }

    private static JObject SectionViewInfo(Transaction tr, SectionView view)
    {
        var row = Id(view); row["sampleLineHandle"] = Handle(view.ParentEntityId);
        row["location"] = CadOperations.Point(view.Location); row["offsetLeft"] = view.OffsetLeft; row["offsetRight"] = view.OffsetRight;
        row["elevationMin"] = view.ElevationMin; row["elevationMax"] = view.ElevationMax; row["styleHandle"] = Handle(view.StyleId);
        row["solidSections"] = ReadRecord(tr, view, CutRecord);
        return row;
    }

    private static Matrix3d ViewMap(SectionView view, out double sx, out double sy)
    {
        var offset = (view.OffsetLeft + view.OffsetRight) / 2;
        var elevation = (view.ElevationMin + view.ElevationMax) / 2;
        var du = (view.OffsetRight - view.OffsetLeft) / 4;
        var dz = (view.ElevationMax - view.ElevationMin) / 4;
        Validation.Require(du > 0 && dz > 0 && Validation.Finite(du) && Validation.Finite(dz), "Rangos nativos de vista inválidos.");
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        // All probes lie inside the graph, including terrain whose elevations are far from zero.
        var ok = view.FindXYAtOffsetAndElevation(offset, elevation, ref x0, ref y0)
            && view.FindXYAtOffsetAndElevation(offset + du, elevation, ref x1, ref y1)
            && view.FindXYAtOffsetAndElevation(offset, elevation + dz, ref x2, ref y2);
        var ux = (x1-x0)/du; var uy = (y1-y0)/du; var vx = (x2-x0)/dz; var vy = (y2-y0)/dz;
        sx = Math.Sqrt(ux*ux+uy*uy); sy = Math.Sqrt(vx*vx+vy*vy);
        Validation.Require(ok && new[] { x0,y0,ux,uy,vx,vy,sx,sy }.All(Validation.Finite)
            && Math.Abs(ux*vy-vx*uy) > 1e-12, "No se pudo resolver la transformación de la vista Civil.");
        return new Matrix3d(new[] { ux,vx,0,x0-ux*offset-vx*elevation,
            uy,vy,0,y0-uy*offset-vy*elevation, 0,0,1,view.Location.Z, 0,0,0,1 });
    }

    private static SectionView[] CreateSectionViews(Document d, Transaction tr, SampleLineGroup group, JObject a, string prefix)
    {
        var alignment = GroupAlignment(d, tr, group);
        var start = ToolArgs.Number(a, "startStation", alignment.StartingStation);
        var end = ToolArgs.Number(a, "endStation", alignment.EndingStation);
        Validation.Require(start >= alignment.StartingStation && end <= alignment.EndingStation && start <= end, "Rango fuera del alineamiento.");
        var selected = group.GetSampleLineIds().Cast<ObjectId>().Select(id => (SampleLine)tr.GetObject(id, OpenMode.ForRead))
            .Where(line => line.Station >= start - 1e-8 && line.Station <= end + 1e-8).OrderBy(line => line.Station).ToArray();
        Validation.Require(selected.Length > 0 && selected.Length <= SectionPlanning.MaximumSections, "Se requieren entre 1 y 200 líneas de muestreo en el rango.");
        var left = ToolArgs.Number(a, "leftWidth", positive: true); var right = ToolArgs.Number(a, "rightWidth", positive: true);
        var min = ToolArgs.Number(a, "elevationMin"); var max = ToolArgs.Number(a, "elevationMax"); SectionPlanning.Ranges(left, right, min, max);
        var position = ToolArgs.Point(a, "position");
        Validation.Require(a["columns"] == null || a["columns"]!.Type == JTokenType.Integer, "columns debe ser entero.");
        var columns = (int?)a["columns"] ?? 4;
        using var ranges = new SectionViewGroupCreationRangeOptions(group.ObjectId);
        ranges.UseUserSpecifiedOffset = true; ranges.SetOffsetRange(-left, right);
        ranges.UseUserSpecifiedElevation = true; ranges.Elevation = min;
        var placement = new SectionViewGroupCreationPlacementOptions(); placement.UseDraftPlacement();
        // Native Civil SectionViewGroup, not a collection of drawn grid lines.
        var nativeStart = Math.Max(alignment.StartingStation, Math.Min(start, selected[0].Station));
        var nativeEnd = Math.Min(alignment.EndingStation, Math.Max(end, selected[selected.Length - 1].Station));
        var nativeGroup = group.SectionViewGroups.Add(CadOperations.ToPoint(position), nativeStart, nativeEnd, ranges, placement);
        var views = nativeGroup.GetSectionViewIds().Cast<ObjectId>().Select(id => CadExecution.Entity<SectionView>(d, tr, id.Handle.ToString(), true))
            .OrderBy(view => ((SampleLine)tr.GetObject(view.ParentEntityId, OpenMode.ForRead)).Station).ToArray();
        Validation.Require(views.Length == selected.Length, "Civil no creó una vista por cada línea seleccionada.");
        foreach (var view in views) ConfigureView(d, tr, view, a);
        double largestWidth = 0, largestHeight = 0;
        foreach (var view in views)
        {
            ViewMap(view, out var sx, out var sy);
            largestWidth = Math.Max(largestWidth, (left + right) * sx);
            largestHeight = Math.Max(largestHeight, (max - min) * sy);
        }
        var cellWidth = ToolArgs.Number(a, "columnSpacing", largestWidth + 15, true);
        var cellHeight = ToolArgs.Number(a, "rowSpacing", largestHeight + 15, true);
        for (var i = 0; i < views.Length; i++)
        {
            var view = views[i]; view.Name = prefix + "_" + (i + 1).ToString("000", CultureInfo.InvariantCulture);
            view.Location = CadOperations.ToPoint(SectionPlanning.Position(position, i, columns, cellWidth, cellHeight));
        }
        return views;
    }

    private static JToken SectionViewsCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var group = Entity<SampleLineGroup>(d, tr, a, "sampleLineGroupHandle", true);
        var views = CreateSectionViews(d, tr, group, a, ToolArgs.Text(a, "namePrefix"));
        return new JObject { ["sampleLineGroupHandle"] = Handle(group.ObjectId), ["nativeSectionViewGroup"] = true,
            ["views"] = new JArray(views.Select(view => SectionViewInfo(tr, view))) };
    }, true);

    private static JObject? ReadRecord(Transaction tr, DBObject owner, string key)
    {
        if (owner.ExtensionDictionary.IsNull) return null;
        var dict = (DBDictionary)tr.GetObject(owner.ExtensionDictionary, OpenMode.ForRead);
        if (!dict.Contains(key)) return null;
        var record = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForRead);
        using var data = record.Data;
        var text = string.Concat(data.AsArray().Where(v => v.TypeCode == (int)DxfCode.Text).Select(v => v.Value as string));
        Validation.Require(text.Length <= 1024 * 1024, "Metadatos de sección demasiado grandes.");
        var value = JObject.Parse(text);
        Validation.Require((int?)value["schema"] == 1, "Versión de metadatos de corte incompatible.");
        return value;
    }

    private static void WriteRecord(Transaction tr, DBObject owner, string key, JObject value)
    {
        if (!owner.IsWriteEnabled) owner.UpgradeOpen();
        if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
        var dict = (DBDictionary)tr.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite);
        Xrecord record;
        if (dict.Contains(key)) record = (Xrecord)tr.GetObject(dict.GetAt(key), OpenMode.ForWrite);
        else { record = new Xrecord(); dict.SetAt(key, record); tr.AddNewlyCreatedDBObject(record, true); }
        var json = value.ToString(Newtonsoft.Json.Formatting.None);
        Validation.Require(json.Length <= 1024 * 1024, "Metadatos de sección demasiado grandes.");
        var chunks = new List<TypedValue>();
        for (var i = 0; i < json.Length; i += 240) chunks.Add(new TypedValue((int)DxfCode.Text, json.Substring(i, Math.Min(240, json.Length - i))));
        using var buffer = new ResultBuffer(chunks.ToArray()); record.Data = buffer;
    }

    private static Region ClipRectangle(double left, double right, double min, double max)
    {
        using var poly = new Polyline(4) { Closed = true };
        poly.AddVertexAt(0, new Point2d(left, min), 0, 0, 0); poly.AddVertexAt(1, new Point2d(right, min), 0, 0, 0);
        poly.AddVertexAt(2, new Point2d(right, max), 0, 0, 0); poly.AddVertexAt(3, new Point2d(left, max), 0, 0, 0);
        using var input = new DBObjectCollection(); input.Add(poly);
        using var regions = Region.CreateFromCurves(input);
        try { Validation.Require(regions.Count == 1, "No se pudo construir el límite de corte."); return (Region)regions[0].Clone(); }
        finally { foreach (DBObject region in regions) region.Dispose(); }
    }

    private static JObject AddSolidCuts(Document d, Transaction tr, SectionView view, string[] solidHandles, bool replace, int curveBudget = 20000)
    {
        var old = ReadRecord(tr, view, CutRecord);
        Validation.Require(old == null || replace, "La vista ya tiene cortes MCP. Usa replaceExisting=true para regenerarlos.");
        var owned = new List<AcEntity>();
        if (old != null)
        {
            Validation.Require((string?)old["viewHandle"] == view.Handle.ToString(), "Metadatos de vista incompatibles.");
            foreach (var handle in OptionalHandles(old, "generatedHandles", 20000))
            {
                try { DocumentAccess.ObjectId(d.Database, handle); }
                catch (CadFault ex) when (ex.Code == "OBJECT_NOT_FOUND") { continue; }
                var curve = CadExecution.Entity<AcEntity>(d, tr, handle, true);
                var mark = ReadRecord(tr, curve, CutOwner);
                Validation.Require(curve is Curve && (string?)mark?["viewHandle"] == view.Handle.ToString(), "No se borró geometría ajena o con metadatos alterados: " + handle);
                owned.Add(curve);
            }
        }
        var line = (SampleLine)tr.GetObject(view.ParentEntityId, OpenMode.ForRead);
        var group = (SampleLineGroup)tr.GetObject(line.GroupId, OpenMode.ForRead); var alignment = GroupAlignment(d, tr, group);
        double cx = 0, cy = 0, rx = 0, ry = 0;
        alignment.PointLocation(line.Station, 0, ref cx, ref cy); alignment.PointLocation(line.Station, 1, ref rx, ref ry);
        var vertices = line.Vertices.Cast<SampleLineVertex>().Select(v => v.Location).ToArray();
        Validation.Require(vertices.Length >= 2, "La línea de muestreo no tiene dos extremos.");
        var origin = new Point3d(cx, cy, 0); var right = vertices[vertices.Length - 1] - vertices[0]; right = new Vector3d(right.X, right.Y, 0).GetNormal();
        if (right.DotProduct(new Vector3d(rx - cx, ry - cy, 0)) < 0) right = -right;
        var normal = right.CrossProduct(Vector3d.ZAxis).GetNormal();
        Validation.Require(vertices.All(p => Math.Abs((p - origin).DotProduct(normal)) <= 1e-6), "Los cortes sólidos requieren una línea de muestreo recta que pase por el eje.");
        using var plane = new Plane(origin, normal);
        var toLocal = Matrix3d.AlignCoordinateSystem(origin, right, Vector3d.ZAxis, normal, Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis);
        // The native mapping incorporates graph direction and vertical exaggeration.
        var toView = ViewMap(view, out _, out _);
        var generated = new List<string>(); var rows = new JArray(); var output = new List<AcEntity>();
        var bt = (BlockTable)tr.GetObject(d.Database.BlockTableId, OpenMode.ForRead); var modelId = bt[BlockTableRecord.ModelSpace];
        try
        {
            foreach (var handle in solidHandles)
            {
                var solid = CadExecution.Entity<Solid3d>(d, tr, handle);
                Validation.Require(solid.OwnerId == modelId, "Se requiere un sólido directo en ModelSpace, no uno anidado en un bloque.");
                using var section = solid.GetSection(plane);
                if (section == null)
                {
                    rows.Add(new JObject { ["solidHandle"] = handle, ["status"] = "no_valid_region", ["note"] = "No hay intersección regional; un plano coincidente con una cara terminal también puede producir este resultado. No se desplazó el plano." });
                    continue;
                }
                section.TransformBy(toLocal);
                var bounds = section.GeometricExtents;
                if (bounds.MaxPoint.X <= view.OffsetLeft || bounds.MinPoint.X >= view.OffsetRight || bounds.MaxPoint.Y <= view.ElevationMin || bounds.MinPoint.Y >= view.ElevationMax)
                { rows.Add(new JObject { ["solidHandle"] = handle, ["status"] = "outside_view" }); continue; }
                using var clip = ClipRectangle(view.OffsetLeft, view.OffsetRight, view.ElevationMin, view.ElevationMax);
                section.BooleanOperation(BooleanOperationType.BoolIntersect, clip);
                var area = section.Area;
                if (area <= 1e-12) { rows.Add(new JObject { ["solidHandle"] = handle, ["status"] = "outside_view" }); continue; }
                var edges = new Queue<DBObject>();
                using (var exploded = new DBObjectCollection()) { section.Explode(exploded); foreach (DBObject item in exploded) edges.Enqueue(item); }
                var firstIndex = output.Count;
                try
                {
                    while (edges.Count > 0)
                    {
                        using var item = edges.Dequeue();
                        if (item is Region nested)
                        {
                            using var children = new DBObjectCollection(); nested.Explode(children); foreach (DBObject child in children) edges.Enqueue(child); continue;
                        }
                        Validation.Require(item is Curve, "Tipo de contorno de corte no soportado: " + item.GetType().Name);
                        var curve = (Curve)item; AcEntity transformed;
                        try { transformed = curve.GetTransformedCopy(toView); }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.CannotScaleNonUniformly)
                        {
                            // Exact rational spline conversion; no sampled or faceted approximation.
                            transformed = curve.Spline;
                            try { transformed.TransformBy(toView); } catch { transformed.Dispose(); throw; }
                        }
                        output.Add(transformed);
                        Validation.Require(output.Count <= curveBudget, "Máximo 20000 contornos de sólidos por operación.");
                        var sourceLayer = (LayerTableRecord)tr.GetObject(solid.LayerId, OpenMode.ForRead);
                        var color = sourceLayer.Color.ColorIndex; if (color < 1 || color > 255) color = 7;
                        var layer = CadExecution.Layer(d, tr, "SECC_CORTE_" + solid.Layer, color);
                        var created = CadExecution.Append(d, tr, transformed, layer); transformed.Color = solid.Color;
                        WriteRecord(tr, transformed, CutOwner, new JObject { ["schema"] = 1, ["viewHandle"] = view.Handle.ToString() }); generated.Add(created);
                    }
                }
                finally { while (edges.Count > 0) edges.Dequeue().Dispose(); }
                rows.Add(new JObject { ["solidHandle"] = handle, ["status"] = "cut", ["area"] = area, ["curveCount"] = output.Count - firstIndex });
            }
            foreach (var curve in owned) curve.Erase();
            var record = new JObject { ["schema"] = 1, ["viewHandle"] = view.Handle.ToString(), ["sourceHandles"] = new JArray(solidHandles),
                ["generatedHandles"] = new JArray(generated), ["station"] = line.Station, ["updateMode"] = "explicit_mcp_refresh",
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["results"] = rows };
            WriteRecord(tr, view, CutRecord, record);
            return record;
        }
        finally { foreach (var entity in output) entity.Dispose(); }
    }

    private static JToken SolidSectionsAdd(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var viewHandles = ToolArgs.Handles(a, "sectionViewHandles", SectionPlanning.MaximumSections); var solids = ToolArgs.Handles(a, "solidHandles", 100);
        Validation.Require(viewHandles.Length * solids.Length <= SectionPlanning.MaximumSolidPairs, "Máximo 1000 combinaciones vista/sólido.");
        var rows = new JArray(); var budget = 20000;
        foreach (var h in viewHandles)
        {
            var row = AddSolidCuts(d, tr, CadExecution.Entity<SectionView>(d, tr, h, true), solids, ToolArgs.Flag(a, "replaceExisting", true), budget);
            budget -= ((JArray)row["generatedHandles"]!).Count; rows.Add(row);
        }
        return new JObject { ["solidUpdateMode"] = "explicit_mcp_refresh", ["views"] = rows };
    }, true);

    private static JToken SectionViewsUpdate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var rows = new JArray(); var count = 0; var budget = 20000;
        foreach (var handle in ToolArgs.Handles(a, "sectionViewHandles", SectionPlanning.MaximumSections))
        {
            var view = CadExecution.Entity<SectionView>(d, tr, handle, true); ConfigureView(d, tr, view, a);
            var record = ReadRecord(tr, view, CutRecord);
            if (record != null && ToolArgs.Flag(a, "refreshSolidSections", true))
            {
                var solids = ToolArgs.Handles(record, "sourceHandles", 100); count += solids.Length;
                Validation.Require(count <= SectionPlanning.MaximumSolidPairs, "Máximo 1000 combinaciones vista/sólido.");
                var result = AddSolidCuts(d, tr, view, solids, true, budget); budget -= ((JArray)result["generatedHandles"]!).Count;
            }
            var row = SectionViewInfo(tr, view);
            if (record != null && !ToolArgs.Flag(a, "refreshSolidSections", true)) row["warning"] = "Los cortes sólidos existentes no se regeneraron.";
            rows.Add(row);
        }
        return new JObject { ["views"] = rows };
    }, true);

    private static JToken GenerateCrossSections(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle");
        var start = ToolArgs.Number(a, "startStation", alignment.StartingStation); var end = ToolArgs.Number(a, "endStation", alignment.EndingStation);
        Validation.Require(start >= alignment.StartingStation && end <= alignment.EndingStation, "Rango fuera del alineamiento.");
        var stations = SectionPlanning.Stations(start, end, ToolArgs.Number(a, "interval", positive: true));
        var left = ToolArgs.Number(a, "leftWidth", positive: true); var right = ToolArgs.Number(a, "rightWidth", positive: true);
        SectionPlanning.Ranges(left, right, ToolArgs.Number(a, "elevationMin"), ToolArgs.Number(a, "elevationMax"));
        var surfaces = OptionalHandles(a, "surfaceHandles"); var solids = OptionalHandles(a, "solidHandles");
        Validation.Require(surfaces.Length + solids.Length > 0, "Selecciona al menos una superficie o un sólido.");
        Validation.Require(stations.Length * solids.Length <= SectionPlanning.MaximumSolidPairs, "Máximo 1000 combinaciones vista/sólido.");
        foreach (var h in surfaces) CadExecution.Entity<Autodesk.Civil.DatabaseServices.Surface>(d, tr, h);
        foreach (var h in solids) CadExecution.Entity<Solid3d>(d, tr, h);
        var name = ToolArgs.Text(a, "name"); var gid = SampleLineGroup.Create(name, alignment.ObjectId); var group = (SampleLineGroup)tr.GetObject(gid, OpenMode.ForWrite);
        ConfigureSources(d, tr, group, surfaces, true, true, a);
        var lines = new JArray(); var index = 0;
        foreach (var station in stations)
        {
            double lx = 0, ly = 0, rx = 0, ry = 0; alignment.PointLocation(station, -left, ref lx, ref ly); alignment.PointLocation(station, right, ref rx, ref ry);
            var points = new Point2dCollection(new[] { new Point2d(lx, ly), new Point2d(rx, ry) });
            var id = SampleLine.Create(name + "_SL_" + (++index).ToString("000", CultureInfo.InvariantCulture), gid, points);
            lines.Add(Handle(id));
        }
        var views = CreateSectionViews(d, tr, group, a, name + "_SV");
        var rows = new JArray(); var budget = 20000;
        foreach (var view in views)
        {
            if (solids.Length > 0) { var result = AddSolidCuts(d, tr, view, solids, true, budget); budget -= ((JArray)result["generatedHandles"]!).Count; }
            rows.Add(SectionViewInfo(tr, view));
        }
        return new JObject { ["sampleLineGroupHandle"] = Handle(gid), ["sampleLineHandles"] = lines, ["nativeSectionViewGroup"] = true,
            ["surfaceUpdateMode"] = "Dynamic", ["solidUpdateMode"] = "explicit_mcp_refresh", ["sources"] = SourceRows(tr, group), ["views"] = rows };
    }, true);
}
