using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CadMcp.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CadMcp.Server;

public sealed partial class OfficialTools
{
    [McpServerTool(Name = "civil_get_section_sources"), Description("Lista fuentes disponibles/muestreadas y modo dinámico de un grupo de muestreo.")]
    public Task<CallToolResult> SectionSources(string sessionId, string documentId, string sampleLineGroupHandle, CancellationToken cancellationToken)
        => Call("civil_get_section_sources", new { sessionId, documentId, sampleLineGroupHandle }, cancellationToken);
    [McpServerTool(Name = "civil_set_section_sources"), Description("Activa fuentes disponibles en el grupo; replace=false conserva las demás. dynamic=true activa actualización Civil. Lote atómico, operationId UUID.")]
    public Task<CallToolResult> SetSectionSources(string sessionId, string documentId, string operationId, string sampleLineGroupHandle, string[] sourceHandles,
        CancellationToken cancellationToken, bool replace = false, bool dynamic = true, string? sectionStyleHandle = null)
        => Call("civil_set_section_sources", new { sessionId, documentId, operationId, sampleLineGroupHandle, sourceHandles, replace, dynamic, sectionStyleHandle }, cancellationToken);
    [McpServerTool(Name = "civil_create_multiple_section_views"), Description("Crea grupo/vistas nativas Civil en retícula, con rangos, estilos y bandas. Máximo 200 vistas. Lote atómico, operationId UUID.")]
    public Task<CallToolResult> MultipleSections(string sessionId, string documentId, string operationId, string coordinateSpace, string units,
        string sampleLineGroupHandle, string namePrefix, Vec3 position, double leftWidth, double rightWidth, double elevationMin, double elevationMax,
        CancellationToken cancellationToken, int columns = 4, double? columnSpacing = null, double? rowSpacing = null,
        double? startStation = null, double? endStation = null, string? styleHandle = null, string? bandSetStyleHandle = null)
        => Call("civil_create_multiple_section_views", new { sessionId, documentId, operationId, coordinateSpace, units, sampleLineGroupHandle, namePrefix, position,
            leftWidth, rightWidth, elevationMin, elevationMax, columns, columnSpacing, rowSpacing, startStation, endStation, styleHandle, bandSetStyleHandle }, cancellationToken);
    [McpServerTool(Name = "civil_update_section_views"), Description("Actualiza rangos/estilos/bandas y regenera cortes sólidos MCP existentes por defecto. Lote atómico, operationId UUID.")]
    public Task<CallToolResult> UpdateSections(string sessionId, string documentId, string operationId, string coordinateSpace, string units, string[] sectionViewHandles,
        CancellationToken cancellationToken, double? leftWidth = null, double? rightWidth = null, double? elevationMin = null, double? elevationMax = null,
        string? styleHandle = null, string? bandSetStyleHandle = null, bool refreshSolidSections = true)
        => Call("civil_update_section_views", new { sessionId, documentId, operationId, coordinateSpace, units, sectionViewHandles, leftWidth, rightWidth,
            elevationMin, elevationMax, styleHandle, bandSetStyleHandle, refreshSolidSections }, cancellationToken);
    [McpServerTool(Name = "civil_add_solid_sections"), Description("Cortes exactos de sólidos con el motor AutoCAD en vistas Civil. Estáticos, regenerables por MCP; no son SectionProjection nativas. Máximo 1000 pares. Lote atómico, operationId UUID.")]
    public Task<CallToolResult> SolidSections(string sessionId, string documentId, string operationId, string coordinateSpace, string units, string[] sectionViewHandles,
        string[] solidHandles, CancellationToken cancellationToken, bool replaceExisting = true)
        => Call("civil_add_solid_sections", new { sessionId, documentId, operationId, coordinateSpace, units, sectionViewHandles, solidHandles, replaceExisting }, cancellationToken);
    [McpServerTool(Name = "civil_generate_cross_sections"), Description("Crea líneas perpendiculares, superficies dinámicas, grupo/vistas Civil y cortes exactos de sólidos regenerables. Máximo 200 estaciones/1000 pares. Lote atómico, operationId UUID.")]
    public Task<CallToolResult> GenerateSections(string sessionId, string documentId, string operationId, string coordinateSpace, string units, string alignmentHandle,
        string name, double interval, Vec3 position, double leftWidth, double rightWidth, double elevationMin, double elevationMax, CancellationToken cancellationToken,
        string[]? surfaceHandles = null, string[]? solidHandles = null, double? startStation = null, double? endStation = null, int columns = 4,
        double? columnSpacing = null, double? rowSpacing = null, string? styleHandle = null, string? bandSetStyleHandle = null)
        => Call("civil_generate_cross_sections", new { sessionId, documentId, operationId, coordinateSpace, units, alignmentHandle, name, interval, position,
            leftWidth, rightWidth, elevationMin, elevationMax, surfaceHandles, solidHandles, startStation, endStation, columns, columnSpacing, rowSpacing, styleHandle, bandSetStyleHandle }, cancellationToken);
}
