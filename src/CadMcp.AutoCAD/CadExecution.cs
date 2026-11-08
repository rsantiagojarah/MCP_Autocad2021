using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;

namespace CadMcp.AutoCAD;

public static class CadExecution
{
    public static JToken Run(BridgeRequest request, bool write, Func<Document, Transaction, JToken> action, bool coordinates = false)
    {
        var doc = DocumentAccess.Active(request);
        if (coordinates) DocumentAccess.Units(doc.Database, ToolArgs.Text(request.Arguments, "units"), ToolArgs.Text(request.Arguments, "coordinateSpace"));
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var result = action(doc, tr);
            if (write) tr.Commit();
            return result;
        }
    }
    public static T Entity<T>(Document doc, Transaction tr, string handle, bool write = false) where T : Entity
    {
        var entity = tr.GetObject(DocumentAccess.ObjectId(doc.Database, handle), OpenMode.ForRead) as T
            ?? throw new CadFault("INVALID_OBJECT", "Handle de tipo incorrecto: " + handle + "; esperado " + typeof(T).Name);
        if (write)
        {
            DocumentAccess.Layer(tr, doc.Database, entity.Layer);
            // References and externally dependent Civil objects must not be edited.
            var reference = entity.GetType().GetProperty("IsReferenceObject");
            if (reference?.GetValue(entity) is bool isReference && isReference) throw new CadFault("REFERENCE_OBJECT", "No se modifica un objeto referenciado.");
            if (!entity.IsWriteEnabled) entity.UpgradeOpen();
        }
        return entity;
    }
    public static ObjectId Layer(Document doc, Transaction tr, string name, short color = 7)
    {
        SymbolUtilityServices.ValidateSymbolName(name, false);
        var table = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
        if (table.Has(name)) return DocumentAccess.Layer(tr, doc.Database, name).ObjectId;
        table.UpgradeOpen();
        var layer = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, color) };
        var id = table.Add(layer); tr.AddNewlyCreatedDBObject(layer, true); return id;
    }
    public static string Append(Document doc, Transaction tr, Entity entity, ObjectId layer)
    {
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        entity.SetDatabaseDefaults(doc.Database); entity.LayerId = layer;
        model.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); return entity.Handle.ToString();
    }
    public static JObject Identity(Entity entity) => new JObject { ["handle"] = entity.Handle.ToString(), ["type"] = entity.GetType().Name, ["layer"] = entity.Layer };
}
