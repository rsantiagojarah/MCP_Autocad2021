using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;

namespace CadMcp.Civil3D;

public static partial class CivilExtendedOperations
{
    private static JToken AlignmentCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var source = Entity<Polyline>(d, tr, a, "sourceHandle");
        Validation.Require(!source.Closed, "El eje requiere una polilínea abierta.");
        var layer = CadExecution.Layer(d, tr, ToolArgs.Text(a, "layer", "C-ROAD-CNTR"));
        var options = new PolylineOptions { PlineId = source.ObjectId, EraseExistingEntities = false, AddCurvesBetweenTangents = false };
        var id = Alignment.Create(Civil(d), options, ToolArgs.Text(a, "name"), ObjectId.Null, layer, Style(d, tr, a, "styleHandle", "alignment"), Style(d, tr, a, "labelSetHandle", "alignmentLabelSet"));
        var alignment = (Alignment)tr.GetObject(id, OpenMode.ForWrite);
        alignment.ReferencePoint = new Point2d(source.StartPoint.X, source.StartPoint.Y);
        alignment.ReferencePointStation = ToolArgs.Number(a, "startingStation", 0);
        var row = Id(alignment); row["startingStation"] = alignment.StartingStation; row["endingStation"] = alignment.EndingStation; row["sourcePreserved"] = true; return row;
    }, true);
    private static JToken AlignmentSample(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle"); var samples = ToolArgs.List<StationOffsetSpec>(a, "samples"); var rows = new JArray();
        foreach (var sample in samples)
        {
            Validation.Require(Validation.Finite(sample.Station) && Validation.Finite(sample.Offset), "Estación/offset no finitos.");
            if (sample.Station < alignment.StartingStation || sample.Station > alignment.EndingStation) { rows.Add(new JObject { ["station"] = sample.Station, ["error"] = "OUTSIDE_ALIGNMENT" }); continue; }
            double x = 0, y = 0; alignment.PointLocation(sample.Station, sample.Offset, ref x, ref y);
            rows.Add(new JObject { ["station"] = sample.Station, ["offset"] = sample.Offset, ["x"] = x, ["y"] = y });
        }
        return new JObject { ["samples"] = rows, ["units"] = d.Database.Insunits.ToString(), ["coordinateSpace"] = "WCS" };
    }, true);
    private static JToken StationOffset(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle"); using var points = Points(a); var rows = new JArray();
        foreach (Point3d point in points)
        {
            double station = 0, offset = 0;
            try { alignment.StationOffset(point.X, point.Y, ref station, ref offset); rows.Add(new JObject { ["station"] = station, ["offset"] = offset }); }
            catch (System.Exception ex) when (ex.GetType().Name == "PointNotOnEntityException") { rows.Add(new JObject { ["error"] = "OUTSIDE_ALIGNMENT" }); }
        }
        return new JObject { ["samples"] = rows };
    }, true);
    private static JToken SurfaceProfile(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle"); var surface = Entity<Autodesk.Civil.DatabaseServices.Surface>(d, tr, a, "surfaceHandle");
        var layer = CadExecution.Layer(d, tr, ToolArgs.Text(a, "layer", "C-ROAD-PROF"));
        var start = ToolArgs.Number(a, "startStation", alignment.StartingStation); var end = ToolArgs.Number(a, "endStation", alignment.EndingStation);
        Validation.Require(start >= alignment.StartingStation && end <= alignment.EndingStation && start < end, "Rango de estaciones inválido.");
        var id = Profile.CreateFromSurface(ToolArgs.Text(a, "name"), alignment.ObjectId, surface.ObjectId, layer,
            Style(d, tr, a, "styleHandle", "profile"), Style(d, tr, a, "labelSetHandle", "profileLabelSet"), ToolArgs.Number(a, "offset", 0), start, end);
        return Id((Profile)tr.GetObject(id, OpenMode.ForRead));
    }, true);
    private static JToken LayoutProfile(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle"); var pvis = ToolArgs.List<PviSpec>(a, "pvis", 2, 1000);
        for (var i = 0; i < pvis.Count; i++) Validation.Require(Validation.Finite(pvis[i].Station) && Validation.Finite(pvis[i].Elevation) && pvis[i].Station >= alignment.StartingStation && pvis[i].Station <= alignment.EndingStation && (i == 0 || pvis[i].Station > pvis[i - 1].Station), "Los PVI requieren estaciones crecientes dentro del eje y elevaciones finitas.");
        var id = Profile.CreateByLayout(ToolArgs.Text(a, "name"), alignment.ObjectId, CadExecution.Layer(d, tr, ToolArgs.Text(a, "layer", "C-ROAD-PROF-PROP")), Style(d, tr, a, "styleHandle", "profile"), Style(d, tr, a, "labelSetHandle", "profileLabelSet"));
        var profile = (Profile)tr.GetObject(id, OpenMode.ForWrite); foreach (var pvi in pvis) profile.PVIs.AddPVI(pvi.Station, pvi.Elevation);
        return Id(profile);
    }, true);
    private static JToken ProfileSample(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var a = r.Arguments; var profile = Entity<Profile>(d, tr, a, "profileHandle"); var stations = ToolArgs.List<double>(a, "stations"); var rows = new JArray();
        foreach (var station in stations)
        {
            Validation.Require(Validation.Finite(station), "Estación no finita.");
            if (station < profile.StartingStation || station > profile.EndingStation) { rows.Add(new JObject { ["station"] = station, ["error"] = "OUTSIDE_PROFILE" }); continue; }
            rows.Add(new JObject { ["station"] = station, ["elevation"] = profile.ElevationAt(station) });
        }
        return new JObject { ["samples"] = rows, ["units"] = d.Database.Insunits.ToString() };
    }, true);
    private static JToken ProfileViewCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle");
        var id = ProfileView.Create(alignment.ObjectId, CadOperations.ToPoint(ToolArgs.Point(a, "position"))); var view = (ProfileView)tr.GetObject(id, OpenMode.ForWrite);
        if (a["name"] != null) view.Name = ToolArgs.Text(a, "name"); if (a["styleHandle"] != null) view.StyleId = Style(d, tr, a, "styleHandle", "profileView"); return Id(view);
    }, true);
    private static JToken FeatureCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var source = Entity<Autodesk.AutoCAD.DatabaseServices.Curve>(d, tr, a, "sourceHandle");
        Validation.Require(source is Polyline || source is Polyline3d || source is Line || source is Arc, "Objeto fuente incompatible.");
        var id = a["siteHandle"] == null ? FeatureLine.Create(ToolArgs.Text(a, "name"), source.ObjectId) : FeatureLine.Create(ToolArgs.Text(a, "name"), source.ObjectId, Entity<Site>(d, tr, a, "siteHandle").ObjectId);
        var f = (FeatureLine)tr.GetObject(id, OpenMode.ForWrite);
        if (a["styleHandle"] != null) f.StyleId = Style(d, tr, a, "styleHandle", "featureLine");
        return Id(f);
    }, true);
    private static JToken FeatureElevations(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var f = Entity<FeatureLine>(d, tr, a, "featureLineHandle", true); var s = Entity<Autodesk.Civil.DatabaseServices.Surface>(d, tr, a, "surfaceHandle");
        f.AssignElevationsFromSurface(s.ObjectId, ToolArgs.Flag(a, "insertIntermediatePoints", true)); return Id(f);
    }, true);
    private static JToken SiteCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) => Id((Site)tr.GetObject(Site.Create(Civil(d), ToolArgs.Text(r.Arguments, "name")), OpenMode.ForRead)));
    private static JToken SampleLines(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var alignment = Entity<Alignment>(d, tr, a, "alignmentHandle"); var name = ToolArgs.Text(a, "name"); var stations = ToolArgs.List<double>(a, "stations", 1, 500);
        Validation.Require(stations.Distinct().Count() == stations.Count && stations.All(s => Validation.Finite(s) && s >= alignment.StartingStation && s <= alignment.EndingStation), "Estaciones inválidas o duplicadas.");
        var left = ToolArgs.Number(a, "leftWidth", positive: true); var right = ToolArgs.Number(a, "rightWidth", positive: true);
        var id = SampleLineGroup.Create(name, alignment.ObjectId); var group = (SampleLineGroup)tr.GetObject(id, OpenMode.ForWrite);
        if (a["surfaceHandles"] != null)
        {
            var surfaceIds = ToolArgs.Handles(a, "surfaceHandles", 100).Select(h => CadExecution.Entity<Autodesk.Civil.DatabaseServices.Surface>(d, tr, h).ObjectId).ToArray();
            var available = group.GetSectionSources().Cast<SectionSource>().ToArray();
            Validation.Require(surfaceIds.All(sid => available.Any(s => s.SourceId == sid)), "Fuente de sección no disponible.");
            foreach (var source in available) source.IsSampled = surfaceIds.Contains(source.SourceId);
        }
        var rows = new JArray(); var index = 0;
        foreach (var station in stations)
        {
            // PointLocation uses negative offsets on the left, positive offsets on the right.
            double lx = 0, ly = 0, rx = 0, ry = 0; alignment.PointLocation(station, -left, ref lx, ref ly); alignment.PointLocation(station, right, ref rx, ref ry);
            var points = new Point2dCollection(new[] { new Point2d(lx, ly), new Point2d(rx, ry) });
            var sid = SampleLine.Create(name + "_" + (++index), id, points); rows.Add(Handle(sid));
        }
        return new JObject { ["groupHandle"] = Handle(id), ["sampleLineHandles"] = rows };
    }, true);
    private static JToken Parts(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var a = r.Arguments; var list = (PartsList)tr.GetObject(DocumentAccess.ObjectId(d.Database, ToolArgs.Text(a, "partsListHandle")), OpenMode.ForRead);
        var domain = ToolArgs.Text(a, "domain") switch { "pipe" => DomainType.Pipe, "structure" => DomainType.Structure, _ => throw new CadFault("INVALID_ARGUMENT", "Dominio inválido.") };
        if (a["familyHandle"] != null)
        {
            var fid = DocumentAccess.ObjectId(d.Database, ToolArgs.Text(a, "familyHandle")); Validation.Require(list.GetPartFamilyIdsByDomain(domain).Cast<ObjectId>().Contains(fid), "La familia no pertenece al catálogo/dominio.");
            var family = (PartFamily)tr.GetObject(fid, OpenMode.ForRead);
            return Page(Enumerable.Range(0, family.PartSizeCount).Select(i => family[i]), a, "sizes", id => new JObject { ["handle"] = Handle(id), ["name"] = ((PartSize)tr.GetObject(id, OpenMode.ForRead)).Name });
        }
        return Page(list.GetPartFamilyIdsByDomain(domain).Cast<ObjectId>(), a, "families", id => { var f = (PartFamily)tr.GetObject(id, OpenMode.ForRead); return new JObject { ["handle"] = Handle(id), ["name"] = f.Name, ["description"] = f.Description, ["sizeCount"] = f.PartSizeCount }; });
    });
    private static JToken NetworkCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var name = ToolArgs.Text(a, "name"); var parts = Style(d, tr, a, "partsListHandle", "partsList"); var id = Network.Create(Civil(d), ref name); var n = (Network)tr.GetObject(id, OpenMode.ForWrite); n.PartsListId = parts;
        if (a["surfaceHandle"] != null) n.ReferenceSurfaceId = Entity<Autodesk.Civil.DatabaseServices.Surface>(d, tr, a, "surfaceHandle").ObjectId;
        if (a["alignmentHandle"] != null) n.ReferenceAlignmentId = Entity<Alignment>(d, tr, a, "alignmentHandle").ObjectId;
        return Id(n);
    });
    private static (ObjectId family, ObjectId size) PartIds(Autodesk.AutoCAD.ApplicationServices.Document d, Transaction tr, Network n, string familyHandle, string sizeHandle, DomainType domain)
    {
        var fid = DocumentAccess.ObjectId(d.Database, familyHandle); var sid = DocumentAccess.ObjectId(d.Database, sizeHandle);
        var list = (PartsList)tr.GetObject(n.PartsListId, OpenMode.ForRead);
        Validation.Require(list.GetPartFamilyIdsByDomain(domain).Cast<ObjectId>().Contains(fid), "La familia no pertenece a la lista de piezas/dominio de la red.");
        var family = (PartFamily)tr.GetObject(fid, OpenMode.ForRead);
        Validation.Require(Enumerable.Range(0, family.PartSizeCount).Any(i => family[i] == sid), "El tamaño no pertenece a la familia."); return (fid, sid);
    }
    private static JToken NetworkParts(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var n = Entity<Network>(d, tr, a, "networkHandle", true); var rows = new JArray();
        var pipes = a["pipes"] == null ? new List<PipeSpec>() : ToolArgs.List<PipeSpec>(a, "pipes", 0, 500);
        var structures = a["structures"] == null ? new List<StructureSpec>() : ToolArgs.List<StructureSpec>(a, "structures", 0, 500);
        Validation.Require(pipes.Count + structures.Count > 0, "Indica pipes o structures.");
        foreach (var spec in pipes)
        {
            Validation.Point(spec.Start); Validation.Point(spec.End); var start = CadOperations.ToPoint(spec.Start); var end = CadOperations.ToPoint(spec.End);
            Validation.Require(new Point2d(start.X, start.Y).GetDistanceTo(new Point2d(end.X, end.Y)) > 1e-6, "Una tubería requiere longitud horizontal positiva.");
            var ids = PartIds(d, tr, n, spec.FamilyHandle, spec.SizeHandle, DomainType.Pipe); var id = ObjectId.Null;
            using var line = new LineSegment3d(start, end); n.AddLinePipe(ids.family, ids.size, line, ref id, false); rows.Add(PartInfo((Pipe)tr.GetObject(id, OpenMode.ForRead)));
        }
        foreach (var spec in structures)
        {
            Validation.Point(spec.Position); Validation.Require(Validation.Finite(spec.Rotation), "Rotación inválida."); var ids = PartIds(d, tr, n, spec.FamilyHandle, spec.SizeHandle, DomainType.Structure); var id = ObjectId.Null;
            n.AddStructure(ids.family, ids.size, CadOperations.ToPoint(spec.Position), spec.Rotation, ref id, false); rows.Add(PartInfo((Structure)tr.GetObject(id, OpenMode.ForRead)));
        }
        return new JObject { ["parts"] = rows, ["created"] = rows.Count, ["rulesApplied"] = false };
    }, true);
    private static JToken ConnectPipe(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var p = Entity<Pipe>(d, tr, a, "pipeHandle", true); var s = Entity<Structure>(d, tr, a, "structureHandle", true);
        Validation.Require(p.NetworkId == s.NetworkId, "Tubería y estructura deben pertenecer a la misma red.");
        var end = ToolArgs.Text(a, "end") switch { "start" => ConnectorPositionType.Start, "end" => ConnectorPositionType.End, _ => throw new CadFault("INVALID_ARGUMENT", "end debe ser start o end.") };
        p.ConnectToStructure(end, s.ObjectId, false); return PartInfo(p);
    });
    private static JToken UpdatePipe(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var p = Entity<Pipe>(d, tr, a, "pipeHandle", true); var start = CadOperations.ToPoint(ToolArgs.Point(a, "start")); var end = CadOperations.ToPoint(ToolArgs.Point(a, "end"));
        Validation.Require(new Point2d(start.X, start.Y).GetDistanceTo(new Point2d(end.X, end.Y)) > 1e-6, "Longitud horizontal inválida.");
        Validation.Require(p.StartStructureId.IsNull && p.EndStructureId.IsNull, "Edita la estructura para mover una tubería conectada; esta herramienta requiere extremos libres.");
        p.StartPoint = start; p.EndPoint = end; return PartInfo(p);
    }, true);
    private static JToken CorridorCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var al = Entity<Alignment>(d, tr, a, "alignmentHandle"); var profile = Entity<Profile>(d, tr, a, "profileHandle"); var assembly = Entity<CivilAssembly>(d, tr, a, "assemblyHandle");
        Validation.Require(profile.AlignmentId == al.ObjectId, "La rasante no pertenece al eje indicado.");
        var start = ToolArgs.Number(a, "startStation", Math.Max(al.StartingStation, profile.StartingStation)); var end = ToolArgs.Number(a, "endStation", Math.Min(al.EndingStation, profile.EndingStation));
        Validation.Require(start < end && start >= al.StartingStation && start >= profile.StartingStation && end <= al.EndingStation && end <= profile.EndingStation, "Rango de corredor inválido.");
        var id = Civil(d).CorridorCollection.Add(ToolArgs.Text(a, "name"), ToolArgs.Text(a, "baselineName", "EJE"), al.ObjectId, profile.ObjectId);
        var corridor = (Corridor)tr.GetObject(id, OpenMode.ForWrite); corridor.Baselines[0].BaselineRegions.Add(ToolArgs.Text(a, "regionName", "REGION_1"), assembly.ObjectId, start, end); corridor.Rebuild(); return Id(corridor);
    }, true);
    private static JToken CorridorRebuild(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) => { var c = Entity<Corridor>(d, tr, r.Arguments, "corridorHandle", true); c.Rebuild(); return Id(c); });
}
