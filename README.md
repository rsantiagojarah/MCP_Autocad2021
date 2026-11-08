# CadMcp2021

Servidor MCP local y plugins en C# para AutoCAD y Civil 3D 2021, versión 0.4.0 preparada.

El catálogo incorpora **71 herramientas** para geometría y sólidos 3D, muros, COGO, TIN y volúmenes, alineamientos, perfiles y secciones, redes de gravedad y corredores. Las seis herramientas nuevas permiten fuentes dinámicas, grupos/vistas nativas de sección y cortes exactos de sólidos regenerables por MCP. Véase [docs/SECCIONES.md](docs/SECCIONES.md) para sus límites y [docs/FUNCIONALIDADES.md](docs/FUNCIONALIDADES.md) para el catálogo anterior.

Paquete nuevo: `artifacts/expanded-0.4.0`. La instalación en uso conserva el puente anterior hasta actualizar y reiniciar Autodesk. Las pruebas automáticas de protocolo/validación no sustituyen la comprobación nativa de esta ampliación en Civil 3D. Los cortes de sólidos requieren regeneración explícita; no se presentan como proyecciones nativas de actualización automática.

## Antecedentes de verificación (versión anterior)

- Plugins AutoCAD y Civil 3D compilados contra las DLL 2021 instaladas, sin advertencias.
- Núcleo, canal Named Pipes y adaptador MCP de compatibilidad comprobados con pruebas automatizadas.
- Servidor oficial .NET 10 compilado y publicado en `artifacts/official-net10/server`, con SDK MCP para C# 1.2.0. Pasaron las 26 pruebas y la comprobación stdio con 13 herramientas, consulta del dibujo activo y enumeración de superficies Civil 3D.
- El complemento 0.2.0 con el servidor oficial .NET 10 está instalado en el perfil normal del usuario. Se verificaron la configuración del host y los hashes de sus 14 archivos publicados. Falta confirmar la reconexión de herramientas tras recargar el cliente.
- El complemento está instalado y habilitado. Las llamadas directas desde Codex comprobaron documentos, unidades, capas, filtros y paginación; en un dibujo vacío también pasaron creación, actualización, reversión de un lote fallido, deduplicación, conflicto de UUID y rechazo de unidades incorrectas.
- Los lotes de 100, 1000 y 10000 puntos se crearon y se verificaron 11100 handles únicos. El reporte distingue ejecución dentro del plugin de la duración completa de la llamada. Son mediciones individuales sobre el dibujo de ensayo, no estadísticas generales de creación.
- AutoCAD Core Console sigue abortando al inicializar su perfil con `Problem with setting up current profile`. Las comprobaciones funcionales de AutoCAD se ejecutaron por MCP. Civil 3D pasó creación COGO, deduplicación, elevaciones de una superficie de referencia, muestra exterior y reversión de inserción COGO.

El estado consolidado está en `artifacts/test-results/verification-summary.json`. Las escrituras y los lotes se comprobaron con el servidor de compatibilidad; el servidor oficial pasó las pruebas del núcleo y consultas en vivo. Los dibujos grandes, terrenos de producción y perfiles/alineamientos reales requieren comprobaciones adicionales.

## Arquitectura

```text
Cliente MCP
    | stdio
CadMcp.Server (.NET 10 oficial / .NET 8 compatibilidad)
    | Named Pipes con longitud prefijada + JSON, máximo 4 MiB
CadMcp.AutoCAD (.NET Framework 4.8, x64)
    | Cola drenada en Application.Idle, documentos bloqueados y transacciones
    +-- Herramientas AutoCAD
    +-- CadMcp.Civil3D, cargado si AeccDbMgd ya pertenece a la sesión
```

Los contratos y el núcleo se compilan para net48 y la plataforma moderna seleccionada. Esto permite comprobar ambos destinos sin necesitar el paquete de referencias de .NET Standard 2.0 en el entorno sin red.

El proceso externo nunca abre objetos de Autodesk. Cada operación sobre un dibujo se realiza en el hilo principal de AutoCAD. El módulo Civil no se carga por la mera existencia de una carpeta C3D: exige que la biblioteca Civil esté cargada en esa sesión.

## Herramientas originales (compatibilidad)

