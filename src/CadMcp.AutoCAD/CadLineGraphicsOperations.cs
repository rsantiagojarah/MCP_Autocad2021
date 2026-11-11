using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

public static class CadLineGraphicsOperations
{
    public static void Register(OperationRegistry r)
    {
        r.Register("cad.lineweights", Weights);
        r.Register("cad.linetypes", Linetypes);
        r.Register("cad.linetype_definitions", Definitions);
        r.Register("cad.load_linetypes", Load, true);
        r.Register("cad.line_properties", Properties);
        r.Register("cad.set_line_properties", SetProperties, true);
        r.Register("cad.set_layer_line_properties", SetLayers, true);
        r.Register("cad.line_settings", Settings);
        r.Register("cad.set_line_settings", SetSettings, true);
    }
    private static string Mode(int weight) => weight == -1 ? "ByLayer" : weight == -2 ? "ByBlock" : weight == -3 ? "Default" : "Explicit";
    private static JToken Weights(BridgeRequest r) => CadExecution.Run(r,false,(d,tr)=>new JObject
    {
        ["codeUnit"]="hundredths_of_millimetre", ["lineweights"]=new JArray(LineGraphicsRules.Lineweights().Select(w=>new JObject
        { ["code"]=w, ["mode"]=Mode(w), ["millimetres"]=w>=0 ? new JValue(w/100.0) : JValue.CreateNull(), ["allowedForLayers"]=w!=-1 && w!=-2 }))
    });
    private static JObject TypeInfo(LinetypeTableRecord t)
    {
        var count=Math.Min(t.NumDashes,64);
        return new JObject { ["name"]=t.Name,["handle"]=t.Handle.ToString(),["description"]=t.AsciiDescription,
            ["patternLength"]=t.PatternLength,["dashCount"]=t.NumDashes,
            ["dashes"]=new JArray(Enumerable.Range(0,count).Select(t.DashLengthAt)),["dashesTruncated"]=t.NumDashes>count,["isDependent"]=t.IsDependent };
    }
    private static JToken Linetypes(BridgeRequest r) => CadExecution.Run(r,false,(d,tr)=>
    {
        var page=ToolArgs.Page(r.Arguments); var table=(LinetypeTable)tr.GetObject(d.Database.LinetypeTableId,OpenMode.ForRead);
        var ids=table.Cast<ObjectId>().ToArray();
        return new JObject { ["linetypes"]=new JArray(ids.Skip(page.offset).Take(page.limit).Select(id=>TypeInfo((LinetypeTableRecord)tr.GetObject(id,OpenMode.ForRead)))),
            ["nextOffset"]=page.offset+page.limit<ids.Length ? new JValue(page.offset+page.limit) : JValue.CreateNull(),["patternUnits"]=d.Database.Insunits.ToString() };
    });
    private static string FindLin(Database db,string file)
    {
        Validation.Require(Path.GetExtension(file).Equals(".lin",StringComparison.OrdinalIgnoreCase)
            && !file.Contains("://"),"Indica un archivo .LIN local o de las rutas de soporte; no una URL.");
        string path=file;
        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            try { path=HostApplicationServices.Current.FindFile(file,db,FindFileHint.Default); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { throw new CadFault("LIN_FILE_NOT_FOUND","No se encontró el archivo LIN: "+file); }
        }
        if (!File.Exists(path)) throw new CadFault("LIN_FILE_NOT_FOUND","No se encontró el archivo LIN: "+file);
        Validation.Require(new FileInfo(path).Length<=8*1024*1024,"El archivo LIN supera 8 MiB.");
        return Path.GetFullPath(path);
    }
    private static KeyValuePair<string,string>[] Headers(string path)
    {
        var rows=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var raw in File.ReadLines(path,System.Text.Encoding.Default))
        {
            var line=raw.Trim(); if(!line.StartsWith("*",StringComparison.Ordinal)) continue;
            var split=line.IndexOf(','); var name=(split<0?line.Substring(1):line.Substring(1,split-1)).Trim();
            LineGraphicsRules.Name(name);
            Validation.Require(!rows.ContainsKey(name),"Definición duplicada en LIN: "+name);
            var description=split<0?"":line.Substring(split+1).Trim(); rows[name]=description.Length>4096?description.Substring(0,4096):description;
            Validation.Require(rows.Count<=10000,"Más de 10000 definiciones en un LIN.");
        }
        return rows.ToArray();
    }
    private static JToken Definitions(BridgeRequest r) => CadExecution.Run(r,false,(d,tr)=>
    {
        var path=FindLin(d.Database,ToolArgs.Text(r.Arguments,"file")); var all=Headers(path); var page=ToolArgs.Page(r.Arguments);
        return new JObject { ["file"]=path,["definitions"]=new JArray(all.Skip(page.offset).Take(page.limit).Select(p=>new JObject { ["name"]=p.Key,["description"]=p.Value })),
            ["nextOffset"]=page.offset+page.limit<all.Length ? new JValue(page.offset+page.limit) : JValue.CreateNull() };
    });
    private static JToken Load(BridgeRequest r) => CadExecution.Run(r,true,(d,tr)=>
    {
        var a=r.Arguments; var names=ToolArgs.Handles(a,"names",100); foreach(var name in names) LineGraphicsRules.Name(name);
        var path=FindLin(d.Database,ToolArgs.Text(a,"file")); var available=Headers(path).Select(p=>p.Key).ToArray();
        foreach(var name in names) if(!available.Contains(name,StringComparer.OrdinalIgnoreCase))
            throw new CadFault("LINETYPE_NOT_IN_FILE","La definición "+name+" no existe en "+path);
        var table=(LinetypeTable)tr.GetObject(d.Database.LinetypeTableId,OpenMode.ForRead);
        var pending=names.Where(n=>!table.Has(n)).ToArray(); var existing=names.Where(table.Has).ToArray();
        if(pending.Length>0)
        {
            // Validate/construct every definition away from the drawing. A malformed later entry
            // must not leave earlier native loads committed into the real linetype table.
            using var scratch=new Database(true,true); var previous=HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase=scratch;
                foreach(var name in pending) scratch.LoadLineTypeFile(name,path);
            }
            catch(Autodesk.AutoCAD.Runtime.Exception ex) { throw new CadFault("LINETYPE_LOAD_FAILED","AutoCAD no pudo cargar el lote LIN (sin modificar el dibujo): "+ex.ErrorStatus+". Revisa sintaxis y fuentes/formas SHX requeridas."); }
            finally { HostApplicationServices.WorkingDatabase=previous; }
            using var source=scratch.TransactionManager.StartOpenCloseTransaction();
            var sourceTable=(LinetypeTable)source.GetObject(scratch.LinetypeTableId,OpenMode.ForRead);
            using var ids=new ObjectIdCollection(pending.Select(n=>sourceTable[n]).ToArray()); using var mapping=new IdMapping();
            scratch.WblockCloneObjects(ids,d.Database.LinetypeTableId,mapping,DuplicateRecordCloning.Ignore,false);
        }
        return new JObject { ["file"]=path,["loaded"]=new JArray(pending),["alreadyLoaded"]=new JArray(existing),
            ["linetypes"]=new JArray(names.Select(n=>TypeInfo((LinetypeTableRecord)tr.GetObject(table[n],OpenMode.ForRead)))) };
    });

    internal static ObjectId Linetype(Document d,Transaction tr,string name,bool layer=false)
    {
        LineGraphicsRules.Name(name);
        if(layer && (name.Equals("ByLayer",StringComparison.OrdinalIgnoreCase)||name.Equals("ByBlock",StringComparison.OrdinalIgnoreCase)))
            throw new CadFault("INVALID_LINETYPE","Una capa requiere un tipo de línea concreto, no ByLayer/ByBlock.");
        var table=(LinetypeTable)tr.GetObject(d.Database.LinetypeTableId,OpenMode.ForRead);
        if(!table.Has(name)) throw new CadFault("LINETYPE_NOT_LOADED","Tipo de línea no cargado: "+name+". Usa cad_load_linetypes primero.");
        var record=(LinetypeTableRecord)tr.GetObject(table[name],OpenMode.ForRead);
        if(record.IsDependent) throw new CadFault("DEPENDENT_LINETYPE","No se asigna un tipo dependiente de una referencia externa.");
        return record.ObjectId;
    }
    internal static void ValidateEntity(Document d,Transaction tr,Entity e)
    {
        Validation.Require(e.GetType().Assembly==typeof(Entity).Assembly && !e.GetRXClass().DxfName.StartsWith("AECC",StringComparison.OrdinalIgnoreCase),"Utiliza los estilos nativos de Civil para sus componentes gráficos.");
        var owner=tr.GetObject(e.OwnerId,OpenMode.ForRead) as BlockTableRecord;
        Validation.Require(owner!=null && owner.IsLayout,"Solo entidades directas en ModelSpace/PaperSpace; no se modifican definiciones de bloque ni referencias externas.");
        DocumentAccess.Layer(tr,d.Database,e.Layer);
    }
    internal static void ValidateProperties(JObject a,bool layer=false)
    {
        if(a["lineweight"]!=null)
        {
            Validation.Require(a["lineweight"]!.Type==JTokenType.Integer,"lineweight debe ser un código entero en centésimas de mm.");
            LineGraphicsRules.Weight(a["lineweight"]!.Value<int>(),layer);
        }
        if(a["linetype"]!=null) LineGraphicsRules.Name(ToolArgs.Text(a,"linetype"));
        if(a["linetypeScale"]!=null) LineGraphicsRules.Scale(ToolArgs.Number(a,"linetypeScale"));
        if(a["colorIndex"]!=null)
            Validation.Require(a["colorIndex"]!.Type==JTokenType.Integer && a["colorIndex"]!.Value<int>()>=(layer?1:0) && a["colorIndex"]!.Value<int>()<=(layer?255:256),"Color ACI fuera de rango.");
    }
    internal static void ApplyEntity(Document d,Transaction tr,Entity e,JObject a)
    {
        ValidateProperties(a); ValidateEntity(d,tr,e);
        var target=a["layer"]==null ? e.LayerId : DocumentAccess.Layer(tr,d.Database,ToolArgs.Text(a,"layer")).ObjectId;
        var type=a["linetype"]==null ? e.LinetypeId : Linetype(d,tr,ToolArgs.Text(a,"linetype"));
        if(!e.IsWriteEnabled) e.UpgradeOpen();
        e.LayerId=target; e.LinetypeId=type;
        if(a["lineweight"]!=null) e.LineWeight=(LineWeight)a["lineweight"]!.Value<int>();
        if(a["linetypeScale"]!=null) e.LinetypeScale=a["linetypeScale"]!.Value<double>();
        if(a["colorIndex"]!=null) e.ColorIndex=a["colorIndex"]!.Value<int>();
    }
    public static JObject Describe(Document d,Transaction tr,Entity e)
    {
        var row=CadExecution.Identity(e); var layer=(LayerTableRecord)tr.GetObject(e.LayerId,OpenMode.ForRead);
        var weight=(int)e.LineWeight; var inheritedWeight=weight==-1?(int)layer.LineWeight:weight;
        if(inheritedWeight==-3) inheritedWeight=Convert.ToInt32(AcApp.GetSystemVariable("LWDEFAULT"));
        var type=(LinetypeTableRecord)tr.GetObject(e.LinetypeId,OpenMode.ForRead);
        var layerType=(LinetypeTableRecord)tr.GetObject(layer.LinetypeObjectId,OpenMode.ForRead);
        var effectiveType=type.Name.Equals("ByLayer",StringComparison.OrdinalIgnoreCase)?layerType.Name:type.Name;
        row["dxfType"]=e.GetRXClass().DxfName; row["lineweight"]=weight; row["lineweightMode"]=Mode(weight);
        row["lineweightMm"]=weight>=0?new JValue(weight/100.0):JValue.CreateNull();
        row["linetype"]=type.Name; row["linetypeHandle"]=type.Handle.ToString(); row["linetypeScale"]=e.LinetypeScale;
        row["colorIndex"]=e.ColorIndex; row["layerLineweight"]=(int)layer.LineWeight; row["layerLinetype"]=layerType.Name;
        row["effectiveLineweightMm"]=inheritedWeight>=0?new JValue(inheritedWeight/100.0):JValue.CreateNull();
        row["effectiveLinetype"]=effectiveType.Equals("ByBlock",StringComparison.OrdinalIgnoreCase)?JValue.CreateNull():new JValue(effectiveType);
        row["globalScale"]=d.Database.Ltscale; row["combinedLinetypeScale"]=d.Database.Ltscale*e.LinetypeScale;
        row["scaleNote"]="El producto global/objeto no incluye escalas de ventanas gráficas, MSLTSCALE/PSLTSCALE ni impresión CTB/STB.";
        return row;
    }
    private static JToken Properties(BridgeRequest r) => CadExecution.Run(r,false,(d,tr)=>new JObject
    { ["entities"]=new JArray(ToolArgs.Handles(r.Arguments,max:500).Select(h=>Describe(d,tr,CadExecution.Entity<Entity>(d,tr,h)))) });
    private static JToken SetProperties(BridgeRequest r) => CadExecution.Run(r,true,(d,tr)=>
    {
        var a=r.Arguments; Validation.Require(new[]{"lineweight","linetype","linetypeScale","layer","colorIndex"}.Any(k=>a[k]!=null),"Indica al menos una propiedad gráfica.");
        ValidateProperties(a); var entities=ToolArgs.Handles(a).Select(h=>CadExecution.Entity<Entity>(d,tr,h)).ToArray();
        foreach(var e in entities) ValidateEntity(d,tr,e);
        foreach(var e in entities) ApplyEntity(d,tr,e,a);
        return new JObject { ["entities"]=new JArray(entities.Select(e=>Describe(d,tr,e))),["geometryPreserved"]=true };
    });
    private static JToken SetLayers(BridgeRequest r) => CadExecution.Run(r,true,(d,tr)=>
    {
        var a=r.Arguments; Validation.Require(new[]{"lineweight","linetype","colorIndex"}.Any(k=>a[k]!=null),"Indica grosor, tipo o color de capa.");
        ValidateProperties(a,true); var names=ToolArgs.Handles(a,"layers",100);
        var layers=names.Select(n=>DocumentAccess.Layer(tr,d.Database,n)).ToArray();
        var type=a["linetype"]==null?ObjectId.Null:Linetype(d,tr,ToolArgs.Text(a,"linetype"),true);
        foreach(var layer in layers)
        {
            if(layer.IsDependent) throw new CadFault("DEPENDENT_LAYER","No se cambia una capa de referencia externa.");
            layer.UpgradeOpen();
            if(a["lineweight"]!=null) layer.LineWeight=(LineWeight)a["lineweight"]!.Value<int>();
            if(!type.IsNull) layer.LinetypeObjectId=type;
            if(a["colorIndex"]!=null) layer.Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,(short)a["colorIndex"]!.Value<int>());
        }
        return new JObject { ["updated"]=layers.Length,["layers"]=new JArray(layers.Select(l=>new JObject { ["name"]=l.Name,["lineweight"]=(int)l.LineWeight,["linetype"]=((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId,OpenMode.ForRead)).Name,["colorIndex"]=l.Color.ColorIndex })),
            ["sharedLayerPropertiesChanged"]=true };
    });
    private static JObject SettingsInfo(Database db) => new JObject { ["globalScale"]=db.Ltscale,["newEntityScale"]=db.Celtscale,["displayLineweight"]=db.LineWeightDisplay };
    private static JToken Settings(BridgeRequest r) => CadExecution.Run(r,false,(d,tr)=>SettingsInfo(d.Database));
    private static JToken SetSettings(BridgeRequest r) => CadExecution.Run(r,true,(d,tr)=>
    {
        var a=r.Arguments; Validation.Require(new[]{"globalScale","newEntityScale","displayLineweight"}.Any(k=>a[k]!=null),"Indica al menos un ajuste de línea.");
        var db=d.Database; var global=ToolArgs.Number(a,"globalScale",db.Ltscale); var current=ToolArgs.Number(a,"newEntityScale",db.Celtscale);
        LineGraphicsRules.Scale(global); LineGraphicsRules.Scale(current); var display=ToolArgs.Flag(a,"displayLineweight",db.LineWeightDisplay);
        var oldGlobal=db.Ltscale; var oldCurrent=db.Celtscale; var oldDisplay=db.LineWeightDisplay;
        try { db.Ltscale=global; db.Celtscale=current; db.LineWeightDisplay=display; return SettingsInfo(db); }
        catch { db.Ltscale=oldGlobal; db.Celtscale=oldCurrent; db.LineWeightDisplay=oldDisplay; throw; }
    });
}
