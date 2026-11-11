using System;
using System.Collections.Generic;
using System.Linq;
using CadMcp.Contracts;
using CadMcp.Core;
using Newtonsoft.Json.Linq;

namespace CadMcp.Server;

public sealed class ToolDefinition
{
    public string Name { get; }
    public string Method { get; }
    public string Description { get; }
    public bool Mutates { get; }
    public JObject Schema { get; }
    public ToolDefinition(string name, string method, string description, bool mutates, JObject schema)
    { Name = name; Method = method; Description = description; Mutates = mutates; Schema = schema; }
    public JObject Protocol() => new JObject { ["name"] = Name, ["description"] = Description, ["inputSchema"] = Schema,
        ["annotations"] = new JObject { ["readOnlyHint"] = !Mutates, ["destructiveHint"] = Mutates, ["idempotentHint"] = !Mutates } };
}

public static partial class ToolCatalog
{
    private static JObject String() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 };
    private static JObject Integer(int min, int max) => new JObject { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };
    private static JObject Array(JObject item, int min, int max) => new JObject { ["type"] = "array", ["items"] = item, ["minItems"] = min, ["maxItems"] = max };
    private static JObject Object(JObject properties, params string[] required) => new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required), ["additionalProperties"] = false };
    private static JObject Point() => Object(new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" }, ["z"] = new JObject { ["type"] = "number" } }, "x", "y", "z");
    private static JObject Base(bool document = true, bool mutation = false)
    {
        var p = new JObject { ["sessionId"] = String() };
        if (document) p["documentId"] = String();
        if (mutation) p["operationId"] = new JObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "UUID nuevo por modificación. Conserva el mismo UUID para consultar o reintentar exactamente la misma operación." };
        return p;
    }
    private static ToolDefinition Tool(string name, string method, string description, JObject extra, string[] required, bool document = true, bool mutation = false)
    {
        var props = Base(document, mutation);
        foreach (var p in extra.Properties()) props[p.Name] = p.Value;
        var all = new List<string> { "sessionId" }; if (document) all.Add("documentId"); if (mutation) all.Add("operationId"); all.AddRange(required);
        return new ToolDefinition(name, method, description, mutation, Object(props, all.ToArray()));
    }
    private static JObject Page(int max = 500) => new JObject { ["offset"] = Integer(0, 1000000), ["limit"] = Integer(1, max) };
    private static JObject Lineweight(bool layer = false)
    {
        var schema = Integer(-3,211); schema["enum"] = new JArray(LineGraphicsRules.Lineweights(layer));
        schema["description"] = "Centésimas de mm: 25=0.25mm; -1=ByLayer, -2=ByBlock, -3=Default. Usa cad_list_lineweights.";
        return schema;
    }
    public static readonly IReadOnlyList<ToolDefinition> All = Build();
    private static IReadOnlyList<ToolDefinition> Build()
    {
        var geometry = Object(new JObject { ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("point", "line", "polyline") }, ["layer"] = String(), ["points"] = Array(Point(), 1, 100000), ["closed"] = new JObject { ["type"] = "boolean" } }, "type", "layer", "points");
        var coords = new JObject { ["coordinateSpace"] = new JObject { ["type"] = "string", ["enum"] = new JArray("WCS") }, ["units"] = String() };
        var create = (JObject)coords.DeepClone(); create["entities"] = Array(geometry, 1, 10000);
        var query = Page(); query["layer"] = String(); query["dxfType"] = String(); query["min"] = Point(); query["max"] = Point();
        var elevation = (JObject)coords.DeepClone(); elevation["surfaceHandle"] = String(); elevation["points"] = Array(Point(), 1, 10000);
        var cogo = (JObject)coords.DeepClone(); cogo["points"] = Array(Point(), 1, 10000); cogo["description"] = new JObject { ["type"] = "string", ["maxLength"] = 1024 };
        return new[]
        {
            new ToolDefinition("cad_list_sessions", "", "Descubre puentes de AutoCAD/Civil 3D del usuario de Windows actual. Devuelve capacidades e identificador de arranque.", false, Object(new JObject())),
            Tool("cad_list_documents", "cad.documents", "Lista documentos abiertos y sus identificadores de sesión.", new JObject(), System.Array.Empty<string>(), false),
            Tool("cad_get_document_info", "cad.document_info", "Lee unidades, estado y matriz UCS a WCS del documento activo indicado.", new JObject(), System.Array.Empty<string>()),
            Tool("cad_list_layers", "cad.layers", "Consulta capas y propiedades con paginación.", Page(), System.Array.Empty<string>()),
            Tool("cad_query_entities", "cad.query_entities", "Consulta ModelSpace: filtros por capa, tipo DXF y caja WCS; devuelve propiedades y metrados. Recorre nextOffset hasta null; no alteres el dibujo durante la paginación.", query, System.Array.Empty<string>()),
            Tool("cad_create_entities", "cad.create_entities", "Crea puntos, líneas o polilíneas 2D en lote atómico. Requiere capas existentes, WCS y unidades coincidentes. No convierte coordenadas ni unidades.", create, new[] { "coordinateSpace", "units", "entities" }, mutation: true),
            Tool("cad_upsert_layers", "cad.upsert_layers", "Crea capas o actualiza color ACI; admite lineweight (centésimas de mm) y linetype ya cargado opcionales en un lote atómico.", new JObject { ["layers"] = Array(Object(new JObject { ["name"] = String(), ["colorIndex"] = Integer(1, 255), ["lineweight"] = Lineweight(true), ["linetype"] = String() }, "name", "colorIndex"), 1, 100) }, new[] { "layers" }, mutation: true),
            Tool("cad_update_entities", "cad.update_entities", "Actualiza capa/color y opcionalmente lineweight, linetype y linetypeScale por handle en lote atómico; respeta capas bloqueadas.", new JObject { ["entities"] = Array(Object(new JObject { ["handle"] = String(), ["layer"] = String(), ["colorIndex"] = Integer(0, 256), ["lineweight"] = Lineweight(), ["linetype"] = String(), ["linetypeScale"] = new JObject { ["type"] = "number" } }, "handle"), 1, 10000) }, new[] { "entities" }, mutation: true),
            Tool("cad_get_request_status", "request.status", "Consulta queued/running/completed/failed y el resultado original de un UUID. NOT_FOUND no demuestra que una operación de un arranque anterior no se ejecutó.", new JObject { ["requestId"] = String() }, new[] { "requestId" }, false),
            Tool("civil_list_surfaces", "civil.surfaces", "Lista superficies y handles. Solo sesiones con módulo Civil 3D.", Page(), System.Array.Empty<string>()),
            Tool("civil_list_alignments", "civil.alignments", "Lista alineamientos, estaciones y hasta 100 perfiles por alineamiento; indica si se truncaron.", Page(100), System.Array.Empty<string>()),
            Tool("civil_get_surface_elevations", "civil.surface_elevations", "Consulta elevaciones por lote de coordenadas WCS. Puntos fuera de superficie devuelven OUTSIDE_SURFACE.", elevation, new[] { "coordinateSpace", "units", "surfaceHandle", "points" }),
            Tool("civil_create_cogo_points", "civil.create_cogo_points", "Crea puntos COGO en lote con descripción común y numeración de Civil 3D. Requiere WCS y unidades coincidentes.", cogo, new[] { "coordinateSpace", "units", "points", "description" }, mutation: true)
        }.Concat(Extended()).Concat(SectionTools()).ToArray();
    }

    // The same validation is used by both transports. JSON Schema subset used by this catalog.
    public static void Validate(JToken value, JObject schema, string path = "arguments")
    {
        void Require(bool ok, string text) { if (!ok) throw new CadFault("INVALID_ARGUMENT", path + ": " + text); }
        var type = (string?)schema["type"];
        if (value.Type == JTokenType.Null && schema["nullable"]?.Value<bool>() == true) return;
        switch (type)
        {
            case "object":
                Require(value is JObject, "se esperaba objeto"); var obj = (JObject)value;
                var props = (JObject)schema["properties"]!;
                foreach (var required in (JArray)schema["required"]!) Require(obj.Property(required.Value<string>()!) != null, "falta " + required);
                foreach (var p in obj.Properties()) { Require(props[p.Name] != null, "propiedad desconocida " + p.Name); Validate(p.Value, (JObject)props[p.Name]!, path + "." + p.Name); }
                break;
            case "array":
                Require(value is JArray, "se esperaba lista"); var array = (JArray)value;
                Require(array.Count >= schema["minItems"]!.Value<int>() && array.Count <= schema["maxItems"]!.Value<int>(), "tamaño de lista inválido");
                for (var i = 0; i < array.Count; i++) Validate(array[i], (JObject)schema["items"]!, path + "[" + i + "]");
                break;
            case "string":
                Require(value.Type == JTokenType.String, "se esperaba texto"); var s = value.Value<string>()!;
                if (schema["minLength"] != null) Require(s.Length >= schema["minLength"]!.Value<int>(), "texto vacío");
                if (schema["maxLength"] != null) Require(s.Length <= schema["maxLength"]!.Value<int>(), "texto demasiado largo");
                if ((string?)schema["format"] == "uuid") Require(Guid.TryParseExact(s, "D", out _), "se esperaba UUID con guiones");
                break;
            case "integer":
                Require(value.Type == JTokenType.Integer, "se esperaba entero"); var n = value.Value<long>();
                Require(n >= schema["minimum"]!.Value<long>() && n <= schema["maximum"]!.Value<long>(), "entero fuera de rango"); break;
            case "number": Require((value.Type == JTokenType.Integer || value.Type == JTokenType.Float) && Validation.Finite(value.Value<double>()), "se esperaba número finito"); break;
            case "boolean": Require(value.Type == JTokenType.Boolean, "se esperaba booleano"); break;
        }
        if (schema["enum"] is JArray allowed) Require(System.Linq.Enumerable.Any(allowed, v => JToken.DeepEquals(v, value)), "valor no permitido");
    }
}