| Herramienta | Función |
|---|---|
| `cad_list_sessions` | Descubre puentes del usuario actual, capacidades e identificador de arranque. |
| `cad_list_documents` | Lista documentos abiertos y sus UUID temporales. |
| `cad_get_document_info` | Unidades, estado, identificación y matriz UCS a WCS. |
| `cad_list_layers` | Capas, colores y estado, con paginación. |
| `cad_query_entities` | Consulta ModelSpace por capa, tipo DXF o caja WCS; devuelve propiedades, longitudes y áreas compatibles. |
| `cad_upsert_layers` | Crea capas o actualiza su color ACI por lote. |
| `cad_create_entities` | Crea puntos, líneas y polilíneas 2D por lote. |
| `cad_update_entities` | Actualiza capa o color por handle. |
| `cad_get_request_status` | Consulta estado y resultado de una solicitud. |
| `civil_list_surfaces` | Superficies y handles, con paginación. |
| `civil_list_alignments` | Alineamientos, estaciones y hasta 100 perfiles por alineamiento. |
| `civil_get_surface_elevations` | Muestreo de elevaciones por lote; distingue puntos fuera de superficie. |
| `civil_create_cogo_points` | Puntos COGO por lote con descripción común y numeración de Civil 3D. |

El catálogo contiene herramientas Civil en ambos adaptadores. Antes de invocarlas, comprueba `methods`/`civil3d` de la sesión: AutoCAD básico devolverá `UNSUPPORTED_OPERATION`.

## Compilar

Requisitos normales: Windows x64, SDK .NET 10, Developer Pack .NET Framework 4.8 o paquetes de referencia NuGet, AutoCAD/Civil 3D 2021 y acceso a NuGet.

Desde PowerShell, en esta carpeta:

```powershell
./scripts/build.ps1
```

El script compila los plugins, ejecuta pruebas y publica en `artifacts/official-net10`.

Para reproducir la compilación sin red utilizada en este equipo, con SDK .NET 8, Framework instalado y los paquetes del proyecto ya presentes en la caché NuGet:

```powershell
./scripts/build.ps1 -Offline
```

Esto genera `artifacts/compatibility-net8`. La compilación offline utiliza los ensamblados del Framework instalado como referencias, exclusivamente como alternativa local; para una distribución oficial se prefieren las referencias del Developer Pack/paquete oficial.

Si Autodesk está en otra ubicación:

```powershell
./scripts/build.ps1 -AutoCADDir 'D:\Autodesk\AutoCAD 2021'
```

Los binarios de Autodesk se usan como referencias con `Copy Local=false`; no se distribuyen en el paquete.

## Cargar el plugin y conectar el cliente

1. Abre AutoCAD o Civil 3D 2021 con un dibujo de prueba.
2. Ejecuta `NETLOAD` y selecciona `CadMcp.AutoCAD.dll` dentro de `artifacts/<variante>/CadMcp2021.bundle/Contents/Windows`. Mantén las DLL acompañantes en la misma carpeta.
3. Si AutoCAD exige una ubicación de confianza, utiliza una ubicación de plugins que ya tengas autorizada o registra esta carpeta según tu configuración. El proyecto no desactiva `SECURELOAD`.
4. Ejecuta `CADMCPSTATUS`. `CADMCPSTART` inicia el puente si está detenido; `CADMCPSTOP` lo detiene.
5. En tu cliente MCP, usa el contenido de `artifacts/<variante>/mcp.config.json`. El archivo contiene una configuración genérica `mcpServers`; la ubicación y el formato final dependen del cliente.
6. Invoca `cad_list_sessions`, `cad_list_documents` y `cad_get_document_info` antes de modificar geometría.

Para verificar manualmente que el servidor arranca:

```powershell
./scripts/start-server.ps1 -Compatibility
```

El servidor espera mensajes MCP por stdin. Que no imprima un menú es normal; stdout contiene únicamente mensajes JSON-RPC. Para la variante oficial, omite `-Compatibility`.

El `.bundle` también se puede colocar en una carpeta `ApplicationPlugins` admitida por Autodesk para disponer de los comandos de carga automática. Ese paso no lo ejecuta el script de compilación.

## Flujo de uso

### Reparación permanente de la conexión local

