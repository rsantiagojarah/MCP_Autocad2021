using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CadMcp.Server;

[McpServerToolType]
public sealed partial class OfficialTools
{
    private readonly CadGateway gateway;
    public OfficialTools(CadGateway gateway) { this.gateway = gateway; }
    private async Task<CallToolResult> Call(string name, object arguments, CancellationToken token)
    {
        // Optional wrapper parameters are omitted, preserving strict catalog validation.
        var serializer = JsonSerializer.Create(Wire.Settings);
        serializer.NullValueHandling = NullValueHandling.Ignore;
        var args = JObject.FromObject(arguments, serializer);
        foreach (var p in System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(args.Properties(), p => p.Value.Type == JTokenType.Null))) p.Remove();
        var result = await gateway.CallAsync(name, args, token).ConfigureAwait(false);
        return new CallToolResult { IsError = !result.Ok, Content = new System.Collections.Generic.List<ContentBlock> { new TextContentBlock { Text = Wire.Serialize(result) } } };
    }
    [McpServerTool(Name = "cad_list_sessions"), Description("Descubre sesiones AutoCAD/Civil 3D y sus capacidades.")]
    public Task<CallToolResult> Sessions(CancellationToken cancellationToken) => Call("cad_list_sessions", new { }, cancellationToken);
    [McpServerTool(Name = "cad_list_documents"), Description("Lista documentos abiertos. Usa sus documentId en las demás herramientas.")]
    public Task<CallToolResult> Documents(string sessionId, CancellationToken cancellationToken) => Call("cad_list_documents", new { sessionId }, cancellationToken);
    [McpServerTool(Name = "cad_get_document_info"), Description("Consulta unidades, estado y matriz UCS a WCS del documento activo indicado.")]
    public Task<CallToolResult> Info(string sessionId, string documentId, CancellationToken cancellationToken) => Call("cad_get_document_info", new { sessionId, documentId }, cancellationToken);
    [McpServerTool(Name = "cad_list_layers"), Description("Consulta capas con paginación, limit máximo 500.")]
    public Task<CallToolResult> Layers(string sessionId, string documentId, CancellationToken cancellationToken, int offset = 0, int limit = 100) => Call("cad_list_layers", new { sessionId, documentId, offset, limit }, cancellationToken);
    [McpServerTool(Name = "cad_query_entities"), Description("Consulta ModelSpace filtrando por capa, DXF o caja WCS; devuelve propiedades y metrados. Recorre nextOffset hasta null sin alterar el dibujo.")]
    public Task<CallToolResult> Query(string sessionId, string documentId, CancellationToken cancellationToken, string? layer = null, string? dxfType = null, int offset = 0, int limit = 100, Vec3? min = null, Vec3? max = null) => Call("cad_query_entities", new { sessionId, documentId, layer, dxfType, offset, limit, min, max }, cancellationToken);
    [McpServerTool(Name = "cad_create_entities"), Description("Crea hasta 10000 entidades por lote. Tipos point/line/polyline. Requiere WCS, unidades coincidentes y capas existentes. operationId es un UUID que debes conservar ante reintentos.")]
    public Task<CallToolResult> Create(string sessionId, string documentId, string operationId, string coordinateSpace, string units, EntitySpec[] entities, CancellationToken cancellationToken) => Call("cad_create_entities", new { sessionId, documentId, operationId, coordinateSpace, units, entities }, cancellationToken);
    [McpServerTool(Name = "cad_upsert_layers"), Description("Crea capas o actualiza color ACI 1..255 por lote. operationId debe ser UUID; consérvalo ante reintentos.")]
    public Task<CallToolResult> UpsertLayers(string sessionId, string documentId, string operationId, LayerSpec[] layers, CancellationToken cancellationToken) => Call("cad_upsert_layers", new { sessionId, documentId, operationId, layers }, cancellationToken);
    [McpServerTool(Name = "cad_update_entities"), Description("Actualiza capa/color por handle en un lote atómico. operationId debe ser UUID; consérvalo ante reintentos.")]
    public Task<CallToolResult> Update(string sessionId, string documentId, string operationId, UpdateEntitySpec[] entities, CancellationToken cancellationToken) => Call("cad_update_entities", new { sessionId, documentId, operationId, entities }, cancellationToken);
    [McpServerTool(Name = "cad_get_request_status"), Description("Consulta el resultado original de un requestId. Historial limitado al arranque actual del puente. NOT_FOUND no demuestra que una operación anterior no se ejecutó.")]
    public Task<CallToolResult> Status(string sessionId, string requestId, CancellationToken cancellationToken) => Call("cad_get_request_status", new { sessionId, requestId }, cancellationToken);
    [McpServerTool(Name = "civil_list_surfaces"), Description("Lista superficies y handles; requiere capacidad civil.surfaces.")]
    public Task<CallToolResult> Surfaces(string sessionId, string documentId, CancellationToken cancellationToken, int offset = 0, int limit = 100) => Call("civil_list_surfaces", new { sessionId, documentId, offset, limit }, cancellationToken);
    [McpServerTool(Name = "civil_list_alignments"), Description("Lista alineamientos y hasta 100 perfiles por alineamiento. limit máximo 100; indica perfiles truncados.")]
    public Task<CallToolResult> Alignments(string sessionId, string documentId, CancellationToken cancellationToken, int offset = 0, int limit = 100) => Call("civil_list_alignments", new { sessionId, documentId, offset, limit }, cancellationToken);
    [McpServerTool(Name = "civil_get_surface_elevations"), Description("Consulta elevaciones por lote WCS con unidades coincidentes. Fuera de superficie devuelve OUTSIDE_SURFACE.")]
    public Task<CallToolResult> Elevations(string sessionId, string documentId, string surfaceHandle, string coordinateSpace, string units, Vec3[] points, CancellationToken cancellationToken) => Call("civil_get_surface_elevations", new { sessionId, documentId, surfaceHandle, coordinateSpace, units, points }, cancellationToken);
    [McpServerTool(Name = "civil_create_cogo_points"), Description("Crea puntos COGO por lote WCS con descripción común y unidades coincidentes. operationId es un UUID que debes conservar ante reintentos.")]
    public Task<CallToolResult> Cogo(string sessionId, string documentId, string operationId, string coordinateSpace, string units, string description, Vec3[] points, CancellationToken cancellationToken) => Call("civil_create_cogo_points", new { sessionId, documentId, operationId, coordinateSpace, units, description, points }, cancellationToken);
}
