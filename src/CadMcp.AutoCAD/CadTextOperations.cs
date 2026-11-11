using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

public static class CadTextOperations
{
    public static void Register(OperationRegistry r)
    {
        r.Register("cad.text_styles", Styles);
        r.Register("cad.fonts", Fonts);
        r.Register("cad.upsert_text_style", Upsert, true);
        r.Register("cad.set_text_style", Assign, true);
        r.Register("cad.update_text", Update, true);
    }

    // AutoCAD's support search is authoritative. Paths are read only; no fonts are installed.
    internal static IEnumerable<string> SupportDirectories(Database db)
    {
        var roots = new List<string> { Path.GetDirectoryName(typeof(DBText).Assembly.Location)!,
            Path.Combine(Path.GetDirectoryName(typeof(DBText).Assembly.Location)!, "Fonts"),
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts) };
        var acad = Convert.ToString(AcApp.GetSystemVariable("ACADPREFIX")) ?? "";
        roots.AddRange(acad.Split(';'));
        foreach (var file in new[] { "acad.pat", "acadiso.pat", "txt.shx" })
        {
            try { roots.Add(Path.GetDirectoryName(HostApplicationServices.Current.FindFile(file, db, FindFileHint.Default))!); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
        }
        return roots.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindFontFile(Database db, string name)
    {
        if (Path.IsPathRooted(name) && File.Exists(name)) return Path.GetFullPath(name);
        try
        {
            var found = HostApplicationServices.Current.FindFile(name, db, FindFileHint.FontFile);
            if (File.Exists(found)) return found;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { }
        foreach (var root in SupportDirectories(db))
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new CadFault("FONT_NOT_FOUND", "Fuente no disponible: " + name + ". Consulta cad_list_fonts; no se utiliza una fuente sustituta.");
    }

    private sealed class FontChoice
    {
        public string File = "";
        public string Family = "";
        public bool Shx;
    }
    private static FontChoice ResolveFont(Database db, string value)
    {
        var extension = Path.GetExtension(value);
        if (extension.Equals(".shx", StringComparison.OrdinalIgnoreCase))
        {
            var file = FindFontFile(db, value);
            using (var input = File.OpenRead(file))
            {
                var header = new byte[32]; var length = input.Read(header, 0, header.Length);
                var text = System.Text.Encoding.ASCII.GetString(header, 0, length);
                if (!text.StartsWith("AutoCAD-86 unifont", StringComparison.OrdinalIgnoreCase))
                    throw new CadFault("INVALID_FONT", "Se requiere una fuente SHX unifont; archivos de formas y bigfont no son fuentes primarias compatibles.");
            }
            return new FontChoice { File = file, Shx = true };
        }
        if (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase))
        {
            var file = FindFontFile(db, value);
            try
            {
                using var collection = new PrivateFontCollection(); collection.AddFontFile(file);
                Validation.Require(collection.Families.Length > 0, "Archivo TTF sin familia válida.");
                return new FontChoice { File = file, Family = collection.Families[0].Name };
            }
            catch (ArgumentException) { throw new CadFault("INVALID_FONT", "Archivo TTF inválido: " + value); }
        }
        if (extension.Length > 0) throw new CadFault("INVALID_FONT", "Usa una familia TTF instalada o un archivo .ttf/.shx.");
        using var installed = new InstalledFontCollection();
        var family = installed.Families.FirstOrDefault(f => f.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (family == null) throw new CadFault("FONT_NOT_FOUND", "Familia TTF no instalada: " + value + ". Consulta cad_list_fonts.");
        return new FontChoice { Family = family.Name };
    }

    private static JObject StyleInfo(TextStyleTableRecord s) => new JObject
    {
        ["handle"] = s.Handle.ToString(), ["name"] = s.Name, ["fontFile"] = s.FileName,
        ["fontFamily"] = s.Font.TypeFace, ["bigFontFile"] = s.BigFontFileName,
        ["fixedHeight"] = s.TextSize, ["widthFactor"] = s.XScale, ["obliqueAngle"] = s.ObliquingAngle,
        ["isShapeFile"] = s.IsShapeFile, ["isDependent"] = s.IsDependent
    };
    private static JToken Styles(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var page = ToolArgs.Page(r.Arguments); var table = (TextStyleTable)tr.GetObject(d.Database.TextStyleTableId, OpenMode.ForRead);
        var ids = table.Cast<ObjectId>().ToArray();
        return new JObject { ["styles"] = new JArray(ids.Skip(page.offset).Take(page.limit).Select(id => StyleInfo((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)))),
            ["nextOffset"] = page.offset + page.limit < ids.Length ? new JValue(page.offset + page.limit) : JValue.CreateNull() };
    });
    private static JToken Fonts(BridgeRequest r) => CadExecution.Run(r, false, (d, tr) =>
    {
        var type = ToolArgs.Text(r.Arguments, "fontType", "all");
        Validation.Require(type == "all" || type == "ttf" || type == "shx", "fontType inválido.");
        var rows = new List<JObject>();
        if (type != "shx")
        {
            using var fonts = new InstalledFontCollection();
            rows.AddRange(fonts.Families.Select(f => new JObject { ["font"] = f.Name, ["fontType"] = "ttf" }));
        }
        if (type != "ttf")
            foreach (var dir in SupportDirectories(d.Database))
                foreach (var file in Directory.EnumerateFiles(dir, "*.shx"))
                {
                    using var input = File.OpenRead(file); var header = new byte[20]; var n = input.Read(header,0,header.Length);
                    if (System.Text.Encoding.ASCII.GetString(header,0,n).StartsWith("AutoCAD-86 unifont", StringComparison.OrdinalIgnoreCase))
                        rows.Add(new JObject { ["font"] = Path.GetFileName(file), ["fontFile"] = file, ["fontType"] = "shx" });
                }
        var all = rows.GroupBy(x => (string)x["font"]!, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => (string)x["font"]!, StringComparer.OrdinalIgnoreCase).ToArray();
        var page = ToolArgs.Page(r.Arguments);
        return new JObject { ["fonts"] = new JArray(all.Skip(page.offset).Take(page.limit)), ["nextOffset"] = page.offset + page.limit < all.Length ? new JValue(page.offset + page.limit) : JValue.CreateNull() };
    });
    private static JToken Upsert(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var name = ToolArgs.Text(a, "name"); SymbolUtilityServices.ValidateSymbolName(name, false);
        var table = (TextStyleTable)tr.GetObject(d.Database.TextStyleTableId, OpenMode.ForRead); var exists = table.Has(name);
        if (exists && !ToolArgs.Flag(a, "updateExisting")) throw new CadFault("STYLE_EXISTS", "El estilo ya existe. Usa otro nombre o updateExisting=true para modificar explícitamente su definición compartida.");
        var choice = ResolveFont(d.Database, ToolArgs.Text(a, "font"));
        var style = exists ? (TextStyleTableRecord)tr.GetObject(table[name], OpenMode.ForRead) : new TextStyleTableRecord { Name = name };
        try
        {
            if (style.IsDependent || style.IsShapeFile) throw new CadFault("INVALID_STYLE", "No se modifica un estilo dependiente o de formas.");
            var height = ToolArgs.Number(a, "fixedHeight", exists ? style.TextSize : 0);
            var width = ToolArgs.Number(a, "widthFactor", exists ? style.XScale : 1, true);
            var oblique = ToolArgs.Number(a, "obliqueAngle", exists ? style.ObliquingAngle : 0);
            Validation.Require(height >= 0 && width >= .01 && width <= 100 && Math.Abs(oblique) < Math.PI / 2, "Altura fija >=0; factor de ancho 0.01..100; inclinación entre -pi/2 y pi/2.");
            if (exists) style.UpgradeOpen();
            else { table.UpgradeOpen(); table.Add(style); tr.AddNewlyCreatedDBObject(style, true); }
            style.BigFontFileName = "";
            style.FileName = choice.File;
            if (!choice.Shx) style.Font = new FontDescriptor(choice.Family, false, false, 0, 0);
            style.TextSize = height; style.XScale = width; style.ObliquingAngle = oblique;
            var row = StyleInfo(style); row["created"] = !exists; row["sharedDefinitionChanged"] = exists;
            return row;
        }
        finally { if (!exists) style.Dispose(); }
    }, true);

