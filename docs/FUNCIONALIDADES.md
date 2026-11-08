# MCP AutoCAD / Civil 3D 2021 — versión 0.4.0

La versión **0.4.0 ofrece 71 herramientas**: las 13 originales, 52 de la ampliación 0.3.0 y seis nuevas para secciones. Incluye creación y edición de los objetos descritos abajo; no equivale a exponer toda la API de Civil 3D. Las nuevas capacidades y sus límites están en [SECCIONES.md](SECCIONES.md).

## Estado de entrega

El código se compila contra las DLL de Autodesk 2021 instaladas. Los adaptadores oficial (.NET 10, SDK MCP) y de compatibilidad (.NET 8) utilizan el mismo catálogo. Las pruebas de protocolo y validación se ejecutan sin alterar dibujos.

La nueva ejecución nativa necesita el puente **0.4.0** cargado en Civil 3D. El puente anterior permanece en memoria hasta cerrar Autodesk. AutoCAD Core Console falla al configurar su perfil en este entorno, antes de cargar las pruebas; las comprobaciones de sólidos y Civil nuevas están preparadas, pero no deben considerarse aprobadas por esa compilación.

## Funciones

| Área | Operaciones disponibles |
|---|---|
| Conexión y dibujo | Sesiones/capacidades y versión del puente, documentos, unidades, UCS/WCS, consulta de ModelSpace, capas y estados. |
| Geometría | Puntos, líneas, polilíneas 2D/3D, círculos, texto, coordenadas detalladas, bulges, longitudes, áreas y límites. |
| Edición CAD | Mover, copiar, rotar en Z, escalar uniformemente, offset y eliminar por handles; respeta capas bloqueadas. |
| Sólidos | Cajas, cilindros, extrusión de contornos, unión/resta/intersección, volúmenes y centroides. |
| Muros | Pantalla y zapata como sólidos continuos por offsets de polilínea abierta horizontal; admite rectas/arcos y conserva el eje. |
| COGO | Crear/listar puntos, cambiar coordenadas/nombre/descripción, crear grupos por números existentes, asignar estilos. |
| Terrenos TIN | Crear desde puntos; añadir vértices, grupos COGO, líneas de rotura y límites exteriores/ocultos/visibles; reconstruir y consultar estadísticas. |
| Análisis del terreno | Elevaciones por lote, triángulos paginados, curvas de nivel extraídas y superficies de volumen con corte/relleno/balance. |
| Alineamientos | Crear desde polilínea conservando su geometría, estación inicial, conversión estación/offset↔XY y consulta de perfiles asociados. |
| Perfiles | Terreno desde superficie, rasante por PVI, tangentes, curvas verticales parabólicas simétricas/asimétricas y circulares, elevaciones por estación y vistas de perfil. |
| Secciones | Grupos y líneas de muestreo, anchos izquierdo/derecho, superficies muestreadas, consulta de secciones y vistas de sección. |
| Feature lines y sitios | Crear desde objetos CAD, asignar elevaciones de superficie, consultar puntos, crear/listar sitios y consultar parcelas. |
| Redes de gravedad | Explorar catálogos/familias/tamaños; crear red, tuberías rectas y estructuras; conectar extremos, editar tuberías libres y cotas de tapa/fondo. |
| Corredores | Inventario y regiones; crear desde eje/rasante/ensamblaje existente; asignar objetivos, frecuencia de aplicación, reconstruir y crear superficies por códigos. |
| Estilos | Listar estilos compatibles y asignarlos a objetos; no presupone nombres como «Standard». |

Los nombres exactos de las 52 herramientas añadidas y su descripción se encuentran en [`examples/extended-tools.json`](../examples/extended-tools.json). Las 13 herramientas originales se conservan y figuran en el README.

## Activación

El paquete preparado está en `artifacts/expanded-0.4.0`. La preparación no modifica la instalación activa ni cierra Autodesk.

