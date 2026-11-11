using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;

namespace CadMcp.AutoCAD;

public static class CadHatchOperations
{
    private const string BoundaryRecord = "CADMCP_HATCH_BOUNDARIES_V1";
    public static void Register(OperationRegistry r)
    {
        r.Register("cad.hatch_patterns", Patterns);
        r.Register("cad.create_hatch", Create, true);
        r.Register("cad.hatch_info", Info);
        r.Register("cad.update_hatch", Update, true);
    }
    private static JToken Patterns(BridgeRequest r) => CadExecution.Run(r, false, (d,tr) =>
    {
        var names = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["SOLID"] = "predefined" };
        foreach (var dir in CadTextOperations.SupportDirectories(d.Database))
        foreach (var file in Directory.EnumerateFiles(dir,"*.pat"))
        {
            if (new FileInfo(file).Length > 4 * 1024 * 1024) continue;
            var type = Path.GetFileName(file).Equals("acad.pat",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).Equals("acadiso.pat",StringComparison.OrdinalIgnoreCase) ? "predefined" : "custom";
            foreach (var line in File.ReadLines(file))
            {
                if (!line.StartsWith("*",StringComparison.Ordinal)) continue;
                var name = line.Substring(1).Split(',')[0].Trim();
                if (name.Length > 0 && name.Length <= 128 && (!names.ContainsKey(name) || type == "predefined")) names[name] = type;
            }
        }
        var all = names.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToArray(); var page = ToolArgs.Page(r.Arguments);
        return new JObject { ["patterns"] = new JArray(all.Skip(page.offset).Take(page.limit).Select(p => new JObject { ["name"] = p.Key, ["patternType"] = p.Value })),
            ["nextOffset"] = page.offset + page.limit < all.Length ? new JValue(page.offset + page.limit) : JValue.CreateNull() };
    });
    private static string[] Islands(JObject a) => a["islandHandles"] is JArray array && array.Count == 0 ? Array.Empty<string>()
        : a["islandHandles"] == null ? Array.Empty<string>() : ToolArgs.Handles(a,"islandHandles",50);

    private sealed class Boundaries : IDisposable
    {
        public Curve[] Curves = Array.Empty<Curve>();
        public int OuterCount;
        public Plane Plane = null!;
        public void Dispose() { Plane?.Dispose(); }
    }
    private static Region RegionOf(Curve curve)
    {
        using var copy = (Curve)curve.Clone(); using var input = new DBObjectCollection(); input.Add(copy);
        var regions = Region.CreateFromCurves(input);
        try
        {
            Validation.Require(regions.Count == 1 && regions[0] is Region region && region.Area > 1e-10,
                "El contorno debe definir una región cerrada simple de área positiva: " + curve.Handle);
            return (Region)((Region)regions[0]).Clone();
        }
        finally { foreach (DBObject obj in regions) obj.Dispose(); regions.Dispose(); }
    }
    private static double IntersectionArea(Region a, Region b)
    {
        using var x = (Region)a.Clone(); using var y = (Region)b.Clone();
        x.BooleanOperation(BooleanOperationType.BoolIntersect,y);
        return x.IsNull ? 0 : x.Area;
    }
    private static bool Crosses(Curve a, Curve b)
    {
        using var points = new Point3dCollection(); a.IntersectWith(b,Intersect.OnBothOperands,points,IntPtr.Zero,IntPtr.Zero); return points.Count > 0;
    }
    private static Boundaries ValidateBoundaries(Document d, Transaction tr, string[] outers, string[] islands)
    {
        var handles = outers.Concat(islands).ToArray();
        Validation.Require(outers.Length > 0 && handles.Length <= 100 && handles.Distinct(StringComparer.OrdinalIgnoreCase).Count() == handles.Length, "Contornos vacíos, repetidos o más de 100.");
        var bt = (BlockTable)tr.GetObject(d.Database.BlockTableId,OpenMode.ForRead); var model = bt[BlockTableRecord.ModelSpace];
        var curves = handles.Select(h=>CadExecution.Entity<Curve>(d,tr,h)).ToArray();
        var result = new Boundaries { Curves = curves, OuterCount = outers.Length }; var regions = new List<Region>();
        try
        {
            foreach (var curve in curves)
            {
                Validation.Require(curve.OwnerId == model, "Contornos directos en ModelSpace; no se admiten objetos anidados.");
                Validation.Require(curve is Polyline || curve is Circle || curve is Ellipse || curve is Spline,
                    "Contorno compatible: Polyline, Circle, Ellipse o Spline cerrada.");
                Validation.Require(curve.Closed && curve.IsPlanar, "Contorno abierto o no plano: " + curve.Handle);
                if (curve is Polyline p) Validation.Require(p.NumberOfVertices <= 10000, "Máximo 10000 vértices por contorno.");
                using var plane = curve.GetPlane();
                if (result.Plane == null) result.Plane = new Plane(plane.PointOnPlane,plane.Normal);
                else Validation.Require(Math.Abs(Math.Abs(plane.Normal.DotProduct(result.Plane.Normal))-1) < 1e-8
                    && Math.Abs((plane.PointOnPlane-result.Plane.PointOnPlane).DotProduct(result.Plane.Normal)) < 1e-7,
                    "Los contornos no son coplanares.");
                regions.Add(RegionOf(curve));
            }
            for (var i=0;i<curves.Length;i++)
            for (var j=i+1;j<curves.Length;j++)
            {
                Validation.Require(!Crosses(curves[i],curves[j]), "Los contornos se cruzan o se tocan: " + handles[i] + ", " + handles[j]);
                if ((i < outers.Length) == (j < outers.Length))
                    Validation.Require(IntersectionArea(regions[i],regions[j]) <= 1e-8, "Exteriores e islas deben ser disjuntos, sin anidamiento entre contornos del mismo tipo.");
            }
            for (var i=outers.Length;i<curves.Length;i++)
            {
                var area = regions[i].Area; var count = 0;
                for (var j=0;j<outers.Length;j++)
                    if (Math.Abs(IntersectionArea(regions[i],regions[j])-area) <= Math.Max(1e-8,area*1e-8)) count++;
                Validation.Require(count == 1, "Cada isla debe estar completamente dentro de un único contorno exterior.");
            }
            return result;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) { result.Dispose(); throw new CadFault("INVALID_BOUNDARY", "Geometría de contorno no válida: " + ex.ErrorStatus); }
        catch { result.Dispose(); throw; }
        finally { foreach (var region in regions) region.Dispose(); }
    }
    private static HatchPatternType PatternType(JObject a, HatchPatternType fallback)
    {
        if (a["patternType"] == null) return fallback;
        var type = ToolArgs.Text(a,"patternType");
        return type == "predefined" ? HatchPatternType.PreDefined : type == "custom" ? HatchPatternType.CustomDefined
            : throw new CadFault("INVALID_ARGUMENT","patternType debe ser predefined o custom.");
    }
    private static void SetPattern(Hatch hatch, string pattern, HatchPatternType type, double scale, double angle)
    {
        Validation.Require(pattern.Length <= 128 && pattern.All(c=>char.IsLetterOrDigit(c) || c=='_' || c=='-' || c=='.'), "Nombre de patrón inválido.");
        Validation.Require(Validation.Finite(scale) && scale > 0 && Validation.Finite(angle), "Escala positiva y ángulo finito requeridos.");
        try
        {
            hatch.SetHatchPattern(type,pattern);
            hatch.PatternScale = scale; hatch.PatternAngle = angle;
            // Autodesk requires reloading the definition after changing pattern parameters.
            hatch.SetHatchPattern(type,pattern);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) { throw new CadFault("INVALID_HATCH_PATTERN", "Patrón no disponible o parámetros no válidos: " + pattern + " (" + ex.ErrorStatus + "). Consulta cad_list_hatch_patterns."); }
    }
    private static void Remember(Transaction tr, Hatch hatch, Boundaries bounds)
    {
        if (hatch.ExtensionDictionary.IsNull) hatch.CreateExtensionDictionary();
        var dict = (DBDictionary)tr.GetObject(hatch.ExtensionDictionary,OpenMode.ForWrite);
        Xrecord record;
        if (dict.Contains(BoundaryRecord)) record = (Xrecord)tr.GetObject(dict.GetAt(BoundaryRecord),OpenMode.ForWrite);
        else { record = new Xrecord(); dict.SetAt(BoundaryRecord,record); tr.AddNewlyCreatedDBObject(record,true); }
        var values = new List<TypedValue> { new TypedValue((int)DxfCode.Int32,1), new TypedValue((int)DxfCode.Int32,bounds.OuterCount) };
        // Soft pointers survive save/reopen and remap when cloning the hatch with its boundaries.
        values.AddRange(bounds.Curves.Select(c=>new TypedValue((int)DxfCode.SoftPointerId,c.ObjectId)));
        using var data = new ResultBuffer(values.ToArray()); record.Data = data;
    }
    private static (string[] outer, string[] islands)? Recall(Transaction tr, Hatch hatch)
    {
        if (hatch.ExtensionDictionary.IsNull) return null;
        var dict = (DBDictionary)tr.GetObject(hatch.ExtensionDictionary,OpenMode.ForRead); if (!dict.Contains(BoundaryRecord)) return null;
        var record = (Xrecord)tr.GetObject(dict.GetAt(BoundaryRecord),OpenMode.ForRead); using var data = record.Data;
        var values = data.AsArray();
        Validation.Require(values.Length >= 3 && values[0].Value is int version && version == 1 && values[1].Value is int, "Metadatos de contorno incompatibles.");
        var count = (int)values[1].Value; var ids = values.Skip(2).Select(v=>v.Value is ObjectId id ? id : ObjectId.Null).ToArray();
        Validation.Require(count > 0 && count <= ids.Length && ids.All(id=>!id.IsNull && !id.IsErased), "Contornos fuente ausentes. Proporciona outerHandles/islandHandles actuales.");
        return (ids.Take(count).Select(id=>id.Handle.ToString()).ToArray(),ids.Skip(count).Select(id=>id.Handle.ToString()).ToArray());
    }
    private static void SetLoops(Transaction tr, Hatch hatch, Boundaries bounds, bool associative)
    {
        if (hatch.NumberOfLoops > 0)
        {
            hatch.Associative = false; hatch.RemoveAssociatedObjectIds();
            for (var i=hatch.NumberOfLoops-1;i>=0;i--) hatch.RemoveLoopAt(i);
        }
        hatch.Normal = bounds.Plane.Normal;
        hatch.Elevation = (bounds.Plane.PointOnPlane-Point3d.Origin).DotProduct(bounds.Plane.Normal);
        hatch.Associative = associative; hatch.HatchStyle = HatchStyle.Normal;
        for (var i=0;i<bounds.Curves.Length;i++)
        {
            using var ids = new ObjectIdCollection(new[] { bounds.Curves[i].ObjectId });
            hatch.AppendLoop(i < bounds.OuterCount ? HatchLoopTypes.Outermost : HatchLoopTypes.Default,ids);
        }
        Remember(tr,hatch,bounds);
    }
    public static JObject Describe(Transaction tr, Hatch h)
    {
        var row = CadExecution.Identity(h); row["dxfType"] = "HATCH"; row["pattern"] = h.PatternName;
        row["patternType"] = h.PatternType == HatchPatternType.PreDefined ? "predefined" : h.PatternType == HatchPatternType.CustomDefined ? "custom" : "userDefined";
        row["scale"] = h.PatternScale; row["angle"] = h.PatternAngle; row["associative"] = h.Associative; row["loopCount"] = h.NumberOfLoops;
        row["colorIndex"] = h.ColorIndex; row["elevation"] = h.Elevation; row["normal"] = CadOperations.Point(Point3d.Origin+h.Normal);
        try { row["area"] = h.Area; } catch (Autodesk.AutoCAD.Runtime.Exception) { row["areaUnavailable"] = true; }
        var loops = new JArray();
        for (var i=0;i<h.NumberOfLoops;i++)
        {
            using var ids = h.Associative ? h.GetAssociatedObjectIdsAt(i) : new ObjectIdCollection();
            loops.Add(new JObject { ["index"] = i, ["loopType"] = h.LoopTypeAt(i).ToString(), ["associatedHandles"] = new JArray(ids.Cast<ObjectId>().Where(id=>!id.IsNull && !id.IsErased).Select(id=>id.Handle.ToString())) });
        }
        row["loops"] = loops; return row;
    }
    private static JToken Info(BridgeRequest r) => CadExecution.Run(r,false,(d,tr)=>Describe(tr,CadExecution.Entity<Hatch>(d,tr,ToolArgs.Text(r.Arguments,"handle"))));
    private static void Evaluate(Hatch h)
    {
        try { h.EvaluateHatch(true); }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) { throw new CadFault("HATCH_EVALUATION_FAILED", "AutoCAD no pudo evaluar el sombreado: " + ex.ErrorStatus + ". Se revierte la operación."); }
    }
    private static void Color(Hatch h, JObject a)
    {
        if (a["colorIndex"] == null) return;
        Validation.Require(a["colorIndex"]!.Type == JTokenType.Integer, "colorIndex debe ser entero ACI 0..256.");
        var color = a["colorIndex"]!.Value<int>(); Validation.Require(color >= 0 && color <= 256,"colorIndex fuera de 0..256."); h.ColorIndex = color;
    }
    private static JToken Create(BridgeRequest r) => CadExecution.Run(r,true,(d,tr)=>
    {
        var a = r.Arguments; using var bounds = ValidateBoundaries(d,tr,ToolArgs.Handles(a,"outerHandles",50),Islands(a));
        var pattern = ToolArgs.Text(a,"pattern"); var type = PatternType(a,HatchPatternType.PreDefined);
        var scale = ToolArgs.Number(a,"scale",1,true); var angle = ToolArgs.Number(a,"angle",0); var associative = ToolArgs.Flag(a,"associative",true);
        // Validate the pattern against Autodesk's native pattern loader before appending an entity.
        using (var probe = new Hatch()) { probe.SetDatabaseDefaults(d.Database); SetPattern(probe,pattern,type,scale,angle); Color(probe,a); }
        var layer = CadExecution.Layer(d,tr,ToolArgs.Text(a,"layer")); using var hatch = new Hatch(); CadExecution.Append(d,tr,hatch,layer);
        SetPattern(hatch,pattern,type,scale,angle); Color(hatch,a); SetLoops(tr,hatch,bounds,associative); Evaluate(hatch);
        var row = Describe(tr,hatch); row["sourcePreserved"] = true; return row;
    },true);
    private static JToken Update(BridgeRequest r) => CadExecution.Run(r,true,(d,tr)=>
    {
        var a = r.Arguments;
        Validation.Require(new[] { "pattern","patternType","scale","angle","associative","layer","colorIndex","outerHandles" }.Any(k=>a[k]!=null),"Indica una propiedad del sombreado.");
        Validation.Require(a["islandHandles"] == null || a["outerHandles"] != null,"Para modificar islas proporciona también outerHandles.");
        var h = CadExecution.Entity<Hatch>(d,tr,ToolArgs.Text(a,"handle"));
        var pattern = ToolArgs.Text(a,"pattern",h.PatternName); var type = PatternType(a,h.PatternType);
        var scale = ToolArgs.Number(a,"scale",h.PatternScale,true); var angle = ToolArgs.Number(a,"angle",h.PatternAngle);
        var associative = ToolArgs.Flag(a,"associative",h.Associative);
        using (var probe = new Hatch()) { probe.SetDatabaseDefaults(d.Database); SetPattern(probe,pattern,type,scale,angle); Color(probe,a); }
        Boundaries? bounds = null;
        try
        {
            if (a["outerHandles"] != null) bounds = ValidateBoundaries(d,tr,ToolArgs.Handles(a,"outerHandles",50),Islands(a));
            else if (associative && !h.Associative)
            {
                var saved = Recall(tr,h);
                if (!saved.HasValue) throw new CadFault("BOUNDARIES_REQUIRED","Para activar asociatividad de este HATCH proporciona outerHandles/islandHandles.");
                bounds = ValidateBoundaries(d,tr,saved.Value.outer,saved.Value.islands);
            }
            var layer = a["layer"] == null ? h.LayerId : DocumentAccess.Layer(tr,d.Database,ToolArgs.Text(a,"layer")).ObjectId;
            DocumentAccess.Layer(tr,d.Database,h.Layer); h.UpgradeOpen();
            SetPattern(h,pattern,type,scale,angle); Color(h,a); h.LayerId = layer;
            if (bounds != null) SetLoops(tr,h,bounds,associative);
            else if (!associative && h.Associative) { h.Associative = false; h.RemoveAssociatedObjectIds(); }
            Evaluate(h); var row = Describe(tr,h); row["identityPreserved"] = true; return row;
        }
        finally { bounds?.Dispose(); }
    },true);
}