    internal static TextStyleTableRecord Style(Document d, Transaction tr, string handle)
    {
        var id = DocumentAccess.ObjectId(d.Database, handle);
        var style = tr.GetObject(id, OpenMode.ForRead) as TextStyleTableRecord;
        if (style == null || style.IsShapeFile || style.IsDependent) throw new CadFault("INVALID_STYLE", "El handle no corresponde a un estilo de texto local compatible.");
        return style;
    }
    private static Entity TextEntity(Document d, Transaction tr, string handle)
    {
        var e = CadExecution.Entity<Entity>(d, tr, handle);
        if (!(e is DBText) || e is AttributeDefinition || e is AttributeReference)
            if (!(e is MText)) throw new CadFault("INVALID_OBJECT", "Se requiere TEXT o MTEXT, no atributos ni otros objetos: " + handle);
        var owner = tr.GetObject(e.OwnerId, OpenMode.ForRead) as BlockTableRecord;
        Validation.Require(owner != null && owner.IsLayout, "Edita textos directos de ModelSpace/PaperSpace. Un texto dentro de una definición de bloque afectaría todas sus instancias.");
        return e;
    }
    private static void SetStyle(Entity e, ObjectId style)
    {
        if (e is DBText text) { text.TextStyleId = style; text.AdjustAlignment(e.Database); }
        else ((MText)e).TextStyleId = style;
    }
    public static JObject Describe(Entity e)
    {
        var row = CadExecution.Identity(e); row["dxfType"] = e.GetRXClass().DxfName;
        if (e is DBText t)
        {
            row["text"] = t.TextString; row["height"] = t.Height; row["rotation"] = t.Rotation; row["styleHandle"] = t.TextStyleId.Handle.ToString();
            row["alignment"] = t.Justify.ToString(); row["position"] = CadOperations.Point(t.Justify == AttachmentPoint.BaseLeft ? t.Position : t.AlignmentPoint);
        }
        else if (e is MText m)
        {
            row["text"] = m.Contents; row["height"] = m.TextHeight; row["rotation"] = m.Rotation; row["styleHandle"] = m.TextStyleId.Handle.ToString();
            row["alignment"] = m.Attachment.ToString(); row["position"] = CadOperations.Point(m.Location);
        }
        return row;
    }
    private static JToken Assign(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var style = Style(d, tr, ToolArgs.Text(r.Arguments, "styleHandle"));
        var entities = ToolArgs.Handles(r.Arguments).Select(h => TextEntity(d,tr,h)).ToArray();
        foreach (var e in entities) DocumentAccess.Layer(tr, d.Database, e.Layer);
        foreach (var e in entities) { e.UpgradeOpen(); SetStyle(e, style.ObjectId); }
        return new JObject { ["texts"] = new JArray(entities.Select(Describe)), ["styleDefinitionChanged"] = false };
    });

    private static AttachmentPoint Alignment(string name, bool multiline)
    {
        var names = new[] { "baseLeft", "baseCenter", "baseRight", "topLeft", "topCenter", "topRight", "middleLeft", "middleCenter", "middleRight", "bottomLeft", "bottomCenter", "bottomRight" };
        Validation.Require(names.Contains(name), "Alineación no compatible.");
        Validation.Require(!multiline || !name.StartsWith("base", StringComparison.Ordinal), "MTEXT requiere alineación top/middle/bottom.");
        return (AttachmentPoint)Enum.Parse(typeof(AttachmentPoint), name, true);
    }
    private static void Edit(Document d, Entity e, JObject a)
    {
        var multiline = e is MText;
        var height = a["height"] == null ? (double?)null : ToolArgs.Number(a, "height", positive: true);
        var rotation = a["rotation"] == null ? (double?)null : ToolArgs.Number(a, "rotation");
        var align = a["alignment"] == null ? (AttachmentPoint?)null : Alignment(ToolArgs.Text(a, "alignment"), multiline);
        var pos = a["position"] == null ? (Point3d?)null : CadOperations.ToPoint(ToolArgs.Point(a, "position"));
        string? contents = null;
        if (a["text"] != null)
        {
            Validation.Require(a["text"]!.Type == JTokenType.String, "text debe ser texto."); contents = a["text"]!.Value<string>()!;
            Validation.Require(contents.Length <= (multiline ? 4096 : 256), "TEXT admite hasta 256 caracteres; MTEXT hasta 4096 por edición.");
        }
        if (e is DBText t)
        {
            var anchor = pos ?? (t.Justify == AttachmentPoint.BaseLeft ? t.Position : t.AlignmentPoint);
            if (contents != null) t.TextString = contents;
            if (height.HasValue) t.Height = height.Value;
            if (rotation.HasValue) t.Rotation = rotation.Value;
            if (align.HasValue) t.Justify = align.Value;
            if (pos.HasValue || align.HasValue)
            {
                if (t.Justify == AttachmentPoint.BaseLeft) t.Position = anchor;
                else t.AlignmentPoint = anchor;
            }
            t.AdjustAlignment(d.Database);
        }
        else
        {
            var m = (MText)e; var anchor = pos ?? m.Location;
            if (contents != null) m.Contents = contents;
            if (height.HasValue) m.TextHeight = height.Value;
            if (rotation.HasValue) m.Rotation = rotation.Value;
            if (align.HasValue) m.Attachment = align.Value;
            if (pos.HasValue || align.HasValue) m.Location = anchor;
        }
    }
    private static JToken Update(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; Validation.Require(new[] { "text", "height", "rotation", "alignment", "position" }.Any(k => a[k] != null), "Indica al menos una propiedad de texto.");
        var entities = ToolArgs.Handles(a).Select(h => TextEntity(d,tr,h)).ToArray();
        foreach (var e in entities) { DocumentAccess.Layer(tr,d.Database,e.Layer); e.UpgradeOpen(); Edit(d,e,a); }
        return new JObject { ["texts"] = new JArray(entities.Select(Describe)), ["identityPreserved"] = true };
    }, true);

    public static JToken Create(BridgeRequest r) => CadExecution.Run(r, true, (d, tr) =>
    {
        var a = r.Arguments; var kind = ToolArgs.Text(a, "type", "text");
        Validation.Require(kind == "text" || kind == "mtext", "type debe ser text o mtext.");
        var height = ToolArgs.Number(a, "height", positive: true); var point = CadOperations.ToPoint(ToolArgs.Point(a, "position"));
        var layer = CadExecution.Layer(d,tr,ToolArgs.Text(a,"layer"));
        var style = a["styleHandle"] == null ? (TextStyleTableRecord)tr.GetObject(d.Database.Textstyle,OpenMode.ForRead) : Style(d,tr,ToolArgs.Text(a,"styleHandle"));
        using Entity e = kind == "text" ? (Entity)new DBText() : new MText();
        // Append applies database defaults before we select the requested text style.
        CadExecution.Append(d,tr,e,layer);
        if (e is DBText t) { t.TextStyleId = style.ObjectId; t.Height = height; t.Position = point; t.WidthFactor = style.XScale; t.Oblique = style.ObliquingAngle; }
        else { var m = (MText)e; m.TextStyleId = style.ObjectId; m.TextHeight = height; m.Location = point; }
        Edit(d,e,a); return Describe(e);
    }, true);
}
