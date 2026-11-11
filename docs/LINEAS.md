# Grosores, tipos y escalas de línea — MCP 0.6.0

La versión 0.6.0 incorpora nueve herramientas (89 en total) para gestionar las propiedades gráficas nativas de AutoCAD 2021. Conserva las herramientas de geometría, texto, HATCH y Civil 3D de las versiones anteriores.

## Herramientas

| Herramienta | Función |
|---|---|
| `cad_list_lineweights` | Valores de grosor nativos, sus milímetros y modos de herencia; identifica los válidos para capas. |
| `cad_list_linetypes` | Tipos cargados, handles, descripción, longitud y segmentos del patrón nativo. |
| `cad_list_linetype_definitions` | Nombres/descripciones disponibles en un archivo `.LIN`; no modifica el dibujo. |
| `cad_load_linetypes` | Carga nombres explícitos desde un `.LIN`; valida el lote completo antes de incorporarlo al dibujo. |
| `cad_get_line_properties` | Consulta grosor, tipo, escala y valores heredados por capa de los handles indicados. |
| `cad_set_line_properties` | Asigna grosor, tipo, escala, capa y/o color por handles. Conserva geometría e identidad. |
| `cad_set_layer_line_properties` | Actualiza grosor, tipo y/o color de capas existentes, conservando los campos omitidos. |
| `cad_get_line_settings` | Consulta `LTSCALE`, `CELTSCALE` y visualización de grosores. |
| `cad_set_line_settings` | Cambia explícitamente esos tres ajustes del documento activo. |

Se amplían también `cad_upsert_layers` y `cad_update_entities`, sin cambiar sus argumentos existentes. `cad_upsert_layers.layers[]` admite `lineweight` y `linetype`; mantiene `colorIndex` obligatorio. Para modificar solo grosor/tipo sin volver a indicar color, usa `cad_set_layer_line_properties`.

`cad_update_entities.entities[]` admite `lineweight`, `linetype` y `linetypeScale`. Las consultas normales de entidades y capas incluyen grosor y tipo; las entidades incluyen su escala.

## Valores y herencia

`lineweight` usa **centésimas de milímetro**, como la enumeración nativa de AutoCAD. No usa las unidades geométricas del dibujo:

| Código | Resultado |
|---|---|
| `25` | 0.25 mm |
| `50` | 0.50 mm |
| `70` | 0.70 mm |
| `-1` | ByLayer: hereda el grosor de su capa. Solo entidades. |
| `-2` | ByBlock: depende del contexto de inserción. Solo entidades. |
| `-3` | Default: usa el grosor predeterminado de AutoCAD. |

Solo se aceptan los valores discretos de `cad_list_lineweights`; por ejemplo, `27` no se redondea a otro valor. El código `0` es el grosor mínimo nativo, no una orden para ocultar la línea. No se modifican `Thickness` ni el ancho geométrico de polilíneas.

`linetype` acepta el nombre de un tipo cargado, como `Continuous`, `DASHED` o `CENTER`. Las entidades también admiten `ByLayer` y `ByBlock`. Las capas requieren tipos concretos; no pueden heredarse a sí mismas. Una actualización de capa afecta a sus objetos ByLayer, y conserva las anulaciones explícitas de otros objetos.

La consulta informa las propiedades explícitas y las de la capa. Resuelve ByLayer y el grosor Default usando `LWDEFAULT`. Para ByBlock devuelve valores efectivos nulos porque no presupone un bloque de inserción. Los valores efectivos no incluyen anulaciones específicas de ventanas gráficas ni componentes de estilos Civil.

## Patrones LIN y escalas

Antes de asignar un tipo hay que cargarlo. `cad_list_linetype_definitions` y `cad_load_linetypes` aceptan `acad.lin`, `acadiso.lin` o una ruta de archivo `.LIN` disponible localmente/en las rutas de soporte. Se rechazan URL, comodines, nombres repetidos y archivos de más de 8 MiB. El listado es paginado; puede recortar descripciones a 4096 caracteres.

La carga usa `Database.LoadLineTypeFile` en una base temporal. Si cualquier definición tiene sintaxis incorrecta o falta una dependencia SHX, falla antes de modificar el dibujo real. Después clona los registros nativos al dibujo dentro de su transacción. Los tipos que ya existen se devuelven en `alreadyLoaded` y se conservan: no se redefine un patrón compartido de manera implícita. Los patrones complejos dependen de las fuentes/formas disponibles en Autodesk; no se instalan ni distribuyen esas fuentes.

`acad.lin` y `acadiso.lin` contienen longitudes diferentes. Seleccionar el archivo apropiado es explícito; el MCP no convierte patrones por el mero valor de `INSUNITS`.

- `linetypeScale`: factor positivo por entidad.
- `globalScale`: corresponde a `LTSCALE`, factor global del dibujo.
- `newEntityScale`: corresponde a `CELTSCALE`, valor predeterminado para objetos nuevos; no reescribe la escala individual de los existentes.
- `displayLineweight`: corresponde a la visualización de grosores, no a un cambio en el grosor asignado.

Las capas no tienen una escala de línea nativa propia; por eso ese parámetro solo existe en entidades/ajustes globales. `combinedLinetypeScale` informa el producto LTSCALE × escala del objeto, sin presentar ese producto como escala final de impresión. `MSLTSCALE`, `PSLTSCALE`, ventanas gráficas y tablas CTB/STB conservan su configuración y pueden afectar la apariencia o el resultado impreso.