1. Guardar los dibujos y cerrar AutoCAD/Civil 3D.
2. Ejecutar en PowerShell normal, desde esta carpeta:

   ```powershell
   ./scripts/Install-ExpandedMcp.ps1
   ```

   El instalador comprueba los hashes del paquete, conserva una copia del puente anterior, instala la carga automática y actualiza el complemento local de Codex. Rechaza ejecutarse si Autodesk sigue abierto. `-BridgeOnly` actualiza únicamente el puente; en ese caso se debe configurar el cliente con `artifacts/expanded-0.4.0/mcp.config.json`.

3. Abrir Civil 3D y recargar el cliente MCP. Comprobar que `cad_list_sessions` devuelve `bridgeVersion: "0.4.0"` y métodos nuevos como `cad.create_retaining_wall` y `civil.create_tin_surface`. El catálogo del servidor debe contener 71 herramientas.
4. Probar primero en un dibujo de ensayo. Las DLL de prueba son opcionales y no forman parte del paquete de producción:
   - `tests/CadMcp.AutoCAD.Smoke/bin/Release/net48/CadMcp.AutoCAD.Smoke.dll`, comando `CADMCPSMOKE`, exige ModelSpace vacío y comprueba sólidos, cotas, volumen del muro y reversión.
   - `tests/CadMcp.Civil3D.Smoke/bin/Release/net48/CadMcp.Civil3D.Smoke.dll`, comando `CADMCPCIVILCHECK`, exige un dibujo Civil vacío con estilos y comprueba TIN, volúmenes, COGO, eje, rasante e interpolación.

## Reglas de uso

- Descubrir sesión y documento y consultar unidades antes de dibujar. Las herramientas con coordenadas exigen `coordinateSpace="WCS"` y las unidades exactas del dibujo. No hay conversión implícita.
- Usar handles e identificadores devueltos por el MCP. La modificación requiere `operationId` UUID. Conservar el mismo UUID en los reintentos; ante `OUTCOME_UNKNOWN`, consultar `cad_get_request_status`. El historial se pierde al reiniciar el puente.
- Cada modificación utiliza bloqueo del documento y transacción. Si falla la creación de una pieza o un sólido dentro de un lote, se revierte el lote.
- Paginar hasta `nextOffset=null`, sin cambiar el dibujo durante el recorrido. Los objetivos de corredor se asignan con los índices de la consulta más reciente del mismo corredor.
- Las herramientas de mover/copiar/eliminar CAD rechazan objetos Civil; utilizar su operación nativa. Se rechaza editar objetos de referencia y capas bloqueadas.
- Los corredores requieren un ensamblaje existente con subensamblajes adecuados. La creación de catálogos/subensamblajes, redes a presión, diseños hidráulicos, grading y creación de parcelas no se incluyen en esta versión. Las parcelas existentes sí se consultan.

## Muro solicitado

Una vez activada la versión nueva, consultar de nuevo la polilínea del dibujo activo y llamar a `cad_create_retaining_wall` con sus identificadores actuales. Los valores solicitados están preparados en `examples/retaining-wall.json`.

- Pantalla: 0,25 m de espesor y 3,00 m por encima de la zapata.
- Talón y puntera: 2,00 m desde cada cara de la pantalla; ancho total 4,25 m.
- Zapata: 0,50 m de espesor, por debajo de la elevación de la polilínea.
- Altura total: 3,50 m. La polilínea marca el eje de pantalla y la cota superior de zapata; talón a la izquierda del recorrido.

El modelo geométrico no constituye una verificación estructural. No se ha creado todavía en el dibujo original.

## Referencias utilizadas

La compatibilidad de compilación se verifica contra las DLL locales 2021. Las operaciones Civil siguen la [referencia oficial de creación de alineamientos](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-API/files/html/bb5c29ce-e1bf-917c-aa1e-902777a8f20d.htm), [líneas de rotura](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-API/files/html/93787e73-9b20-4758-5ef4-e651928eae94.htm), [superficies de volumen](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-API/files/html/178fcf69-6079-268e-139a-0c00edcd50e3.htm) y [feature lines](https://help.autodesk.com/cloudhelp/2021/ENU/Civil3D-API/files/html/847c818b-b459-5390-9c55-cf5903b995c0.htm).
