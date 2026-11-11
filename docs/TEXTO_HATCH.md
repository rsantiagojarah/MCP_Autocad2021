# Texto y HATCH nativos — versión 0.5.0

La versión 0.5.0 añade nueve herramientas al catálogo (80 en total) y amplía `cad_create_text`. El servidor oficial .NET 10 y el adaptador de compatibilidad .NET 8 usan el mismo catálogo. El puente sigue compilándose para .NET Framework 4.8, x64, contra AutoCAD/Civil 3D 2021.

## Herramientas

| Nombre | Función |
|---|---|
| `cad_list_fonts` | Familias TTF instaladas y archivos SHX unifont disponibles; filtro opcional `fontType=ttf/shx`. |
| `cad_list_text_styles` | Nombre, handle, archivo/familia, altura fija, factor de ancho e inclinación. |
| `cad_upsert_text_style` | Crea un estilo; requiere `updateExisting=true` para cambiar uno existente. |
| `cad_set_text_style` | Asigna `styleHandle` a `handles` de TEXT/MTEXT sin cambiar la definición compartida. |
| `cad_update_text` | Edita contenido, altura, rotación, alineación y anclaje, conservando los handles. |
| `cad_list_hatch_patterns` | Patrones encontrados en PAT disponibles, con tipo `predefined/custom`, además de SOLID. |
| `cad_create_hatch` | Crea HATCH real desde `outerHandles` e `islandHandles` opcionales. |
| `cad_get_hatch` | Consulta patrón, escala, ángulo, color, normal, elevación, asociatividad, área y bucles. |
| `cad_update_hatch` | Actualiza propiedades y, opcionalmente, sus contornos; conserva la identidad del HATCH. |

Las consultas de fuentes, estilos y patrones admiten `offset/limit` y devuelven `nextOffset`. El inventario de fuentes no instala archivos ni consulta Internet.

## Convenciones y alcance

Descubrir `sessionId` y `documentId` antes de escribir. El documento indicado debe estar activo. Toda modificación exige `operationId` UUID. Ante `PENDING/OUTCOME_UNKNOWN`, consultar el UUID original con `cad_get_request_status`; reintentar la misma operación con el mismo UUID. El historial sigue siendo de memoria y se pierde al reiniciar el puente.

Las operaciones con alturas, posiciones o parámetros geométricos requieren `coordinateSpace="WCS"` y `units` coincidente con `INSUNITS`. No existe conversión implícita. Ángulos en radianes; color mediante `colorIndex` ACI 0..256. Cada escritura usa bloqueo de documento y transacción, y respeta capas bloqueadas. Un error revierte la operación completa.

### Fuentes y estilos

`font` acepta una familia TTF instalada, como `Arial`, o un archivo `.ttf/.shx` accesible desde las rutas de soporte, la carpeta de fuentes de Windows o una ruta absoluta. Se valida la existencia y el formato. Las SHX deben ser unifont; archivos de formas y bigfont no se aceptan como fuente primaria. No se autoriza sustituir silenciosamente una familia ausente.

`fixedHeight` admite cero (altura variable) o una altura positiva; `widthFactor` está entre 0.01 y 100; `obliqueAngle` está estrictamente entre -pi/2 y pi/2. Al actualizar un estilo, las propiedades opcionales omitidas se conservan. Cambiar una definición compartida puede afectar todos los objetos que la usan: por eso se exige `updateExisting=true`. Utiliza otro nombre si necesitas una variación independiente.

Asignar un estilo no modifica su definición ni altera el contenido de textos no seleccionados. Los formatos locales de MTEXT (por ejemplo `\\f`, `\\H`) se conservan y pueden prevalecer sobre el estilo; la herramienta no elimina formato sin autorización. TEXT/MTEXT deben ser objetos directos en ModelSpace o PaperSpace; se rechazan atributos y textos dentro de definiciones de bloque para evitar cambiar todas sus instancias.

`cad_create_text` conserva sus argumentos originales y añade `styleHandle`, `type="text"/"mtext"` y `alignment`. El tipo predeterminado continúa siendo TEXT. La altura explícita prevalece en el objeto creado; la altura fija se informa en el estilo para facilitar su selección.

`cad_update_text` aplica las propiedades indicadas a todos los handles de su lote. TEXT admite hasta 256 caracteres por edición y MTEXT hasta 4096, incluido su formato nativo. Las alineaciones son `baseLeft/baseCenter/baseRight` (solo TEXT), o `topLeft/topCenter/topRight`, `middleLeft/middleCenter/middleRight`, `bottomLeft/bottomCenter/bottomRight`. `position` es el punto de anclaje WCS; si se cambia la alineación sin posición se conserva el anclaje actual. Se emplea `DBText.AdjustAlignment`.

### Sombreados