El instalador compara las versiones de las CLI instaladas (incluidos los binarios versionados del escritorio y el comando de PATH) y usa la más reciente. El archivo sin versión `OpenAI/Codex/bin/codex.exe` puede pertenecer a una instalación antigua y rechazar opciones que la aplicación actual acepta, como `service_tier = "default"`. Antes de registrar el MCP, se valida que la CLI elegida pueda leer la configuración; un error de lectura detiene la reparación sin intentar agregar un servidor. Esta selección conserva las preferencias de modelo y servicio del usuario.

Desde **PowerShell normal, con el mismo usuario de Windows que Civil 3D**, ejecuta:

```powershell
& 'C:\Users\rsant\Desktop\ProyectosPy\MCP_Autocad2021\scripts\Repair-CadConnection.ps1'
```

El instalador registra el servidor oficial .NET 10 directamente en la configuración de usuario de Codex, usando el host local `.tools/dotnet10/dotnet.exe` y la CLI del escritorio. Conserva una copia de la configuración antes del registro y no reemplaza un servidor existente con otro destino. También instala el paquete Autodesk en `%APPDATA%/Autodesk/ApplicationPlugins/CadMcp2021.bundle` con carga al inicio: después de reiniciar Autodesk no es necesario repetir `NETLOAD`. No guarda ni cierra dibujos, no cambia ACL, `SECURELOAD` ni el aislamiento de Codex. Si una DLL diferente está en uso, pide cerrar Autodesk antes de actualizarla.

Si hay un único puente del mismo usuario, hace consultas de lectura para verificarlo. La instalación y la conexión se informan por separado en `artifacts/test-results/connection-repair.json`. Guarda los dibujos y reinicia Civil 3D para activar la carga automática; reinicia Codex y abre un chat nuevo para que aparezcan `cad_list_sessions` y `cad_query_entities`. La conexión desde el chat nuevo requiere una comprobación adicional: una consulta stdio correcta desde PowerShell no demuestra por sí sola que el cliente haya cargado las herramientas.

Para diagnosticar sin modificar la configuración ni instalar archivos:

```powershell
./scripts/Repair-CadConnection.ps1 -CheckOnly
```

`cad_list_sessions` distingue `connected`, `bridge_not_started`, `bridge_unavailable` y `windows_user_mismatch`. Puede contar puentes de otros usuarios para el diagnóstico, pero nunca se conecta a ellos ni permite usar sus identificadores. Ejecutar el servidor manualmente desde una terminal `CodexSandboxOffline` no equivale a usar el MCP registrado: el puente rechaza esa identidad. Usa las herramientas MCP del cliente iniciado con el usuario de Autodesk. Si no aparecen en el chat nuevo, revisa el estado del servidor en la configuración MCP de Codex.

### Comprobación interactiva y registro en Codex

Para que aparezca como **complemento** en ChatGPT de escritorio, registra el paquete y su catálogo desde tu PowerShell normal:

```powershell
& 'C:\Users\rsant\Desktop\ProyectosPy\MCP_Autocad2021\scripts\Build-DesktopPlugin.ps1' -Install
```

Después reinicia ChatGPT de escritorio. En Complementos selecciona la fuente **Herramientas CAD locales** y busca **AutoCAD y Civil 3D 2021**. La instalación por CLI informa `installedPath`; esa ruta debe pertenecer al perfil con el que ejecutas ChatGPT, no a `CodexSandboxOffline`.