## Ejemplos

Obtener primero `sessionId`, `documentId`, tipos cargados y capas. Cada escritura nueva necesita un UUID nuevo.

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID>","file":"acadiso.lin","names":["DASHED","CENTER"]}
```

Configurar una capa existente:

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID>","layers":["EJES"],"lineweight":25,"linetype":"CENTER"}
```

Hacer que ciertas entidades hereden sus propiedades de esa capa:

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID>","handles":["<handle>"],"layer":"EJES","lineweight":-1,"linetype":"ByLayer","linetypeScale":1}
```

Anulación explícita por entidad:

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID>","handles":["<handle>"],"lineweight":50,"linetype":"DASHED","linetypeScale":2}
```

Para copiar una norma desde objetos existentes, consultar `cad_get_line_properties` y aplicar los campos deseados a los destinos. Es posible copiar sus valores explícitos o elegir los efectivos por capa; no se modifica el objeto de referencia.

## Sesiones, transacciones y límites

Se requiere el documento activo indicado. Las escrituras conservan el protocolo `operationId` y su historial en memoria. Un reintento con el mismo UUID/contenido devuelve el resultado original; otro contenido con el mismo UUID falla. Ante `PENDING/OUTCOME_UNKNOWN`, consultar el UUID original con `cad_get_request_status`.

La lectura detallada admite hasta 500 handles; la escritura, 1000; los lotes de capas y de nombres LIN, 100. Las operaciones bloquean el documento y revierten el lote si falla un elemento. Se respetan capas bloqueadas y se rechazan dependencias de referencias externas. Las nuevas escrituras por handle requieren entidades AutoCAD directas en ModelSpace/PaperSpace; no modifican definiciones compartidas de bloques ni estilos de componentes Civil. Para Civil se conservan sus herramientas nativas de estilos.

Estas herramientas no introducen coordenadas: grosores se expresan en centésimas de mm y escalas como factores. Las herramientas geométricas existentes conservan WCS y comprobación explícita de unidades.

## Verificación y actualización

Se aprobaron **106 pruebas automáticas** y **30 comprobaciones nativas dentro de AutoCAD 2021**, mediante una DLL de ensayo que enlaza las mismas fuentes de producción. El ensayo abrió `Drawing2.dwg` y conservó el dibujo previo. Comprobó DASHED/CENTER desde `acadiso.lin`, grosores 0.25/0.35/0.70 mm, ByLayer/ByBlock, escalas individuales/globales, compatibilidad de herramientas antiguas, capas bloqueadas, deduplicación, reversión de un lote de entidades y rechazo de un LIN con una definición válida seguida de otra defectuosa. El archivo defectuoso no dejó ninguna de las dos definiciones cargada en el dibujo.

Los reportes van en `artifacts/expanded-0.6.0/verification`; el manifiesto SHA256 vincula el reporte nativo con la DLL de ensayo compilada. La ejecución nativa no reemplaza el puente antiguo ni se presenta como una prueba del puente 0.6.0 ya instalado en el cliente.

1. Compilar/preparar: `./scripts/Build-LineGraphicsRelease.ps1`. Genera `artifacts/expanded-0.6.0` y `artifacts/CadMcp2021-0.6.0.zip`.
2. Comprobar hashes sin instalar: `./scripts/Install-LineGraphicsMcp.ps1 -CheckOnly`.
3. Guardar manualmente los dibujos y cerrar AutoCAD/Civil 3D antes de actualizar. El instalador no guarda ni cierra dibujos y rechaza la instalación mientras Autodesk está abierto.
4. Ejecutar desde PowerShell normal del usuario de Autodesk: `./scripts/Install-LineGraphicsMcp.ps1`. Respalda el puente anterior y actualiza puente y complemento. `-BridgeOnly` actualiza solo el puente.
5. Abrir Autodesk y reconectar/recargar el MCP. Comprobar `bridgeVersion=0.6.0`, 89 herramientas y métodos `cad.lineweights`, `cad.load_linetypes`, `cad.set_line_properties` y `cad.set_layer_line_properties`.

La prueba nativa opcional está en `native-check/CadMcp.LineGraphics.Native.dll`, comando `CADMCPLINECHECK`. Crea un dibujo nuevo de ensayo, no guarda ni cierra dibujos, y escribe `line-graphics-native.json` junto a su DLL. Solo la aceptación manual de cualquier aviso de seguridad permite cargar una DLL sin firma. No se cambian SECURELOAD, TRUSTEDPATHS ni ACL.

## Referencias oficiales

- [AutoCAD 2021: propiedades generales y valoración gráfica](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-81585857-F1B1-44F4-B7D0-B707386CA721.htm).
- [AutoCAD 2021: propiedades de capas](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-B297EBD9-D68C-47E1-87CE-1B3798496599.htm).
- [Managed .NET: tipos de línea y carga LIN](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-DevGuide-Managed/files/GUID-81423588-A182-4511-B9D3-115014C96BCE.htm).
- [Managed .NET: Database.LoadLineTypeFile](https://help.autodesk.com/cloudhelp/2024/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Database_LoadLineTypeFile_string_string.html).

Las referencias .NET accesibles de otras ediciones complementan la documentación de producto 2021. Las firmas efectivas y el comportamiento se verificaron por compilación y ejecución contra las DLL instaladas de **AutoCAD 2021**.
