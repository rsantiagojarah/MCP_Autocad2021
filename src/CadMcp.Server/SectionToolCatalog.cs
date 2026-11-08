using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CadMcp.Server;

public static partial class ToolCatalog
{
    private static IEnumerable<ToolDefinition> SectionTools()
    {
        JObject Number() => new JObject { ["type"] = "number" };
        JObject Boolean() => new JObject { ["type"] = "boolean" };
        JObject Coordinates() => new JObject { ["coordinateSpace"] = new JObject { ["type"] = "string", ["enum"] = new JArray("WCS") }, ["units"] = String() };
        JObject ViewOptions()
        {
            var a = Coordinates();
            foreach (var key in new[] { "leftWidth", "rightWidth", "elevationMin", "elevationMax", "columnSpacing", "rowSpacing" }) a[key] = Number();
            a["columns"] = Integer(1, 50); a["position"] = Point();
            a["styleHandle"] = String(); a["bandSetStyleHandle"] = String();
            return a;
        }
        yield return Tool("civil_get_section_sources", "civil.section_sources", "Lista fuentes disponibles/muestreadas y modo dinámico de un grupo de líneas de muestreo.",
            new JObject { ["sampleLineGroupHandle"] = String() }, new[] { "sampleLineGroupHandle" });
        yield return Tool("civil_set_section_sources", "civil.set_section_sources", "Activa fuentes de superficie/corredor disponibles en el grupo. replace=false conserva las otras; dynamic=true activa actualización Civil. Lote atómico con operationId UUID.",
            new JObject { ["sampleLineGroupHandle"] = String(), ["sourceHandles"] = Array(String(), 0, 100), ["replace"] = Boolean(), ["dynamic"] = Boolean(), ["sectionStyleHandle"] = String() },
            new[] { "sampleLineGroupHandle", "sourceHandles" }, mutation: true);
        var multiple = ViewOptions(); multiple["sampleLineGroupHandle"] = String(); multiple["namePrefix"] = String();
        multiple["startStation"] = Number(); multiple["endStation"] = Number();
        yield return Tool("civil_create_multiple_section_views", "civil.create_multiple_section_views", "Crea grupo nativo SectionViewGroup y vistas en retícula con rangos, estilo y bandas. Máximo 200 vistas. Lote atómico con operationId UUID.", multiple,
            new[] { "coordinateSpace", "units", "sampleLineGroupHandle", "namePrefix", "position", "leftWidth", "rightWidth", "elevationMin", "elevationMax" }, mutation: true);
        var update = Coordinates(); update["sectionViewHandles"] = Array(String(), 1, 200);
        foreach (var key in new[] { "leftWidth", "rightWidth", "elevationMin", "elevationMax" }) update[key] = Number();
        update["styleHandle"] = String(); update["bandSetStyleHandle"] = String(); update["refreshSolidSections"] = Boolean();
        yield return Tool("civil_update_section_views", "civil.update_section_views", "Actualiza rangos/estilo/bandas de vistas nativas. Por defecto regenera sus cortes sólidos administrados por MCP. No borra geometría ajena. Lote atómico con operationId UUID.", update,
            new[] { "coordinateSpace", "units", "sectionViewHandles" }, mutation: true);
        var solids = Coordinates(); solids["sectionViewHandles"] = Array(String(), 1, 200); solids["solidHandles"] = Array(String(), 1, 100);
        solids["replaceExisting"] = Boolean();
        yield return Tool("civil_add_solid_sections", "civil.add_solid_sections", "Corta sólidos con Solid3d.GetSection en el plano vertical de cada línea de muestreo y los coloca en vistas Civil. Recorta al rango visible. Cortes exactos estáticos, regenerables por MCP; no crea SectionProjection nativa. Máximo 1000 pares. Lote atómico con operationId UUID.", solids,
            new[] { "coordinateSpace", "units", "sectionViewHandles", "solidHandles" }, mutation: true);
        var generate = ViewOptions(); generate["alignmentHandle"] = String(); generate["name"] = String(); generate["interval"] = Number();
        generate["startStation"] = Number(); generate["endStation"] = Number(); generate["surfaceHandles"] = Array(String(), 0, 100); generate["solidHandles"] = Array(String(), 0, 100);
        yield return Tool("civil_generate_cross_sections", "civil.generate_cross_sections", "Una operación: líneas perpendiculares, superficies dinámicas, grupo/vistas nativas, rangos, estilos/bandas y cortes exactos de sólidos. Superficies dinámicas; sólidos regenerables por MCP. Máximo 200 estaciones/1000 pares. Lote atómico con operationId UUID.", generate,
            new[] { "coordinateSpace", "units", "alignmentHandle", "name", "interval", "position", "leftWidth", "rightWidth", "elevationMin", "elevationMax" }, mutation: true);
    }
}
