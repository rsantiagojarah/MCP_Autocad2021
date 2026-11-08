using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadMcp.Contracts;
using CadMcp.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadMcp.AutoCAD;

public static class DocumentAccess
{
    private sealed class Identity { public string Id = Guid.NewGuid().ToString("D"); }
    private static readonly ConditionalWeakTable<Document, Identity> Identities = new ConditionalWeakTable<Document, Identity>();
    public static string Id(Document document) => Identities.GetOrCreateValue(document).Id;
    public static Document Active(BridgeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentId)) throw new CadFault("DOCUMENT_REQUIRED", "Indica documentId obtenido de cad_list_documents.");
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(Id(doc), request.DocumentId, StringComparison.Ordinal))
            throw new CadFault("DOCUMENT_NOT_ACTIVE", "El documento solicitado no está activo o se cerró. Actívalo y vuelve a consultar su identificador.");
        return doc;
    }
    public static void Units(Database database, string units, string coordinateSpace)
    {
        Validation.Require(coordinateSpace == "WCS", "Esta versión requiere coordinateSpace=WCS.");
        if (!string.Equals(units, database.Insunits.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new CadFault("UNIT_MISMATCH", "El dibujo utiliza " + database.Insunits + ". No se realiza conversión implícita.");
    }
    public static ObjectId ObjectId(Database db, string handle)
    {
        if (handle == null || handle.Length < 1 || handle.Length > 16 || !long.TryParse(handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw new CadFault("INVALID_ARGUMENT", "Handle hexadecimal inválido.");
        try
        {
            var id = db.GetObjectId(false, new Handle(number), 0);
            if (id.IsNull || id.IsErased) throw new CadFault("OBJECT_NOT_FOUND", "El objeto no existe o fue eliminado.");
            return id;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { throw new CadFault("OBJECT_NOT_FOUND", "No existe el handle " + handle + " en este dibujo."); }
    }
    public static LayerTableRecord Layer(Transaction tr, Database db, string name)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name)) throw new CadFault("LAYER_NOT_FOUND", "No existe la capa " + name + ". Créala primero con cad_upsert_layers.");
        var layer = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
        if (layer.IsLocked) throw new CadFault("LAYER_LOCKED", "La capa " + name + " está bloqueada.");
        return layer;
    }
}