Cada handle identifica un contorno cerrado individual: Polyline ligera, Circle, Ellipse o Spline plana. Los contornos deben ser directos en ModelSpace y coplanares. La normal y la elevación se deducen del primer exterior; también se permiten planos inclinados. Las polilíneas conservan sus arcos/bulges. No se aproximan contornos con líneas independientes.

Se permiten hasta 50 exteriores y 50 islas, con un máximo total de 100 contornos y 10000 vértices por polilínea. Los exteriores deben ser disjuntos, y las islas, interiores a exactamente un exterior y disjuntas entre sí. Se rechazan intersecciones, tangencias entre contornos, anidamientos entre islas, contornos abiertos y áreas nulas. No se construyen bucles a partir de grupos de líneas/arcos abiertos: únalos previamente en una polilínea cerrada. La validación de áreas y contención utiliza regiones exactas de Autodesk.

Se utilizan `Hatch.SetHatchPattern`, `Hatch.AppendLoop` y `Hatch.EvaluateHatch`. `pattern="SOLID"` crea relleno sólido; `ANSI31` y otros patrones deben estar disponibles. El tipo predeterminado es `patternType="predefined"`; para un archivo PAT independiente usa `custom`. La escala debe ser positiva. Autodesk sigue imponiendo sus límites de densidad; un fallo de evaluación revierte el lote.

La asociatividad predeterminada es `true` y se configura antes de añadir los bucles. Los exteriores se añaden como `Outermost`, las islas como `Default`, con `HatchStyle.Normal`. Los contornos fuente se conservan.

Los HATCH creados por MCP guardan referencias blandas a sus contornos en un Xrecord. Desactivar y reactivar la asociatividad reconstruye los bucles a partir de esas referencias. Si faltan fuentes, o el HATCH no fue creado por MCP y carece de referencias recuperables, proporciona `outerHandles/islandHandles` al activar asociatividad. No se inventan contornos. La edición de patrón, escala y color no requiere esos metadatos. Modificar solamente islas requiere volver a indicar los exteriores.

## Ejemplos

Crear un estilo nuevo (sustituir IDs y unidades por los descubiertos):

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID nuevo>","coordinateSpace":"WCS","units":"Meters","name":"ROTULOS_ARIAL","font":"Arial","fixedHeight":0,"widthFactor":1,"obliqueAngle":0}
```

Asignar el handle devuelto por el estilo:

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID nuevo>","styleHandle":"<estilo>","handles":["<TEXT>","<MTEXT>"]}
```

Crear un HATCH con isla:

```json
{"sessionId":"<sesión>","documentId":"<documento>","operationId":"<UUID nuevo>","coordinateSpace":"WCS","units":"Meters","outerHandles":["<exterior>"],"islandHandles":["<isla>"],"pattern":"ANSI31","scale":1,"angle":0,"layer":"0","colorIndex":3,"associative":true}
```

## Compilar y actualizar

1. Preparar el paquete desde PowerShell normal: `./scripts/Build-TextHatchRelease.ps1`. Genera `artifacts/expanded-0.5.0` y `artifacts/CadMcp2021-0.5.0.zip`; no modifica la instalación activa.
2. Revisar el paquete sin escribir en el perfil: `./scripts/Install-TextHatchMcp.ps1 -CheckOnly`.
3. Guardar manualmente los dibujos y cerrar AutoCAD/Civil 3D. El instalador se niega a reemplazar DLL mientras Autodesk está abierto; no lo cierra ni guarda por su cuenta.
4. Ejecutar `./scripts/Install-TextHatchMcp.ps1` desde PowerShell normal, con el mismo usuario de Autodesk. Verifica hashes, respalda el puente anterior, actualiza la carga automática y registra el servidor/complemento. No modifica ACL, SECURELOAD ni TRUSTEDPATHS.
5. Abrir Autodesk, reconectar el cliente MCP o abrir un chat nuevo. Comprobar `bridgeVersion="0.5.0"`, 80 herramientas y métodos `cad.text_styles`, `cad.upsert_text_style`, `cad.create_hatch`, `cad.update_hatch`.

`-BridgeOnly` actualiza solo el puente; el cliente puede usar `artifacts/expanded-0.5.0/mcp.config.json`. El paquete conserva Newtonsoft.Json, contratos, núcleo y módulo Civil junto al puente. Las DLL de Autodesk no se redistribuyen.

## Verificación

La suite automática comprueba esquemas, compatibilidad de `cad_create_text`, UUID, deduplicación y límites. Reporte: `artifacts/test-results/text-hatch/text-hatch.trx`. La prueba stdio de cada adaptador comprueba 80 herramientas y rechazo de UUID inválidos en las nuevas escrituras.

La prueba nativa está en `native-check/CadMcp.TextHatch.NativeV2.dll`, comandos `CADMCPTEXTHATCHCHECK2` y después `CADMCPTEXTHATCHVERIFY`. Solo ejecutarla en un dibujo nuevo y vacío. Compila las mismas fuentes de producción dentro de una DLL independiente, para ensayarlas sin reemplazar un puente antiguo cargado. No cambia las capacidades del puente activo.

