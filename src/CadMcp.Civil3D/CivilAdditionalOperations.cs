using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace CadMcp.Civil3D;

public static partial class CivilExtendedOperations
{
    private static JToken SurfacePointGroups(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var surface = Entity<TinSurface>(d, tr, a, "surfaceHandle", true);
        foreach (var h in ToolArgs.Handles(a, "pointGroupHandles", 100))
        {
            var id = DocumentAccess.ObjectId(d.Database, h);
            Validation.Require(tr.GetObject(id, OpenMode.ForRead) is PointGroup, "Handle incompatible con PointGroup.");
            surface.PointGroupsDefinition.AddPointGroup(id);
        }
        surface.Rebuild(); return Id(surface);
    });
    private static JToken ProfilePviAdd(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var p = Entity<Profile>(d, tr, a, "profileHandle", true);
        Validation.Require(p.ProfileType == ProfileType.FG, "Solo se pueden editar PVI en perfiles de rasante FG.");
        var alignment = (Alignment)tr.GetObject(p.AlignmentId, OpenMode.ForRead); var station = ToolArgs.Number(a, "station"); var elevation = ToolArgs.Number(a, "elevation");
        Validation.Require(station >= alignment.StartingStation && station <= alignment.EndingStation, "Estación fuera del eje.");
        var type = ToolArgs.Text(a, "type");
        if (type == "tangent") p.PVIs.AddPVI(station, elevation);
        else if (type == "symmetricParabola") p.PVIs.AddPVISymParabola(station, elevation, ToolArgs.Number(a, "curveLength", positive: true));
        else if (type == "asymmetricParabola") p.PVIs.AddPVIAsymParabola(station, elevation, ToolArgs.Number(a, "beforeLength", positive: true), ToolArgs.Number(a, "afterLength", positive: true));
        else if (type == "circular") p.PVIs.AddPVIArc(station, elevation, ToolArgs.Number(a, "radius", positive: true));
        else throw new CadFault("INVALID_ARGUMENT", "Tipo de curva vertical desconocido.");
        return Id(p);
    }, true);
    private static JToken SectionViewCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var line = Entity<SampleLine>(d, tr, a, "sampleLineHandle");
        var id = SectionView.Create(ToolArgs.Text(a, "name"), line.ObjectId, CadOperations.ToPoint(ToolArgs.Point(a, "position")));
        return Id((SectionView)tr.GetObject(id, OpenMode.ForRead));
    }, true);
    private static JToken StructureUpdate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var s = Entity<Structure>(d, tr, a, "structureHandle", true);
        Validation.Require(a["rimElevation"] != null || a["sumpElevation"] != null, "Indica cota de tapa o fondo.");
        var rim = ToolArgs.Number(a, "rimElevation", s.RimElevation); var sump = ToolArgs.Number(a, "sumpElevation", s.SumpElevation);
        Validation.Require(sump <= rim, "El fondo no puede estar por encima de la tapa.");
        if (a["rimElevation"] != null) { s.AutomaticRimSurfaceAdjustment = false; s.RimElevation = rim; }
        if (a["sumpElevation"] != null) { s.ControlSumpBy = StructureControlSumpType.ByElevation; s.SumpElevation = sump; }
        return PartInfo(s);
    }, true);
    private static JToken CorridorSurfaceCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var corridor = Entity<Corridor>(d, tr, a, "corridorHandle", true); var codes = ToolArgs.List<string>(a, "linkCodes", 1, 100);
        var available = corridor.GetLinkCodes(); Validation.Require(codes.All(code => !string.IsNullOrWhiteSpace(code) && available.Contains(code)), "El corredor no contiene alguno de los códigos de enlace indicados.");
        var surface = corridor.CorridorSurfaces.Add(ToolArgs.Text(a, "name")); foreach (var code in codes) surface.AddLinkCode(code, true);
        surface.IsBuild = true;
        if (ToolArgs.Flag(a, "addExtentsBoundary", true)) surface.Boundaries.AddCorridorExtentsBoundary("EXTENTS");
        corridor.Rebuild(); return new JObject { ["surfaceHandle"] = Handle(surface.SurfaceId), ["name"] = surface.Name };
    });
    private static JToken CorridorTargets(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var corridor = Entity<Corridor>(d, tr, a, "corridorHandle", true); var targets = corridor.GetTargets(); var specs = ToolArgs.List<CorridorTargetSpec>(a, "targets", 1, 1000);
        Validation.Require(specs.Select(s => s.Index).Distinct().Count() == specs.Count, "Índices de objetivo duplicados.");
        var allocations = new List<ObjectIdCollection>();
        try
        {
            foreach (var spec in specs)
            {
                Validation.Require(spec.Index >= 0 && spec.Index < targets.Count && spec.TargetHandles != null && spec.TargetHandles.Length <= 1000, "Índice de objetivo inválido; consulta civil_get_object_info.");
                var requested = spec.TargetHandles!;
                Validation.Require(requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() == requested.Length, "Objetivos duplicados.");
                var ids = new ObjectIdCollection(); allocations.Add(ids);
                foreach (var h in requested) { var e = CadExecution.Entity<AcEntity>(d, tr, h); ids.Add(e.ObjectId); }
                targets[spec.Index].TargetIds = ids;
            }
            corridor.SetTargets(targets); corridor.Rebuild(); return Id(corridor);
        }
        finally { foreach (var ids in allocations) ids.Dispose(); }
    });
    private static JToken CorridorFrequency(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var corridor = Entity<Corridor>(d, tr, a, "corridorHandle", true); var baselineName = ToolArgs.Text(a, "baselineName"); var regionName = ToolArgs.Text(a, "regionName");
        var baseline = corridor.Baselines.Cast<Baseline>().SingleOrDefault(b => b.Name == baselineName) ?? throw new CadFault("OBJECT_NOT_FOUND", "Línea base inexistente.");
        var region = baseline.BaselineRegions.Cast<BaselineRegion>().SingleOrDefault(b => b.Name == regionName) ?? throw new CadFault("OBJECT_NOT_FOUND", "Región inexistente.");
        var interval = ToolArgs.Number(a, "interval", positive: true);
        Validation.Require((region.EndStation - region.StartStation) / interval <= 20000, "Máximo 20000 estaciones por región.");
        var settings = region.AppliedAssemblySetting; settings.FrequencyAlongTangents = interval; settings.FrequencyAlongCurves = interval; settings.FrequencyAlongSpirals = interval; settings.FrequencyAlongProfileCurves = interval;
        corridor.Rebuild(); return Id(corridor);
    }, true);
    private static JToken ObjectStyle(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var raw = Entity<AcEntity>(d, tr, a, "handle", true);
        if (raw is CogoPoint point) { point.StyleId = Style(d, tr, a, "styleHandle", "point"); return CadExecution.Identity(point); }
        Validation.Require(raw is CivilEntity, "Se requiere un objeto Civil."); var entity = (CivilEntity)raw;
        var kind = entity is TinSurface || entity is TinVolumeSurface ? "surface" : entity is Alignment ? "alignment" : entity is Profile ? "profile" : entity is ProfileView ? "profileView" : entity is FeatureLine ? "featureLine" : entity is Corridor ? "corridor" : entity is SampleLine ? "sampleLine" : entity is SectionView ? "sectionView" : entity is Autodesk.Civil.DatabaseServices.Section ? "section" : throw new CadFault("INVALID_OBJECT", "No se admite el estilo de este tipo de objeto.");
        entity.StyleId = Style(d, tr, a, "styleHandle", kind); return Id(entity);
    });
}
