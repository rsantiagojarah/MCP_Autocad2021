using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

public sealed class CadOperations
{
    private readonly OperationRegistry registry;
    public CadOperations(OperationRegistry registry) { this.registry = registry; }
    public void Register()
    {
        registry.Register("session.info", Session);
        registry.Register("cad.documents", Documents);
        registry.Register("cad.document_info", Info);
        registry.Register("cad.layers", Layers);
        registry.Register("cad.query_entities", Query);
        registry.Register("cad.create_entities", Create, mutates: true);
        registry.Register("cad.upsert_layers", UpsertLayers, mutates: true);
        registry.Register("cad.update_entities", Update, mutates: true);
        CadExtendedOperations.Register(registry);
    }
    private JToken Session(BridgeRequest request) => JObject.FromObject(new
    {
        sessionId = PipeNames.Prefix + Process.GetCurrentProcess().Id,
        bridgeInstanceId = instanceId,
        processId = Process.GetCurrentProcess().Id,
        bridgeVersion = typeof(CadOperations).Assembly.GetName().Version!.ToString(3),
        product = Convert.ToString(AcApp.GetSystemVariable("PRODUCT")),
        version = Convert.ToString(AcApp.GetSystemVariable("ACADVER")),
        civil3d = registry.Methods.Contains("civil.surfaces"),
        methods = registry.Methods.ToArray(),
        limits = new { frameBytes = Wire.MaxFrameBytes, batchEntities = 10000, queryPage = 500, journalRequests = 10000 }
    });
    private readonly string instanceId = Guid.NewGuid().ToString("D");

