"""Generate both MCP adapters from the same explicit, reviewable tool declarations."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
S = {"type": "string", "minLength": 1, "maxLength": 1024}
N = {"type": "number"}
B = {"type": "boolean"}
P = {"type": "object", "properties": {k: N for k in ("x", "y", "z")}, "required": ["x", "y", "z"], "additionalProperties": False}
def enum(*values): return {"type": "string", "enum": list(values)}
def arr(item, minimum=1, maximum=10000): return {"type": "array", "items": item, "minItems": minimum, "maxItems": maximum}
def obj(props, required): return {"type": "object", "properties": props, "required": required, "additionalProperties": False}
def p(name, typ="string", schema=None, optional=False): return (name, typ, schema or {"string": S, "double": N, "bool": B, "Vec3": P}[typ], optional)
def handle(name): return p(name)
def points(name="points", minimum=1): return p(name, "Vec3[]", arr(P, minimum))
def handles(name="handles", maximum=1000): return p(name, "string[]", arr(S, 1, maximum))
def doubles(name): return p(name, "double[]", arr(N))
TOOLS = []
def tool(name, method, description, params=(), write=False, coords=False, page=False):
    params = list(params)
    if coords: params = [p("coordinateSpace", schema=enum("WCS")), p("units")] + params
    if page: params += [p("offset", "int", {"type": "integer", "minimum": 0, "maximum": 1000000}, True), p("limit", "int", {"type": "integer", "minimum": 1, "maximum": 500}, True)]
    if write: description += " Lote atómico; conserva operationId UUID ante reintentos."
    TOOLS.append(dict(name=name, method=method, description=description, params=params, write=write))

tool("cad_get_entity_geometry", "cad.entity_geometry", "Geometría detallada WCS: vértices/bulges paginados de polilínea, cotas, límites y volumen/centroide de sólidos.", [handle("handle")], page=True)
tool("cad_create_polyline3d", "cad.create_polyline3d", "Crea una polilínea 3D con vértices WCS y capa. No repitas el primer vértice.", [points(minimum=2), p("layer"), p("closed", "bool", optional=True)], True, True)
tool("cad_create_circle", "cad.create_circle", "Crea un círculo horizontal con centro WCS, radio positivo y capa.", [p("center", "Vec3"), p("radius", "double"), p("layer")], True, True)
tool("cad_create_text", "cad.create_text", "Crea texto DBText; altura positiva y rotación en radianes.", [p("position", "Vec3"), p("text"), p("height", "double"), p("layer"), p("rotation", "double", optional=True)], True, True)
tool("cad_create_solid", "cad.create_solid", "Crea box (position=esquina mínima, length/width/height) o cylinder (position=centro de base, radius/height), ejes WCS.", [p("type", schema=enum("box", "cylinder")), p("position", "Vec3"), p("height", "double"), p("layer"), p("length", "double", optional=True), p("width", "double", optional=True), p("radius", "double", optional=True)], True, True)
tool("cad_transform_entities", "cad.transform_entities", "Mueve, rota en Z y escala uniformemente entidades AutoCAD o copias. Orden: escala, rotación, desplazamiento; radianes; no modifica objetos Civil.", [handles(), p("displacement", "Vec3", optional=True), p("basePoint", "Vec3", optional=True), p("rotation", "double", optional=True), p("scale", "double", optional=True), p("copy", "bool", optional=True)], True, True)
tool("cad_delete_entities", "cad.delete_entities", "Elimina únicamente entidades AutoCAD por handles explícitos; no elimina objetos Civil.", [handles()], True)
tool("cad_offset_curve", "cad.offset_curve", "Crea offsets nativos de curva AutoCAD preservando la fuente; distancia con signo en unidades del dibujo.", [handle("handle"), p("distance", "double"), p("layer", optional=True)], True, True)
tool("cad_extrude_polyline", "cad.extrude_polyline", "Extruye un contorno cerrado a sólido; height con signo. Conserva la polilínea fuente.", [handle("handle"), p("height", "double"), p("layer", optional=True)], True, True)
tool("cad_boolean_solids", "cad.boolean_solids", "Unión, resta o intersección de sólidos. Modifica targetHandle y conserva los sólidos herramienta.", [handle("targetHandle"), handles("toolHandles", 100), p("operation", schema=enum("union", "subtract", "intersect"))], True)
tool("cad_create_retaining_wall", "cad.create_retaining_wall", "Crea pantalla y zapata como dos sólidos siguiendo polilínea horizontal abierta (rectas/arcos). Polilínea=eje de pantalla; talón a izquierda y puntera a derecha del recorrido. Cota fuente=coronación de zapata salvo footingTopElevation. Heel/toe se miden desde las caras de pantalla; capas creadas automáticamente. No verifica diseño estructural.", [handle("sourceHandle"), p("stemThickness", "double"), p("stemHeight", "double"), p("heel", "double"), p("toe", "double"), p("footingThickness", "double"), p("footingTopElevation", "double", optional=True), p("stemLayer", optional=True), p("footingLayer", optional=True)], True, True)
tool("cad_set_view", "cad.set_view", "Encuadra el espacio modelo en vista superior o isométrica; cambia solo la vista.", [p("orientation", schema=enum("top", "isometric"))], True)

tool("civil_list_styles", "civil.styles", "Lista estilos disponibles y handles compatibles para crear objetos; no presupone nombres de plantilla.", [p("kind", schema=enum("surface", "alignment", "profile", "profileView", "alignmentLabelSet", "profileLabelSet", "featureLine", "partsList", "assembly", "corridor", "sampleLine", "point"))], page=True)
tool("civil_list_objects", "civil.objects", "Inventario paginado de objetos Civil por familia.", [p("kind", schema=enum("sites", "corridors", "assemblies", "networks", "pointGroups", "featureLines", "parcels", "profiles", "sampleLineGroups"))], page=True)
tool("civil_get_object_info", "civil.object_info", "Detalles de superficie (estadísticas/volúmenes), eje, perfil/PVI, feature line, red/piezas, corredor/regiones, sección o parcela. Colecciones extensas paginadas.", [handle("handle")], page=True)
tool("civil_list_cogo_points", "civil.cogo_points", "Puntos COGO con número, coordenadas WCS, descripción y bloqueo.", page=True)
cogo = obj({"handle": S, "position": P, "description": {"type": "string", "maxLength": 1024}, "name": {"type": "string", "maxLength": 1024}}, ["handle"])
tool("civil_update_cogo_points", "civil.update_cogo_points", "Actualiza coordenadas, nombre o descripción de puntos COGO; rechaza puntos bloqueados o de levantamiento.", [p("points", "CogoUpdateSpec[]", arr(cogo))], True, True)
tool("civil_create_point_group", "civil.create_point_group", "Crea grupo a partir de números COGO existentes y explícitos.", [p("name"), p("pointNumbers", "uint[]", arr({"type": "integer", "minimum": 1, "maximum": 4294967295}))], True)
style = lambda: p("styleHandle", optional=True)
tool("civil_create_tin_surface", "civil.create_tin_surface", "Crea terreno TIN con al menos tres puntos WCS no colineales. styleHandle opcional.", [p("name"), points(minimum=3), style()], True, True)
tool("civil_add_surface_points", "civil.add_surface_points", "Añade vértices WCS a una superficie TIN y reconstruye.", [handle("surfaceHandle"), points()], True, True)
tool("civil_add_surface_breaklines", "civil.add_surface_breaklines", "Añade líneas de rotura estándar desde líneas, polilíneas 2D/3D o feature lines y reconstruye.", [handle("surfaceHandle"), handles("sourceHandles"), p("midOrdinateDistance", "double", optional=True)], True, True)
tool("civil_add_surface_boundary", "civil.add_surface_boundary", "Añade límite exterior, oculto o visible desde polilínea cerrada y reconstruye.", [handle("surfaceHandle"), handle("sourceHandle"), p("type", schema=enum("outer", "hide", "show")), p("midOrdinateDistance", "double", optional=True)], True, True)
tool("civil_rebuild_surface", "civil.rebuild_surface", "Reconstruye una superficie existente.", [handle("surfaceHandle")], True)
tool("civil_get_surface_triangles", "civil.surface_triangles", "Triángulos TIN visibles con tres vértices WCS, paginados.", [handle("surfaceHandle")], page=True)
tool("civil_create_volume_surface", "civil.create_volume_surface", "Crea superficie TIN de comparación y devuelve corte, relleno y balance calculados por Civil 3D.", [p("name"), handle("baseSurfaceHandle"), handle("comparisonSurfaceHandle")], True)
tool("civil_extract_surface_contours", "civil.extract_surface_contours", "Extrae curvas como entidades AutoCAD a intervalo positivo en capa; máximo 2000 niveles y 10000 curvas.", [handle("surfaceHandle"), p("interval", "double"), p("layer", optional=True)], True, True)
tool("civil_create_alignment", "civil.create_alignment", "Crea alineamiento desde polilínea abierta, conserva fuente, sin añadir curvas automáticas. Estilos omitidos=primeros disponibles.", [p("name"), handle("sourceHandle"), p("layer", optional=True), style(), p("labelSetHandle", optional=True), p("startingStation", "double", optional=True)], True, True)
sample = obj({"station": N, "offset": N}, ["station", "offset"])
tool("civil_sample_alignment", "civil.sample_alignment", "Convierte estaciones/offsets del eje a coordenadas WCS. Offset Civil: negativo izquierda, positivo derecha.", [handle("alignmentHandle"), p("samples", "StationOffsetSpec[]", arr(sample))], coords=True)
tool("civil_station_offset", "civil.station_offset", "Convierte puntos WCS en estaciones y offsets respecto al eje.", [handle("alignmentHandle"), points()], coords=True)
tool("civil_create_surface_profile", "civil.create_surface_profile", "Crea perfil de terreno desde eje y superficie; admite offset y rango de estaciones.", [p("name"), handle("alignmentHandle"), handle("surfaceHandle"), p("layer", optional=True), style(), p("labelSetHandle", optional=True), p("offset", "double", optional=True), p("startStation", "double", optional=True), p("endStation", "double", optional=True)], True, True)
pvi = obj({"station": N, "elevation": N}, ["station", "elevation"])
tool("civil_create_layout_profile", "civil.create_layout_profile", "Crea rasante por PVI crecientes unidos con tangentes; no incorpora curvas verticales automáticas.", [p("name"), handle("alignmentHandle"), p("pvis", "PviSpec[]", arr(pvi, 2, 1000)), p("layer", optional=True), style(), p("labelSetHandle", optional=True)], True, True)
tool("civil_sample_profile", "civil.sample_profile", "Consulta elevación de perfil en estaciones explícitas; distingue muestras fuera de rango.", [handle("profileHandle"), doubles("stations")], coords=True)
tool("civil_create_profile_view", "civil.create_profile_view", "Crea vista de perfil para un alineamiento en posición WCS.", [handle("alignmentHandle"), p("position", "Vec3"), p("name", optional=True), style()], True, True)
tool("civil_create_feature_line", "civil.create_feature_line", "Crea feature line desde línea, arco o polilínea 2D/3D; siteHandle opcional.", [p("name"), handle("sourceHandle"), p("siteHandle", optional=True), style()], True, True)
tool("civil_set_feature_line_elevations", "civil.set_feature_line_elevations", "Asigna a una feature line las elevaciones de una superficie.", [handle("featureLineHandle"), handle("surfaceHandle"), p("insertIntermediatePoints", "bool", optional=True)], True, True)
tool("civil_create_site", "civil.create_site", "Crea un emplazamiento Civil (Site).", [p("name")], True)
tool("civil_create_sample_lines", "civil.create_sample_lines", "Crea grupo y líneas de muestreo en estaciones, con anchos izquierdo/derecho y superficies opcionales.", [p("name"), handle("alignmentHandle"), p("stations", "double[]", arr(N, 1, 500)), p("leftWidth", "double"), p("rightWidth", "double"), p("surfaceHandles", "string[]", arr(S, 1, 100), True)], True, True)
tool("civil_list_parts", "civil.parts", "Explora familias y tamaños de lista de piezas por dominio. Primero lista familias; después indica familyHandle para sus tamaños.", [handle("partsListHandle"), p("domain", schema=enum("pipe", "structure")), p("familyHandle", optional=True)], page=True)
tool("civil_create_pipe_network", "civil.create_pipe_network", "Crea red de gravedad con lista de piezas y referencias opcionales a superficie/eje; no realiza cálculo hidráulico.", [p("name"), handle("partsListHandle"), p("surfaceHandle", optional=True), p("alignmentHandle", optional=True)], True)
pipe = obj({"familyHandle": S, "sizeHandle": S, "start": P, "end": P}, ["familyHandle", "sizeHandle", "start", "end"])
structure = obj({"familyHandle": S, "sizeHandle": S, "position": P, "rotation": N}, ["familyHandle", "sizeHandle", "position"])
tool("civil_add_network_parts", "civil.add_network_parts", "Añade tuberías rectas y estructuras desde familias/tamaños de catálogo. Coordenadas de tubería al eje central. No aplica reglas que alteren las cotas ni conecta automáticamente.", [handle("networkHandle"), p("pipes", "PipeSpec[]", arr(pipe, 0, 500), True), p("structures", "StructureSpec[]", arr(structure, 0, 500), True)], True, True)
tool("civil_connect_pipe", "civil.connect_pipe", "Conecta extremo start/end de tubería a estructura de la misma red sin forzar desconexiones.", [handle("pipeHandle"), handle("structureHandle"), p("end", schema=enum("start", "end"))], True)
tool("civil_update_pipe", "civil.update_pipe", "Actualiza extremos al eje central de tubería con extremos libres. Devuelve pendiente y cotas.", [handle("pipeHandle"), p("start", "Vec3"), p("end", "Vec3")], True, True)
tool("civil_create_corridor", "civil.create_corridor", "Crea corredor, línea base y región desde eje/rasante y ensamblaje existente; reconstruye. El ensamblaje debe tener subensamblajes y objetivos resueltos.", [p("name"), handle("alignmentHandle"), handle("profileHandle"), handle("assemblyHandle"), p("baselineName", optional=True), p("regionName", optional=True), p("startStation", "double", optional=True), p("endStation", "double", optional=True)], True, True)
tool("civil_rebuild_corridor", "civil.rebuild_corridor", "Reconstruye un corredor existente.", [handle("corridorHandle")], True)
tool("civil_add_surface_point_groups", "civil.add_surface_point_groups", "Añade grupos de puntos COGO a la definición de una superficie TIN y reconstruye.", [handle("surfaceHandle"), handles("pointGroupHandles", 100)], True)
tool("civil_add_profile_pvi", "civil.add_profile_pvi", "Añade PVI con tangente, parábola simétrica/asimétrica o arco circular a una rasante FG. Parámetros condicionales: curveLength; beforeLength/afterLength; radius.", [handle("profileHandle"), p("station", "double"), p("elevation", "double"), p("type", schema=enum("tangent", "symmetricParabola", "asymmetricParabola", "circular")), p("curveLength", "double", optional=True), p("beforeLength", "double", optional=True), p("afterLength", "double", optional=True), p("radius", "double", optional=True)], True, True)
tool("civil_create_section_view", "civil.create_section_view", "Crea vista de sección transversal para una línea de muestreo.", [p("name"), handle("sampleLineHandle"), p("position", "Vec3")], True, True)
tool("civil_update_structure", "civil.update_structure", "Ajusta cotas de tapa/fondo de estructura de red. Al indicar tapa desactiva su ajuste automático a superficie.", [handle("structureHandle"), p("rimElevation", "double", optional=True), p("sumpElevation", "double", optional=True)], True, True)
tool("civil_create_corridor_surface", "civil.create_corridor_surface", "Crea superficie de corredor por códigos de enlace existentes y límite de extensión opcional.", [handle("corridorHandle"), p("name"), p("linkCodes", "string[]", arr(S, 1, 100)), p("addExtentsBoundary", "bool", optional=True)], True)
target = obj({"index": {"type": "integer", "minimum": 0, "maximum": 1000000}, "targetHandles": arr(S, 0, 1000)}, ["index", "targetHandles"])
tool("civil_set_corridor_targets", "civil.set_corridor_targets", "Asigna objetivos por los índices devueltos por civil_get_object_info. Consulta índices otra vez si cambia la estructura del corredor. Lista de handles vacía libera ese objetivo.", [handle("corridorHandle"), p("targets", "CorridorTargetSpec[]", arr(target, 1, 1000))], True)
tool("civil_set_corridor_frequency", "civil.set_corridor_frequency", "Configura el intervalo de aplicación de ensamblajes en una región de corredor y reconstruye.", [handle("corridorHandle"), p("baselineName"), p("regionName"), p("interval", "double")], True, True)
tool("civil_update_object_style", "civil.update_object_style", "Asigna un estilo compatible a superficie, eje, perfil, vista, punto, feature line, corredor o línea de muestreo.", [handle("handle"), handle("styleHandle")], True)

def cs_string(value): return json.dumps(value, ensure_ascii=False)
catalog = ['// Generated by scripts/generate_extended_tools.py. Edit declarations there.', 'using System.Collections.Generic;', 'using Newtonsoft.Json.Linq;', 'namespace CadMcp.Server;', 'public static partial class ToolCatalog', '{', '    private static IEnumerable<ToolDefinition> Extended()', '    {']
official = ['// Generated by scripts/generate_extended_tools.py. Edit declarations there.', 'using System.ComponentModel;', 'using System.Threading;', 'using System.Threading.Tasks;', 'using CadMcp.Contracts;', 'using ModelContextProtocol.Protocol;', 'using ModelContextProtocol.Server;', 'namespace CadMcp.Server;', 'public sealed partial class OfficialTools', '{']
manifest = []
for index, t in enumerate(TOOLS):
    schema = {name: schema for name, typ, schema, optional in t['params']}
    required = [name for name, typ, schema, optional in t['params'] if not optional]
    catalog.append(f'        yield return Tool({cs_string(t["name"])}, {cs_string(t["method"])}, {cs_string(t["description"])}, JObject.Parse(@"{json.dumps(schema, ensure_ascii=False).replace(chr(34), chr(34)*2)}"), new string[] {{ {", ".join(cs_string(n) for n in required)} }}, mutation: {str(t["write"]).lower()});')
    args = ['string sessionId', 'string documentId']
    names = ['sessionId', 'documentId']
    if t['write']: args.append('string operationId'); names.append('operationId')
    args.append('CancellationToken cancellationToken')
    for name, typ, schema, optional in sorted(t['params'], key=lambda p: p[3]):
        if optional:
            default = '0' if name == 'offset' and typ == 'int' else '100' if name == 'limit' and typ == 'int' else 'null'
            args.append(f'{typ if default != "null" else typ+"?"} {name} = {default}')
        else: args.append(f'{typ} {name}')
        names.append(name)
    official += [f'    [McpServerTool(Name = {cs_string(t["name"])}), Description({cs_string(t["description"])})]', f'    public Task<CallToolResult> Extended{index}({", ".join(args)}) => Call({cs_string(t["name"])}, new {{ {", ".join(names)} }}, cancellationToken);']
    manifest.append({k: t[k] for k in ('name', 'method', 'description', 'write')})
catalog += ['    }', '}']
official += ['}']
(ROOT/'src/CadMcp.Server/ExtendedToolCatalog.cs').write_text('\n'.join(catalog)+'\n', encoding='utf-8')
(ROOT/'src/CadMcp.Server/ExtendedOfficialTools.cs').write_text('\n'.join(official)+'\n', encoding='utf-8')
(ROOT/'examples/extended-tools.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print(f'Generated {len(TOOLS)} new tools; total {13+len(TOOLS)}.')
