# Secciones nativas y cortes de sólidos — MCP 0.4.0

Esta versión añade seis herramientas y eleva el catálogo de 65 a **71**. Se compila contra las DLL instaladas de AutoCAD/Civil 3D 2021. La compilación y las pruebas del protocolo no sustituyen la prueba dentro de Autodesk.

## Herramientas

| Herramienta | Resultado |
|---|---|
| `civil_get_section_sources` | Fuentes disponibles y muestreadas de un grupo, tipos, estilos y modo de actualización. |
| `civil_set_section_sources` | Activa fuentes disponibles. `replace=false` conserva las demás; `replace=true` sustituye la selección; `dynamic=true` configura actualización Civil. Una lista vacía con `replace=true` desactiva todas. |
| `civil_create_multiple_section_views` | Crea un **SectionViewGroup nativo** y sus SectionView, en retícula, desde líneas existentes. Configura rangos, estilo de vista y conjunto de bandas. |
| `civil_update_section_views` | Modifica rangos, estilo y bandas. Regenera los cortes sólidos MCP existentes por defecto. `refreshSolidSections=false` conserva los contornos anteriores y devuelve una advertencia. |
| `civil_add_solid_sections` | Intersecta sólidos con el plano vertical de cada línea recta de muestreo usando `Solid3d.GetSection`, recorta al rango visible y transforma los contornos con la correspondencia nativa offset/elevación de SectionView. |
| `civil_generate_cross_sections` | Crea líneas perpendiculares, configura superficies dinámicas, crea grupo/vistas nativas y añade los cortes sólidos en una sola transacción. |

También se amplían `civil_list_styles` con `section`, `sectionView`, `sectionViewBandSet` y `groupPlot`; `civil_list_objects` con `sections` y `sectionViews`; y `civil_get_object_info` con rangos, ubicación y metadatos de cortes de cada SectionView. La consulta de SampleLine devuelve sus vértices y vistas.

## Diferencia entre superficies y sólidos

Las superficies usan fuentes y objetos Section **nativos de Civil**, configurados en modo `Dynamic`.

La API pública .NET de Civil 3D 2021 no expone un constructor o método `Create` para `SectionProjection`. Por ello, los sólidos se cortan con el motor geométrico de AutoCAD y sus contornos se colocan dentro de vistas Civil. **No son cruces `<Section>` nativos ni se actualizan automáticamente al modificar el sólido o mover la vista.** Para regenerarlos se llama a `civil_update_section_views` o a `civil_add_solid_sections` con `replaceExisting=true`. No se automatizan diálogos ni se ejecutan comandos arbitrarios.

Las curvas originales se conservan como curvas exactas. Si una transformación con exageración vertical requiere convertir un arco a spline, se usa la representación spline de la API, sin discretización por puntos.

Los contornos creados se registran en Xrecords de la vista y se marcan individualmente con su vista propietaria. La regeneración elimina únicamente los contornos que mantienen esa marca. Los contornos ya borrados se omiten. Metadatos alterados, capas bloqueadas o fuentes ausentes provocan un fallo y la reversión de la transacción. No se borran los sólidos fuente.

## Ejemplo de generación

Obtén `sessionId`, `documentId`, los handles de eje/fuentes y las unidades del dibujo. Cada modificación nueva requiere un UUID nuevo; conserva el mismo UUID si el resultado de una llamada es incierto.

```json
{
  "sessionId": "<de cad_list_sessions>",
  "documentId": "<documento activo>",
  "operationId": "<UUID nuevo>",
  "coordinateSpace": "WCS",
  "units": "Meters",
  "alignmentHandle": "<eje>",
  "name": "SECCIONES_MURO",
  "startStation": 0,
  "endStation": 15,
  "interval": 1,
  "surfaceHandles": [],
  "solidHandles": ["<pantalla>", "<zapata>"],
  "leftWidth": 5,
  "rightWidth": 5,
  "elevationMin": -1,
  "elevationMax": 4,
  "position": {"x": 1100, "y": 1200, "z": 0},
  "columns": 4
}
```

Los dos extremos se incluyen una sola vez. Si el intervalo no divide el rango, se incluye una última sección en `endStation`. Los anchos son magnitudes positivas; el offset izquierdo de la vista es negativo. En un vértice sin curva se emplea la normal que devuelve Civil para esa estación.

Los parámetros opcionales `styleHandle` y `bandSetStyleHandle` deben proceder de `civil_list_styles`. Las bandas se importan del conjunto seleccionado; su contenido y configuración dependen de la plantilla. `columnSpacing` y `rowSpacing` permiten controlar la retícula. En su ausencia se calcula el espacio de la geometría usando la escala real de la vista y se añade un margen de 15 unidades; plantillas con rótulos o bandas muy extensos pueden necesitar más separación.

## Límites y casos de borde

- Máximo 200 secciones, 1000 combinaciones vista/sólido y 20000 curvas de corte por operación.
- Sólidos directos en ModelSpace; no sólidos anidados en bloques ni mallas.
- Las líneas de muestreo de los cortes sólidos deben ser rectas y pasar por el eje.
- Los cortes se recortan a los límites de offsets y elevaciones de la vista.
- Una intersección nula, tangencial o coincidente con una cara terminal puede no producir una Region válida. Se informa `no_valid_region`; no se desplaza el plano ni se inventa un contorno. Consulta los resultados por sólido, especialmente en los extremos del eje.
- Los errores revierten la operación completa. Las respuestas `PENDING`/`OUTCOME_UNKNOWN` requieren consultar el UUID original con `cad_get_request_status`.

## Instalación y validación

Paquete: `artifacts/expanded-0.4.0`. No reemplaza DLL cargadas en Autodesk ni cambia el dibujo abierto durante su preparación.

1. Guarda los dibujos y cierra Civil 3D/AutoCAD.
2. Desde PowerShell 7 normal, con el usuario de Autodesk, ejecuta `scripts/Install-ExpandedMcp.ps1`.
3. Abre Civil 3D y un chat nuevo de Codex. Comprueba `bridgeVersion=0.4.0`, 71 herramientas y los seis métodos nuevos.

La prueba nativa preparada está en `native-check/CadMcp.Civil3D.Smoke.dll`. En un dibujo **nuevo vacío**, cargar esa DLL y ejecutar `CADMCPSECTIONCHECK` crea un TIN, un eje, una caja y tres vistas; comprueba áreas exactas, recorte de elevaciones, refresco sin duplicados, preservación de fuentes/geometría ajena y reversión de un lote fallido. Escribe `section-native-check.json` junto a la DLL. No ejecutar en el DWG del proyecto. Esa prueba debe ejecutarse después de cargar el puente nuevo: compilarla no significa que haya pasado.

Referencias: [SectionView de Civil 3D 2021](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-API/files/html/34ad3acf-6df0-2633-6571-863d93c914ba.htm), [miembros públicos de SectionProjection 2021](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-API/files/html/8b722110-4530-9898-a1b4-8f0dd6ff7c7a.htm), [SetOffsetRange](https://help.autodesk.com/cloudhelp/2022/ENU/Civil3D-API/files/html/54e23899-8cb6-0642-de84-afcceaa0e518.htm), [Solid3d.GetSection](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid3d_GetSection_Plane.html). Las firmas efectivas se verificaron por reflexión y compilación contra las DLL 2021 locales.