    private static JToken Documents(BridgeRequest request)
    {
        var list = new JArray();
        foreach (Document doc in AcApp.DocumentManager)
            list.Add(JObject.FromObject(new { documentId = DocumentAccess.Id(doc), name = doc.Name,
                active = doc == AcApp.DocumentManager.MdiActiveDocument }));
        return new JObject { ["documents"] = list };
    }
    private static JToken Info(BridgeRequest request)
    {
        var doc = DocumentAccess.Active(request);
        var matrix = doc.Editor.CurrentUserCoordinateSystem;
        return JObject.FromObject(new { documentId = DocumentAccess.Id(doc), name = doc.Name,
            units = doc.Database.Insunits.ToString(), coordinateSpace = "WCS", ucsToWcs = matrix.ToArray(),
            tileMode = doc.Database.TileMode, modified = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
            fingerprint = doc.Database.FingerprintGuid.ToString() });
    }
    private static JToken Layers(BridgeRequest request)
    {
        var doc = DocumentAccess.Active(request);
        var offset = (int?)request.Arguments["offset"] ?? 0;
        var limit = (int?)request.Arguments["limit"] ?? 100;
        Validation.Require(offset >= 0 && offset <= 1000000 && limit >= 1 && limit <= 500, "Paginación inválida.");
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var table = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
            var ids = table.Cast<ObjectId>().ToArray();
            var layers = ids.Skip(offset).Take(limit).Select(id =>
            {
                var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                return new { name = layer.Name, colorIndex = layer.Color.ColorIndex, locked = layer.IsLocked, frozen = layer.IsFrozen, off = layer.IsOff,
                    lineweight = (int)layer.LineWeight, linetype = ((LinetypeTableRecord)tr.GetObject(layer.LinetypeObjectId,OpenMode.ForRead)).Name };
            }).ToArray();
            return JObject.FromObject(new { layers, nextOffset = offset + layers.Length < ids.Length ? (int?)(offset + layers.Length) : null });
        }
    }
    private static JToken Query(BridgeRequest request)
    {
        var args = Validation.Arguments<QueryEntitiesArgs>(request.Arguments);
        Validation.Query(args);
        var doc = DocumentAccess.Active(request);
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var results = new JArray(); var index = 0; var scanned = 0; var more = false; var responseBytes = 0;
            foreach (ObjectId id in model)
            {
                if (index < args.Offset) { index++; continue; }
                if (results.Count >= args.Limit || scanned >= 20000) { more = true; break; }
                index++; scanned++;
                if (id.IsErased) continue;
                if (args.DxfType != null && !string.Equals(id.ObjectClass.DxfName, args.DxfType, StringComparison.OrdinalIgnoreCase)) continue;
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || (args.Layer != null && !string.Equals(entity.Layer, args.Layer, StringComparison.OrdinalIgnoreCase))) continue;
                if (!Intersects(entity, args.Min, args.Max)) continue;
                var row = Describe(entity, tr);
                var rowBytes = Encoding.UTF8.GetByteCount(row.ToString(Newtonsoft.Json.Formatting.None));
                if (results.Count > 0 && responseBytes + rowBytes > 3 * 1024 * 1024)
                {
                    // This candidate must be visited again on the next page.
                    index--; scanned--; more = true; break;
                }
                results.Add(row); responseBytes += rowBytes;
            }
            return new JObject { ["entities"] = results, ["scanned"] = scanned,
                ["nextOffset"] = more ? (JToken)new JValue(index) : JValue.CreateNull(),
                ["space"] = "ModelSpace", ["units"] = doc.Database.Insunits.ToString(),
                ["paginationNote"] = "Los offsets son válidos mientras no cambie el dibujo." };
        }
    }
    private static bool Intersects(Entity entity, Vec3? min, Vec3? max)
    {
        if (min == null || max == null) return true;
        try
        {
            var e = entity.GeometricExtents;
            return e.MaxPoint.X >= min.X && e.MinPoint.X <= max.X && e.MaxPoint.Y >= min.Y && e.MinPoint.Y <= max.Y && e.MaxPoint.Z >= min.Z && e.MinPoint.Z <= max.Z;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }
    }
    public static JObject Describe(Entity entity, Transaction tr)
    {
        var result = new JObject { ["handle"] = entity.Handle.ToString(), ["dxfType"] = entity.GetRXClass().DxfName,
            ["layer"] = entity.Layer, ["colorIndex"] = entity.ColorIndex };
        result["lineweight"] = (int)entity.LineWeight; result["linetype"] = entity.Linetype; result["linetypeScale"] = entity.LinetypeScale;
        if (entity is Line line) { result["start"] = Point(line.StartPoint); result["end"] = Point(line.EndPoint); result["length"] = line.Length; }
        else if (entity is Polyline poly)
        {
            result["length"] = poly.Length; result["closed"] = poly.Closed; result["vertexCount"] = poly.NumberOfVertices;
            if (poly.Closed) result["area"] = poly.Area;
        }
        else if (entity is Circle circle) { result["center"] = Point(circle.Center); result["radius"] = circle.Radius; result["area"] = circle.Area; result["length"] = circle.Circumference; }
        else if (entity is Arc arc) result["length"] = arc.Length;
        else if (entity is DBPoint point) result["position"] = Point(point.Position);
        else if (entity is Solid3d solid) result["volume"] = solid.MassProperties.Volume;
        else if (entity is DBText text) { result["text"] = Clip(text.TextString, 4096); result["textTruncated"] = text.TextString.Length > 4096; result["styleHandle"] = text.TextStyleId.Handle.ToString(); result["height"] = text.Height; result["rotation"] = text.Rotation; result["alignment"] = text.Justify.ToString(); }
        else if (entity is MText mtext) { result["text"] = Clip(mtext.Contents, 4096); result["textTruncated"] = mtext.Contents.Length > 4096; result["styleHandle"] = mtext.TextStyleId.Handle.ToString(); result["height"] = mtext.TextHeight; result["rotation"] = mtext.Rotation; result["alignment"] = mtext.Attachment.ToString(); }
        else if (entity is Hatch hatch) { result["pattern"] = hatch.PatternName; result["scale"] = hatch.PatternScale; result["angle"] = hatch.PatternAngle; result["associative"] = hatch.Associative; result["loopCount"] = hatch.NumberOfLoops; }
        else if (entity is BlockReference block)
        {
            var attributes = new JArray();
            foreach (ObjectId id in block.AttributeCollection)
            {
                if (attributes.Count >= 100) break;
                var attribute = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute != null) attributes.Add(new JObject { ["tag"] = Clip(attribute.Tag, 256), ["text"] = Clip(attribute.TextString, 4096), ["textTruncated"] = attribute.TextString.Length > 4096 });
            }
            result["attributes"] = attributes; result["position"] = Point(block.Position);
            result["attributesTruncated"] = block.AttributeCollection.Count > 100;
        }
        return result;
    }
    private static string Clip(string text, int max) => text.Length <= max ? text : text.Substring(0, max);
    public static JObject Point(Point3d point) => new JObject { ["x"] = point.X, ["y"] = point.Y, ["z"] = point.Z };

    private static JToken Create(BridgeRequest request)
    {
        var args = Validation.Arguments<CreateEntitiesArgs>(request.Arguments); Validation.Geometry(args);
        var doc = DocumentAccess.Active(request); DocumentAccess.Units(doc.Database, args.Units, args.CoordinateSpace);
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var layerIds = args.Entities.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(name => name, name => DocumentAccess.Layer(tr, doc.Database, name).ObjectId, StringComparer.OrdinalIgnoreCase);
            var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var handles = new List<string>();
            foreach (var spec in args.Entities)
            {
                Entity entity;
                var p = spec.Points;
                if (spec.Type == "point") entity = new DBPoint(ToPoint(p[0]));
                else if (spec.Type == "line") entity = new Line(ToPoint(p[0]), ToPoint(p[1]));
                else
                {
                    var poly = new Polyline(p.Count) { Elevation = p[0].Z, Closed = spec.Closed };
                    for (var i = 0; i < p.Count; i++) poly.AddVertexAt(i, new Point2d(p[i].X, p[i].Y), 0, 0, 0);
                    entity = poly;
                }
                using (entity)
                {
                    entity.SetDatabaseDefaults(doc.Database); entity.LayerId = layerIds[spec.Layer];
                    model.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); handles.Add(entity.Handle.ToString());
                }
            }
            tr.Commit();
            return JObject.FromObject(new { created = handles.Count, handles, coordinateSpace = "WCS", units = args.Units });
        }
    }
    public static Point3d ToPoint(Vec3 point) => new Point3d(point.X, point.Y, point.Z);

    private static JToken UpsertLayers(BridgeRequest request)
    {
        var specs = request.Arguments["layers"]?.ToObject<List<LayerSpec>>() ?? throw new CadFault("INVALID_ARGUMENT", "Falta layers.");
        Validation.Require(specs.Count > 0 && specs.Count <= 100, "El lote admite entre 1 y 100 capas.");
        foreach (var spec in specs)
        {
            Validation.Require(spec != null && !string.IsNullOrWhiteSpace(spec.Name) && spec.ColorIndex >= 1 && spec.ColorIndex <= 255, "Nombre o color de capa inválido.");
            SymbolUtilityServices.ValidateSymbolName(spec!.Name, false);
            if (spec.Lineweight.HasValue) LineGraphicsRules.Weight(spec.Lineweight.Value,true);
            if (spec.Linetype != null) LineGraphicsRules.Name(spec.Linetype);
        }
        var doc = DocumentAccess.Active(request);
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var table = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
            foreach (var spec in specs)
                if (spec.Linetype != null) CadLineGraphicsOperations.Linetype(doc,tr,spec.Linetype,true);
            foreach (var spec in specs)
            {
                LayerTableRecord layer;
                if (table.Has(spec.Name)) layer = (LayerTableRecord)tr.GetObject(table[spec.Name], OpenMode.ForWrite);
                else
                {
                    if (!table.IsWriteEnabled) table.UpgradeOpen();
                    layer = new LayerTableRecord { Name = spec.Name }; table.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
                }
                if (layer.IsDependent) throw new CadFault("DEPENDENT_LAYER", "No se modifica una capa de referencia externa.");
                if ((spec.Lineweight.HasValue || spec.Linetype != null) && layer.IsLocked) throw new CadFault("LAYER_LOCKED", "La capa está bloqueada: "+spec.Name);
                layer.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)spec.ColorIndex);
                if (spec.Lineweight.HasValue) layer.LineWeight = (LineWeight)spec.Lineweight.Value;
                if (spec.Linetype != null) layer.LinetypeObjectId = CadLineGraphicsOperations.Linetype(doc,tr,spec.Linetype,true);
            }
            tr.Commit(); return JObject.FromObject(new { updated = specs.Count });
        }
    }
    private static JToken Update(BridgeRequest request)
    {
        var specs = request.Arguments["entities"]?.ToObject<List<UpdateEntitySpec>>() ?? throw new CadFault("INVALID_ARGUMENT", "Falta entities.");
        Validation.Require(specs.Count > 0 && specs.Count <= 10000, "El lote admite entre 1 y 10000 entidades.");
        foreach (var spec in specs)
        {
            Validation.Require(spec != null && (spec.Layer != null || spec.ColorIndex.HasValue || spec.Lineweight.HasValue || spec.Linetype != null || spec.LinetypeScale.HasValue)
                && (!spec.ColorIndex.HasValue || (spec.ColorIndex >= 0 && spec.ColorIndex <= 256)), "Especifica una propiedad válida: capa, color ACI o propiedades de línea.");
            if (spec!.Lineweight.HasValue) LineGraphicsRules.Weight(spec.Lineweight.Value);
            if (spec.Linetype != null) LineGraphicsRules.Name(spec.Linetype);
            if (spec.LinetypeScale.HasValue) LineGraphicsRules.Scale(spec.LinetypeScale.Value);
        }
        var doc = DocumentAccess.Active(request);
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (var spec in specs)
            {
                var id = DocumentAccess.ObjectId(doc.Database, spec.Handle);
                var entity = tr.GetObject(id, OpenMode.ForWrite) as Entity ?? throw new CadFault("INVALID_OBJECT", "El handle no corresponde a una entidad.");
                DocumentAccess.Layer(tr, doc.Database, entity.Layer);
                if (spec.Lineweight.HasValue || spec.Linetype != null || spec.LinetypeScale.HasValue)
                    CadLineGraphicsOperations.ValidateEntity(doc,tr,entity);
                if (spec.Layer != null) entity.LayerId = DocumentAccess.Layer(tr, doc.Database, spec.Layer).ObjectId;
                if (spec.ColorIndex.HasValue) entity.ColorIndex = spec.ColorIndex.Value;
                if (spec.Lineweight.HasValue) entity.LineWeight = (LineWeight)spec.Lineweight.Value;
                if (spec.Linetype != null) entity.LinetypeId = CadLineGraphicsOperations.Linetype(doc,tr,spec.Linetype);
                if (spec.LinetypeScale.HasValue) entity.LinetypeScale = spec.LinetypeScale.Value;
            }
            tr.Commit(); return JObject.FromObject(new { updated = specs.Count });
        }
    }
}