La prueba crea TEXT/MTEXT, asigna Arial a objetos existentes, crea una fuente `txt.shx`, edita texto y alineación conservando identidad, crea ANSI31/SOLID y una isla con contornos de áreas exactas 100 y 4, cambia propiedades/asociatividad, rechaza fuentes/patrones/contornos inválidos y comprueba reversión y deduplicación. Escribe un JSON y guarda únicamente su dibujo de ensayo junto a la DLL. La segunda orden verifica la isla después de finalizar la primera. AutoCAD 2021 puede no devolver `Hatch.Area` para un patrón: se informa expresamente, y se comprueban los bucles nativos Normal/Outermost/Default y las áreas exactas de los contornos, sin inventar un valor de esa propiedad. La variable de entorno `CADMCP_TEXT_HATCH_REPORT` permite otro destino. No ejecutar en el DWG del proyecto.

### Resultado en el puente de producción

En este equipo se completaron **35 comprobaciones nativas mediante MCP**, usando el puente 0.5.0 cargado en AutoCAD 2021. El reporte `verification/live-native-mcp-complete.json` incluye fuentes disponibles, estilos Arial/SHX, asignación y edición de TEXT/MTEXT, ANSI31/SOLID, bucles de isla, modificación de escala/ángulo/color/patrón/asociatividad, movimiento de la isla, deduplicación y reversión de errores. Se contrastó el hash del puente instalado con el del paquete. No se cerró ni guardó automáticamente el dibujo de ensayo.

La propiedad `Hatch.Area` no estuvo disponible en esa sesión. La isla se comprobó mediante dos bucles asociados nativos, con contornos de áreas 100 y 4 y área neta geométrica 96. Este resultado no se presenta como una lectura de `Hatch.Area`.

Para repetir la prueba completa sin cargar DLL de ensayo, indicar una sesión/documento activo **vacío**:

```powershell
python tests/live_text_hatch_stdio.py .tools/dotnet10/dotnet.exe artifacts/expanded-0.5.0/server/CadMcp.Server.dll --session-id '<sesión>' --document-id '<documento>' --allow-empty-drawing-write-test
```

Ejecutar desde el usuario normal de AutoCAD. La prueba deja los objetos en una capa `MCP_TEXT_HATCH_TEST_*`; no guarda ni cierra dibujos. El reporte aprobado de esta entrega se obtuvo completando la primera ejecución con `tests/live_text_hatch_followup.py`, que exige la misma sesión, documento, identificador de arranque y capa registrados; no repite la creación de objetos.

`scripts/Test-TextHatchNative.ps1` intenta ejecutarla en una instancia separada de Core Console. Este equipo presenta un fallo de inicio de Core Console; si ocurre antes de la carga de la DLL, no cuenta como una prueba nativa aprobada. El ensayo gráfico requiere que el usuario acepte cualquier aviso de carga de DLL sin firma. Los resultados reales se entregan por separado; compilar la prueba no acredita su ejecución.

## Documentación oficial consultada

- [Autodesk ObjectARX 2021: entorno y .NET Framework 4.8](https://help.autodesk.com/cloudhelp/2021/ENU/OARX-DevGuide/files/GUID-2D3DDE7F-FB92-4EFC-9912-7978B60D9963.htm).
- [Autodesk AutoCAD 2021: bucles exteriores y evaluación del HATCH](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-ActiveX-Reference/files/GUID-4CA06494-CDFF-46FA-9E1D-A0E8220F69F4.htm).
- [Autodesk AutoCAD 2021: inserción de bucles e islas](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-ActiveX-Reference/files/GUID-E35E3387-14E8-433D-B6A6-82E034A11BEE.htm).
- [Autodesk Managed .NET: creación/modificación de estilos](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-DevGuide-Managed/files/GUID-A3CA7191-A979-4FD4-8322-357C27180BB6.htm).
- [Autodesk Managed .NET: contornos HATCH y orden de asociatividad](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-DevGuide-Managed/files/GUID-01A47A4F-9FC5-4DB6-8C3E-B72D75688965.htm).
- [Autodesk Managed .NET: DBText.AdjustAlignment](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-ManagedRefGuide/files/OREFNET-Autodesk_AutoCAD_DatabaseServices_DBText_AdjustAlignment_Database.html).

Las páginas Managed .NET de las rutas 2021 probadas no fueron accesibles en la consulta web. Se contrastaron los conceptos con la documentación oficial 2021 indicada y las firmas efectivas se compilaron contra AcDbMgd/AcMgd/AcCoreMgd instaladas de **2021**. Las páginas .NET de otras ediciones se usan como referencia complementaria, sin presentar su versión como 2021.
