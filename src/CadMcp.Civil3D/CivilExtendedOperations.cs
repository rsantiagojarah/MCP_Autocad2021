using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CadMcp.AutoCAD;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;

namespace CadMcp.Civil3D;

public static partial class CivilExtendedOperations
{
    public static void Register(OperationRegistry r)
    {
        r.Register("civil.styles", Styles);
        r.Register("civil.objects", Objects);
        r.Register("civil.object_info", ObjectInfo);
        r.Register("civil.cogo_points", CogoPoints);
        r.Register("civil.update_cogo_points", UpdateCogo, true);
        r.Register("civil.create_point_group", PointGroupCreate, true);
        r.Register("civil.create_tin_surface", SurfaceCreate, true);
        r.Register("civil.add_surface_points", SurfacePoints, true);
        r.Register("civil.add_surface_breaklines", Breaklines, true);
        r.Register("civil.add_surface_boundary", Boundary, true);
        r.Register("civil.rebuild_surface", RebuildSurface, true);
        r.Register("civil.surface_triangles", Triangles);
        r.Register("civil.create_volume_surface", Volume, true);
        r.Register("civil.extract_surface_contours", Contours, true);
        r.Register("civil.create_alignment", AlignmentCreate, true);
        r.Register("civil.sample_alignment", AlignmentSample);
        r.Register("civil.station_offset", StationOffset);
        r.Register("civil.create_surface_profile", SurfaceProfile, true);
        r.Register("civil.create_layout_profile", LayoutProfile, true);
        r.Register("civil.sample_profile", ProfileSample);
        r.Register("civil.create_profile_view", ProfileViewCreate, true);
        r.Register("civil.create_feature_line", FeatureCreate, true);
        r.Register("civil.set_feature_line_elevations", FeatureElevations, true);
        r.Register("civil.create_site", SiteCreate, true);
        r.Register("civil.create_sample_lines", SampleLines, true);
        r.Register("civil.parts", Parts);
        r.Register("civil.create_pipe_network", NetworkCreate, true);
        r.Register("civil.add_network_parts", NetworkParts, true);
        r.Register("civil.connect_pipe", ConnectPipe, true);
        r.Register("civil.update_pipe", UpdatePipe, true);
        r.Register("civil.create_corridor", CorridorCreate, true);
        r.Register("civil.rebuild_corridor", CorridorRebuild, true);
        r.Register("civil.add_surface_point_groups", SurfacePointGroups, true);
        r.Register("civil.add_profile_pvi", ProfilePviAdd, true);
        r.Register("civil.create_section_view", SectionViewCreate, true);
        r.Register("civil.update_structure", StructureUpdate, true);
        r.Register("civil.create_corridor_surface", CorridorSurfaceCreate, true);
        r.Register("civil.set_corridor_targets", CorridorTargets, true);
        r.Register("civil.set_corridor_frequency", CorridorFrequency, true);
        r.Register("civil.update_object_style", ObjectStyle, true);
        r.Register("civil.section_sources", SectionSourcesRead);
        r.Register("civil.set_section_sources", SectionSourcesSet, true);
        r.Register("civil.create_multiple_section_views", SectionViewsCreate, true);
        r.Register("civil.update_section_views", SectionViewsUpdate, true);
        r.Register("civil.add_solid_sections", SolidSectionsAdd, true);
        r.Register("civil.generate_cross_sections", GenerateCrossSections, true);
    }
    private static CivilDocument Civil(Document d) => CivilDocument.GetCivilDocument(d.Database);
    private static T Entity<T>(Document d, Transaction tr, JObject a, string key, bool write = false) where T : AcEntity => CadExecution.Entity<T>(d, tr, ToolArgs.Text(a, key), write);
    private static JObject Id(AcEntity e) { var row = CadExecution.Identity(e); if (e is CivilEntity c) row["name"] = c.Name; return row; }
    private static string? Handle(ObjectId id) => id.IsNull ? null : id.Handle.ToString();
    private static JToken Page<T>(IEnumerable<T> source, JObject args, string key, Func<T, JToken> describe, int max = 500)
    {
        var page = ToolArgs.Page(args, max); var rows = source.Skip(page.offset).Take(page.limit + 1).ToArray();
        return new JObject { [key] = new JArray(rows.Take(page.limit).Select(describe)), ["nextOffset"] = rows.Length > page.limit ? new JValue(page.offset + page.limit) : JValue.CreateNull() };
    }
    private static IEnumerable<ObjectId> Model(Document d, Transaction tr)
    {
        var bt = (BlockTable)tr.GetObject(d.Database.BlockTableId, OpenMode.ForRead);
        return ((BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead)).Cast<ObjectId>();
    }
    private static IEnumerable<ObjectId> StyleIds(CivilDocument c, string kind)
    {
        IEnumerable collection = kind switch
        {
            "surface" => c.Styles.SurfaceStyles, "alignment" => c.Styles.AlignmentStyles, "profile" => c.Styles.ProfileStyles,
            "profileView" => c.Styles.ProfileViewStyles, "alignmentLabelSet" => c.Styles.LabelSetStyles.AlignmentLabelSetStyles,
            "profileLabelSet" => c.Styles.LabelSetStyles.ProfileLabelSetStyles, "featureLine" => c.Styles.FeatureLineStyles,
            "partsList" => c.Styles.PartsListSet, "assembly" => c.Styles.AssemblyStyles, "corridor" => c.Styles.CorridorStyles,
            "sampleLine" => c.Styles.SampleLineStyles, "point" => c.Styles.PointStyles,
            "section" => c.Styles.SectionStyles, "sectionView" => c.Styles.SectionViewStyles,
            "sectionViewBandSet" => c.Styles.SectionViewBandSetStyles, "groupPlot" => c.Styles.GroupPlotStyles,
            _ => throw new CadFault("INVALID_ARGUMENT", "Tipo de estilo desconocido.")
        };
        return collection.Cast<ObjectId>();
    }
    private static ObjectId Style(Document d, Transaction tr, JObject a, string key, string kind)
    {
        var ids = StyleIds(Civil(d), kind).ToArray();
        var id = a[key] == null ? ids.FirstOrDefault() : DocumentAccess.ObjectId(d.Database, ToolArgs.Text(a, key));
        Validation.Require(!id.IsNull && ids.Contains(id), "Estilo inexistente o incompatible: " + key + ". Consulta civil_list_styles.");
        return id;
    }
    private static JToken Styles(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) => Page(StyleIds(Civil(d), ToolArgs.Text(r.Arguments, "kind")), r.Arguments, "styles", id =>
    {
        var style = (StyleBase)tr.GetObject(id, OpenMode.ForRead); return new JObject { ["handle"] = Handle(id), ["name"] = style.Name, ["type"] = style.GetType().Name };
    }));
    private static JToken Objects(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var c = Civil(d); var kind = ToolArgs.Text(r.Arguments, "kind");
        IEnumerable<ObjectId> ids = kind switch
        {
            "sites" => c.GetSiteIds().Cast<ObjectId>(), "corridors" => c.CorridorCollection.Cast<ObjectId>(), "assemblies" => c.AssemblyCollection.Cast<ObjectId>(),
            "networks" => c.GetPipeNetworkIds().Cast<ObjectId>(), "pointGroups" => c.PointGroups.Cast<ObjectId>(),
            "featureLines" => Model(d, tr).Where(id => id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(FeatureLine)))),
            "parcels" => c.GetSiteIds().Cast<ObjectId>().SelectMany(id => ((Site)tr.GetObject(id, OpenMode.ForRead)).GetParcelIds().Cast<ObjectId>()),
            "profiles" => c.GetAlignmentIds().Cast<ObjectId>().SelectMany(id => ((Alignment)tr.GetObject(id, OpenMode.ForRead)).GetProfileIds().Cast<ObjectId>()),
            "sampleLineGroups" => c.GetAlignmentIds().Cast<ObjectId>().SelectMany(id => ((Alignment)tr.GetObject(id, OpenMode.ForRead)).GetSampleLineGroupIds().Cast<ObjectId>()),
            "sectionViews" => c.GetAlignmentIds().Cast<ObjectId>().SelectMany(id => ((Alignment)tr.GetObject(id, OpenMode.ForRead)).GetSampleLineGroupIds().Cast<ObjectId>())
                .SelectMany(id => ((SampleLineGroup)tr.GetObject(id, OpenMode.ForRead)).GetSampleLineIds().Cast<ObjectId>())
                .SelectMany(id => ((SampleLine)tr.GetObject(id, OpenMode.ForRead)).GetSectionViewIds().Cast<ObjectId>()),
            "sections" => c.GetAlignmentIds().Cast<ObjectId>().SelectMany(id => ((Alignment)tr.GetObject(id, OpenMode.ForRead)).GetSampleLineGroupIds().Cast<ObjectId>())
                .SelectMany(id => ((SampleLineGroup)tr.GetObject(id, OpenMode.ForRead)).GetSampleLineIds().Cast<ObjectId>())
                .SelectMany(id => ((SampleLine)tr.GetObject(id, OpenMode.ForRead)).GetSectionIds().Cast<ObjectId>()),
            _ => throw new CadFault("INVALID_ARGUMENT", "Tipo de objeto desconocido.")
        };
        return Page(ids, r.Arguments, "objects", id =>
        {
            var obj = tr.GetObject(id, OpenMode.ForRead); var row = new JObject { ["handle"] = Handle(id), ["type"] = obj.GetType().Name };
            if (obj is CivilEntity e) row["name"] = e.Name;
            else if (obj is PointGroup pg) { row["name"] = pg.Name; row["pointCount"] = pg.PointsCount; }
            return row;
        });
    });
    private static JObject Scalars(object properties)
    {
        var row = new JObject();
        foreach (var p in properties.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.CanRead))
        {
            if (p.PropertyType == typeof(double) || p.PropertyType == typeof(int) || p.PropertyType == typeof(uint) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(string))
            {
                var value = p.GetValue(properties); if (value is double number && !Validation.Finite(number)) continue;
                row[char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1)] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
        }
        return row;
    }
    private static JToken ObjectInfo(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var a = r.Arguments; var id = DocumentAccess.ObjectId(d.Database, ToolArgs.Text(a, "handle")); var obj = tr.GetObject(id, OpenMode.ForRead);
        if (obj is PointGroup group) return Page(group.GetPointNumbers(), a, "pointNumbers", n => new JValue(n));
        if (obj is CogoPoint cp) return new JObject { ["handle"] = Handle(id), ["number"] = cp.PointNumber, ["position"] = CadOperations.Point(cp.Location), ["description"] = cp.RawDescription, ["name"] = cp.PointName, ["locked"] = cp.IsLocked };
        Validation.Require(obj is CivilEntity, "Usa cad_get_entity_geometry para objetos AutoCAD.");
        var e = (CivilEntity)obj; var row = Id(e); row["isReference"] = e.IsReferenceObject; row["units"] = d.Database.Insunits.ToString();
        if (e is CivilSurface s)
        {
            row["general"] = Scalars(s.GetGeneralProperties());
            if (s is TinSurface ts) { row["tin"] = Scalars(ts.GetTinProperties()); row["terrain"] = Scalars(ts.GetTerrainProperties()); }
            if (s is TinVolumeSurface vs) row["volumes"] = Scalars(vs.GetVolumeProperties());
        }
        else if (e is Alignment al)
        {
            row["length"] = al.Length; row["startingStation"] = al.StartingStation; row["endingStation"] = al.EndingStation;
            row["profiles"] = new JArray(al.GetProfileIds().Cast<ObjectId>().Select(Handle)); row["profileViews"] = new JArray(al.GetProfileViewIds().Cast<ObjectId>().Select(Handle));
            row["sampleLineGroups"] = new JArray(al.GetSampleLineGroupIds().Cast<ObjectId>().Select(Handle));
        }
        else if (e is Profile profile)
        {
            row["alignmentHandle"] = Handle(profile.AlignmentId); row["profileType"] = profile.ProfileType.ToString(); row["startingStation"] = profile.StartingStation; row["endingStation"] = profile.EndingStation;
            row["pvis"] = Page(profile.PVIs.Cast<ProfilePVI>(), a, "items", p => new JObject { ["station"] = p.Station, ["elevation"] = p.Elevation });
        }
        else if (e is FeatureLine f)
        {
            row["length2D"] = f.Length2D; row["length3D"] = f.Length3D; row["siteHandle"] = Handle(f.SiteId);
            using var points = f.GetPoints(FeatureLinePointType.AllPoints);
            row["points"] = Page(points.Cast<Point3d>(), a, "items", CadOperations.Point);
        }
        else if (e is Network n)
        {
            row["partsListHandle"] = Handle(n.PartsListId);
            var ids = n.GetPipeIds().Cast<ObjectId>().Concat(n.GetStructureIds().Cast<ObjectId>());
            row["parts"] = Page(ids, a, "items", pid => PartInfo((Part)tr.GetObject(pid, OpenMode.ForRead)));
        }
        else if (e is Part part) return PartInfo(part);
        else if (e is Corridor corridor)
        {
            row["isOutOfDate"] = corridor.IsOutOfDate;
            row["targets"] = Page(corridor.GetTargets().Cast<SubassemblyTargetInfo>().Select((t, i) => new { target = t, index = i }), a, "items", item => new JObject {
                ["index"] = item.index, ["logicalName"] = item.target.LogicalName, ["displayName"] = item.target.DisplayName,
                ["subassemblyName"] = item.target.SubassemblyName, ["targetType"] = item.target.TargetType.ToString(), ["targetHandles"] = new JArray(item.target.TargetIds.Cast<ObjectId>().Select(Handle)) });
            row["baselines"] = Page(corridor.Baselines.Cast<Baseline>(), a, "items", b => new JObject { ["name"] = b.Name, ["alignmentHandle"] = Handle(b.AlignmentId), ["profileHandle"] = Handle(b.ProfileId),
                ["regions"] = new JArray(b.BaselineRegions.Cast<BaselineRegion>().Take(100).Select(region => new JObject { ["name"] = region.Name, ["startStation"] = region.StartStation, ["endStation"] = region.EndStation, ["assemblyHandle"] = Handle(region.AssemblyId) })), ["regionsTruncated"] = b.BaselineRegions.Count > 100 });
        }
        else if (e is Site site) { row["featureLines"] = Page(site.GetFeatureLineIds().Cast<ObjectId>(), a, "items", v => new JValue(Handle(v))); }
        else if (e is SampleLineGroup slg) row["sampleLines"] = Page(slg.GetSampleLineIds().Cast<ObjectId>(), a, "items", v => Id((SampleLine)tr.GetObject(v, OpenMode.ForRead)));
        else if (e is SampleLine sl) { row["station"] = sl.Station; row["sections"] = new JArray(sl.GetSectionIds().Cast<ObjectId>().Select(Handle)); row["sectionViews"] = new JArray(sl.GetSectionViewIds().Cast<ObjectId>().Select(Handle)); row["vertices"] = new JArray(sl.Vertices.Cast<SampleLineVertex>().Select(v => CadOperations.Point(v.Location))); }
        else if (e is SectionView sectionView) return SectionViewInfo(tr, sectionView);
        else if (e is Autodesk.Civil.DatabaseServices.Section section) { row["station"] = section.Station; row["sectionPoints"] = Page(section.SectionPoints.Cast<SectionPoint>(), a, "items", point => new JObject { ["location"] = CadOperations.Point(point.Location) }); }
        else if (e is Parcel parcel) { row["area"] = parcel.Area; }
        else if (e is CivilAssembly assembly) row["location"] = CadOperations.Point(assembly.Location);
        return row;
    });
    private static JObject PartInfo(Part part)
    {
        var row = Id(part); row["networkHandle"] = Handle(part.NetworkId); row["sizeName"] = part.PartSizeName;
        if (part is Pipe p) { row["start"] = CadOperations.Point(p.StartPoint); row["end"] = CadOperations.Point(p.EndPoint); row["length2D"] = p.Length2DCenterToCenter; row["length3D"] = p.Length3DCenterToCenter; row["slope"] = p.Slope; row["innerDiameterOrWidth"] = p.InnerDiameterOrWidth; row["innerHeight"] = p.InnerHeight; row["startStructureHandle"] = Handle(p.StartStructureId); row["endStructureHandle"] = Handle(p.EndStructureId); }
        if (part is Structure s) { row["position"] = CadOperations.Point(s.Position); row["rimElevation"] = s.RimElevation; row["sumpElevation"] = s.SumpElevation; row["connectedPipeCount"] = s.ConnectedPipesCount; }
        return row;
    }
    private static JToken CogoPoints(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) => Page(Civil(d).CogoPoints.Cast<ObjectId>(), r.Arguments, "points", id =>
    {
        var p = (CogoPoint)tr.GetObject(id, OpenMode.ForRead);
        return new JObject { ["handle"] = Handle(id), ["number"] = p.PointNumber, ["position"] = CadOperations.Point(p.Location), ["name"] = p.PointName, ["description"] = p.RawDescription, ["locked"] = p.IsLocked };
    }));
    private static JToken UpdateCogo(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var specs = ToolArgs.List<CogoUpdateSpec>(r.Arguments, "points", 1, 10000);
        Validation.Require(specs.Select(s => s.Handle).Distinct(StringComparer.OrdinalIgnoreCase).Count() == specs.Count, "Handles duplicados.");
        foreach (var spec in specs)
        {
            Validation.Require(spec.Position != null || spec.Description != null || spec.Name != null, "Cada punto necesita al menos un cambio.");
            var p = CadExecution.Entity<CogoPoint>(d, tr, spec.Handle, true);
            Validation.Require(!p.IsLocked && !p.IsSurveyPoint && p.IsMovable, "Punto bloqueado, de levantamiento o no editable.");
            if (spec.Position != null) { Validation.Point(spec.Position); p.Easting = spec.Position.X; p.Northing = spec.Position.Y; p.Elevation = spec.Position.Z; }
            if (spec.Description != null) { Validation.Require(spec.Description.Length <= 1024, "Descripción demasiado larga."); p.RawDescription = spec.Description; }
            if (spec.Name != null) { Validation.Require(spec.Name.Length <= 1024, "Nombre demasiado largo."); p.PointName = spec.Name; }
        }
        return new JObject { ["updated"] = specs.Count };
    }, true);
    private static JToken PointGroupCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var numbers = ToolArgs.List<uint>(a, "pointNumbers", 1, 10000);
        Validation.Require(numbers.All(n => n > 0) && numbers.Distinct().Count() == numbers.Count, "Números de punto inválidos o duplicados.");
        var available = Civil(d).CogoPoints.Cast<ObjectId>().Select(id => ((CogoPoint)tr.GetObject(id, OpenMode.ForRead)).PointNumber).ToHashSet();
        Validation.Require(numbers.All(available.Contains), "Hay números de punto inexistentes.");
        var id = Civil(d).PointGroups.Add(ToolArgs.Text(a, "name")); var group = (PointGroup)tr.GetObject(id, OpenMode.ForWrite);
        group.SetQuery(new StandardPointGroupQuery { IncludeNumbers = string.Join(",", numbers) }); group.Update();
        return new JObject { ["handle"] = Handle(id), ["name"] = group.Name, ["pointCount"] = group.PointsCount };
    });
    private static Point3dCollection Points(JObject a, int minimum = 1)
    {
        var points = ToolArgs.List<Vec3>(a, "points", minimum, 10000); foreach (var point in points) Validation.Point(point);
        return new Point3dCollection(points.Select(CadOperations.ToPoint).ToArray());
    }
    private static JToken SurfaceCreate(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; using var points = Points(a, 3);
        var id = TinSurface.Create(d.Database, ToolArgs.Text(a, "name")); var surface = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);
        if (a["styleHandle"] != null) surface.StyleId = Style(d, tr, a, "styleHandle", "surface");
        surface.AddVertices(points); surface.Rebuild(); Validation.Require(surface.GetTinProperties().NumberOfTriangles > 0, "Los puntos no definen un terreno triangulable.");
        return Id(surface);
    }, true);
    private static JToken SurfacePoints(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var s = Entity<TinSurface>(d, tr, r.Arguments, "surfaceHandle", true); using var points = Points(r.Arguments); s.AddVertices(points); s.Rebuild(); return Id(s);
    }, true);
    private static JToken Breaklines(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var s = Entity<TinSurface>(d, tr, a, "surfaceHandle", true); var handles = ToolArgs.Handles(a, "sourceHandles", 1000);
        using var ids = new ObjectIdCollection();
        foreach (var h in handles) { var e = CadExecution.Entity<AcEntity>(d, tr, h); Validation.Require(e is Line || e is Polyline || e is Polyline3d || e is FeatureLine, "Línea de rotura incompatible: " + h); ids.Add(e.ObjectId); }
        s.BreaklinesDefinition.AddStandardBreaklines(ids, ToolArgs.Number(a, "midOrdinateDistance", 0.1, true), 0, 0, 0); s.Rebuild(); return Id(s);
    }, true);
    private static JToken Boundary(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var s = Entity<TinSurface>(d, tr, a, "surfaceHandle", true); var p = Entity<Polyline>(d, tr, a, "sourceHandle");
        Validation.Require(p.Closed, "El límite debe ser una polilínea cerrada.");
        var type = ToolArgs.Text(a, "type") switch { "outer" => SurfaceBoundaryType.Outer, "hide" => SurfaceBoundaryType.Hide, "show" => SurfaceBoundaryType.Show, _ => throw new CadFault("INVALID_ARGUMENT", "Límite inválido.") };
        using var ids = new ObjectIdCollection(new[] { p.ObjectId });
        s.BoundariesDefinition.AddBoundaries(ids, ToolArgs.Number(a, "midOrdinateDistance", 0.1, true), type, true); s.Rebuild(); return Id(s);
    }, true);
    private static JToken RebuildSurface(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) => { var s = Entity<CivilSurface>(d, tr, r.Arguments, "surfaceHandle", true); s.Rebuild(); return Id(s); });
    private static JToken Triangles(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var s = Entity<TinSurface>(d, tr, r.Arguments, "surfaceHandle");
        return Page(s.GetTriangles(false).Cast<TinSurfaceTriangle>(), r.Arguments, "triangles", t => new JObject { ["a"] = CadOperations.Point(t.Vertex1.Location), ["b"] = CadOperations.Point(t.Vertex2.Location), ["c"] = CadOperations.Point(t.Vertex3.Location) });
    });
    private static JToken Volume(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var b = Entity<TinSurface>(d, tr, a, "baseSurfaceHandle"); var c = Entity<TinSurface>(d, tr, a, "comparisonSurfaceHandle");
        Validation.Require(b.ObjectId != c.ObjectId, "Las superficies deben ser diferentes.");
        var id = TinVolumeSurface.Create(ToolArgs.Text(a, "name"), b.ObjectId, c.ObjectId); var s = (TinVolumeSurface)tr.GetObject(id, OpenMode.ForWrite); s.Rebuild();
        var row = Id(s); row["volumes"] = Scalars(s.GetVolumeProperties()); return row;
    });
    private static JToken Contours(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var s = Entity<TinSurface>(d, tr, a, "surfaceHandle");
        var interval = ToolArgs.Number(a, "interval", positive: true); var stats = s.GetGeneralProperties();
        Validation.Require((stats.MaximumElevation - stats.MinimumElevation) / interval <= 2000, "Máximo 2000 niveles de curvas por llamada.");
        var layer = CadExecution.Layer(d, tr, ToolArgs.Text(a, "layer", "C-TOPO-EXTRACT"));
        using var ids = s.ExtractContours(interval); Validation.Require(ids.Count <= 10000, "Se generaron demasiadas curvas; aumenta interval.");
        foreach (ObjectId id in ids) ((AcEntity)tr.GetObject(id, OpenMode.ForWrite)).LayerId = layer;
        return new JObject { ["handles"] = new JArray(ids.Cast<ObjectId>().Select(Handle)), ["created"] = ids.Count };
    }, true);
}