`codex mcp add` registra una conexión MCP directa; no crea por sí mismo una ficha en el directorio de complementos. El paquete de escritorio está definido en `packaging/chatgpt-plugin` y se genera en `desktop-marketplace`. Incluye el servidor .NET 8, manifiestos portable/compatibilidad y catálogo local; no publica nada en Internet. [Empaquetado e instalación local](https://developers.openai.com/plugins/build/plugins).

La consola del agente puede ejecutarse con un usuario aislado (`CodexSandboxOffline`), mientras AutoCAD se ejecuta con tu usuario normal. Las tuberías se limitan al mismo usuario: en ese caso ejecuta la validación desde tu PowerShell normal. No se amplían las ACL para evitar esa separación.

1. En AutoCAD o Civil 3D 2021, crea un dibujo de prueba con ModelSpace vacío y carga mediante `NETLOAD` el plugin publicado. Comprueba `CADMCPSTATUS`.
2. Ejecuta desde PowerShell normal:

```powershell
& 'C:\Users\rsant\Desktop\ProyectosPy\MCP_Autocad2021\scripts\Check-Live.ps1' -AllowEmptyDrawingWriteTest -RegisterCodex
```

El script verifica conexión, unidades, capas, creación de tres entidades, longitud 5, área 100, deduplicación, estado y reversión de un lote fallido. Si la sesión tiene el módulo Civil, consulta superficies/alineamientos y crea dos puntos COGO. Deja la geometría de prueba en el dibujo y no lo guarda. Rechaza la prueba de escritura si ModelSpace contiene entidades; no considera que un dibujo vacío carezca de contenido en PaperSpace, por lo que debes usar un dibujo de ensayo nuevo.

Sin `-AllowEmptyDrawingWriteTest`, hace solo consultas. Si hay varios puentes, solicita `-SessionId`. El reporte se guarda en `artifacts/test-results/live-check.json`. Las elevaciones de superficies deben contrastarse después con coordenadas de resultado conocido; esa prueba no queda cubierta por este script.

`-RegisterCodex` registra el servidor tras superar las comprobaciones. También puedes ejecutar `scripts/Register-CodexMcp.ps1` por separado. El registro escribe en la configuración de usuario de Codex, fuera de este proyecto; conserva cualquier servidor existente con el mismo nombre y otra configuración. Abre una sesión nueva de Codex para comprobar que aparecen las herramientas. El fragmento de referencia es `examples/codex-config.toml`.

La configuración TOML de Codex utiliza `[mcp_servers.<nombre>]`; el JSON genérico `mcpServers` no se importa directamente como `config.toml`. [Documentación oficial de OpenAI](https://developers.openai.com/codex/mcp/).

Ejemplo de instrucción una vez conectado: «Consulta el documento activo y sus unidades. Crea en la capa 0 una línea desde (0,0,0) hasta (3,4,0), en WCS, y comprueba su longitud».

`examples/create-geometry.json` contiene los argumentos de un lote. Sustituye la sesión y el documento con los valores descubiertos y confirma las unidades. El UUID de ese ejemplo debe cambiar para cada modificación NUEVA. Para comprobar el mismo lote tras una desconexión, conserva su UUID original.

Reglas de la primera versión:

- Las operaciones del dibujo requieren que el `documentId` indicado esté activo. No se cambia automáticamente de documento.
- Los UUID de documento duran hasta que el documento se cierre. Los handles identifican objetos dentro de ese dibujo.
- Las coordenadas se interpretan exclusivamente en WCS. `units` debe coincidir con `Database.Insunits` (`Meters`, `Millimeters`, `Unitless`, etc.). No existe conversión implícita.
- Las polilíneas son 2D, con Z constante. Para cerrarlas utiliza `closed=true`, sin repetir el primer punto.
- Las capas de destino deben existir y estar desbloqueadas. Se pueden crear con `cad_upsert_layers`.
- Los lotes AutoCAD usan una transacción por operación. Si falla una entidad, se aborta el lote; no se solicita guardar el DWG.
- Los lotes COGO usan la API de colección y una transacción externa; su comportamiento de reversión debe comprobarse en Civil 3D 2021 antes de usarlos en proyectos reales.
- Las consultas devuelven hasta 500 entidades y examinan hasta 20000 candidatos por página, además del recorrido hasta el offset. El contenido por página se limita aproximadamente a 3 MiB. Repite usando `nextOffset` hasta null. No alteres el dibujo entre páginas; los offsets no son instantáneas. Los textos se limitan a 4096 caracteres y los atributos a 100 por bloque, con indicadores de truncamiento.
- El número de perfiles por alineamiento se limita a 100 y se informa si se truncó. La ampliación de esa consulta forma parte de futuras versiones.

## Reintentos, espera y errores

Cada modificación requiere `operationId` (UUID). El puente conserva hasta 10000 modificaciones, con un presupuesto de 64 MiB para sus respuestas serializadas. Antes de aceptar una modificación reserva espacio para una respuesta máxima de 4 MiB. El mismo UUID y contenido devuelven el resultado original; otro contenido con el mismo UUID devuelve `REQUEST_ID_CONFLICT`. Las consultas completadas se liberan del historial para evitar acumular datos de dibujos en memoria; sus resultados no permanecen disponibles mediante `request.status`.

Este historial es **en memoria**: no garantiza deduplicación después de reiniciar el puente o AutoCAD. `bridgeInstanceId` permite detectar un nuevo arranque. No se eliminan resultados automáticamente para hacer espacio: al alcanzar el límite se rechaza trabajo nuevo con `JOURNAL_FULL`.

La cola admite 128 solicitudes. Las solicitudes vencen a los 30 segundos si aún no comenzaron. Un comando activo puede retrasar la ejecución; las operaciones iniciadas no se interrumpen por la fuerza.

`PENDING` o `OUTCOME_UNKNOWN` exigen consultar `cad_get_request_status` con el UUID original. La interrupción del cliente no demuestra que la modificación haya sido cancelada. `NOT_FOUND` tras un reinicio tampoco demuestra que no se ejecutó. El servidor no reenvía modificaciones automáticamente tras romperse una conexión.

Las tuberías están limitadas al SID del usuario actual mediante ACL. El prefijo distingue usuario y proceso, y las conexiones permanecen abiertas entre llamadas. Los tiempos de cola y ejecución se incluyen en las respuestas.

## Validación

```powershell
./scripts/build.ps1 -Offline
./scripts/test-coreconsole.ps1
python tests/integration_stdio.py
```

La primera orden ejecuta pruebas de deduplicación concurrente, historial, vencimiento, cierre, mensajes fragmentados/Unicode, límites de longitud, tubería real persistente, desconexión y validación de herramientas.

La segunda compila un ensamblado de prueba separado y utiliza un dibujo nuevo de Core Console. Comprueba creación, longitud 5, área 100, deduplicación y reversión de un lote fallido; no guarda un DWG. Requiere que Core Console pueda iniciar su perfil y cargar el ensamblado. Esta prueba no pudo completarse en el entorno restringido actual.

La tercera es una prueba opcional del servidor publicado a través de stdin/stdout. Comprueba negociación MCP, catálogo de 13 herramientas, descubrimiento, errores de argumentos/JSON, ping y cierre. Python se utiliza únicamente para esta comprobación; no es una dependencia del servidor ni del plugin.

Los resultados automatizados se guardan en `artifacts/test-results/core-tests.trx`. Las pruebas directas de lectura están en `live-autocad-read-check.json`, las de escritura/reversión/lotes en `live-autocad-write-check.json` y las de Civil 3D en `live-civil3d-check.json`, dentro de `artifacts/test-results`. La superficie Civil utilizada tiene tres vértices y elevaciones conocidas; no se ha comprobado el rendimiento de terrenos grandes. La enumeración de alineamientos se comprobó con una colección vacía, sin validar perfiles/alineamientos reales.

Para repetir la comprobación aprobada de .NET 10 y el SDK oficial MCP, ejecuta desde tu PowerShell normal, con el puente Autodesk abierto:

```powershell
& 'C:\Users\rsant\Desktop\ProyectosPy\MCP_Autocad2021\scripts\Prepare-Official.ps1'
```

Si falta el SDK, este script descarga el instalador oficial de Microsoft y lo instala únicamente en `.tools/dotnet10`, sin modificar el PATH permanente. Restaura NuGet, compila, ejecuta las pruebas del núcleo, publica el servidor y comprueba stdio y consultas del dibujo activo. Sus reportes son `official-preparation.json`, `official-tests.trx` y `official-stdio-integration.json`. Requiere acceso a Internet desde tu usuario normal. No sustituye el complemento .NET 8 instalado ni actualiza las DLL cargadas en Autodesk.

Para instalar el servidor oficial ya validado en ChatGPT de escritorio y Codex, ejecuta desde PowerShell normal:

```powershell
& 'C:\Users\rsant\Desktop\ProyectosPy\MCP_Autocad2021\scripts\Build-DesktopPlugin.ps1' -Install
```

El instalador genera la versión 0.2.0, fija la ruta del host `.tools/dotnet10/dotnet.exe`, actualiza el complemento con el mismo identificador y verifica los archivos instalados mediante SHA256. Conserva la carpeta del proyecto y el runtime local en su ubicación actual. El resultado queda en `artifacts/test-results/desktop-installation.json`; reinicia ChatGPT o abre una sesión nueva de Codex para cargar la nueva versión. No reemplaza el puente cargado dentro de Autodesk. `-Compatibility` prepara la variante .NET 8 si se necesita volver a ella.

Si aparece `NU1301` con `Host desconocido (api.nuget.org)`, el SDK ya puede estar instalado: el fallo corresponde a la conexión con NuGet. El script comprueba DNS y HTTPS, reintenta y, si no responde la entrada V3, prueba la entrada heredada oficial `https://www.nuget.org/api/v2/` (sus descargas también necesitan `globalcdn.nuget.org`). El diagnóstico queda en `official-preparation.json`; puedes ejecutar solo esta comprobación con `-CheckConnectionOnly`. Si fallan ambas direcciones, prueba otra conexión de Internet o revisa el DNS/proxy de tu red antes de repetir. El script no cambia el DNS ni el proxy del equipo.

Para repetir las comprobaciones locales y compilar los ensamblados de prueba:

```powershell
./scripts/Run-Checks.ps1
```

Para las pruebas de un dibujo de ensayo vacío, ejecuta con el mismo usuario de Windows que Autodesk:

```powershell
./scripts/Run-Checks.ps1 -Live -WriteTest -Benchmark
```

La medición registra diez consultas con P50/P95, tiempos de lotes de 100, 1000 y 10000 puntos y memoria de AutoCAD antes/después. Los lotes se acumulan en el dibujo; se crean 11100 puntos adicionales. Estas cifras no se presentan como rendimiento verificado hasta ejecutar el script y obtener el reporte.

El resumen queda en `artifacts/test-results/verification-summary.json`. `Check-Live.ps1` también acepta `-ServerPath` para verificar otra variante del servidor y `-SurfaceReferenceFile examples/surface-reference.json` para contrastar elevaciones con valores conocidos; sustituye los valores del ejemplo con referencias reales.

La DLL `tests/CadMcp.AutoCAD.Smoke/bin/Release/net48/CadMcp.AutoCAD.Smoke.dll` expone `CADMCPSMOKE`. La DLL `tests/CadMcp.Civil3D.Smoke/bin/Release/net48/CadMcp.Civil3D.Smoke.dll` expone `CADMCPCIVILCHECK`, solo para Civil 3D. Ambas se cargan con NETLOAD y exigen un dibujo de ensayo vacío. La segunda crea una superficie TIN del plano z=0.1*x+0.2*y, contrasta elevaciones 3 y 4, comprueba una muestra exterior, crea dos puntos COGO y verifica la reversión de una inserción COGO al abortar una transacción. Son ensamblados de prueba separados; compilados no significa ejecutados. Dejan sus reportes JSON junto a cada DLL y la geometría de ensayo en el dibujo, sin guardarlo.

## Añadir funcionalidades

1. Implementa una operación y regístrala en `OperationRegistry`, con `mutates: true` si modifica el dibujo para que sus resultados se conserven; para un dominio adicional implementa `ICadModule` y configura su carga.
2. Añade parámetros, límites y descripción a `ToolCatalog`.
3. Expón el método correspondiente en `OfficialTools` si utilizas el SDK oficial. El adaptador de compatibilidad utiliza el catálogo directamente.
4. Añade pruebas de comportamiento, fallos y compatibilidad. Compila y carga en Autodesk antes de publicar.

`registry` queda fijo mientras el puente está en ejecución. Actualizar DLL cargadas normalmente requiere reiniciar AutoCAD. La arquitectura permite agregar módulos, pero no descarga ensamblados ni ofrece aislamiento frente a fallos nativos de Autodesk.

Las siguientes ampliaciones previstas son: superficies nuevas, perfiles y estaciones, redes de tuberías, volúmenes, exportaciones y trabajos largos con progreso/cancelación cooperativa. No están implementadas en esta versión.

## Referencias

- [Compatibilidad .NET de AutoCAD 2021](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm).
- [APIs de Civil 3D 2021](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-DevGuide/files/GUID-1FC5774A-14EB-48CC-8A0A-FA983E8B9703.htm).
- [SDK oficial MCP para C#](https://github.com/modelcontextprotocol/csharp-sdk).
- [Transportes MCP](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).
