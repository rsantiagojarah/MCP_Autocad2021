# AutoCAD y Civil 3D 2021

Complemento local para ChatGPT de escritorio y Codex. El servidor incluido utiliza .NET 8 y el adaptador MCP de compatibilidad; no incorpora el SDK oficial .NET 10.

Abre AutoCAD o Civil 3D 2021, carga CadMcp.AutoCAD.dll mediante NETLOAD y comprueba CADMCPSTATUS. Ese plugin está en el paquete Autodesk original del proyecto. Abre un chat local nuevo y selecciona este complemento para consultar sesiones y documentos.

No convierte unidades ni coordenadas implícitamente. Las escrituras requieren WCS, unidades coincidentes y un UUID por operación. El historial de operaciones solo dura hasta reiniciar el puente. La carga y las funciones dentro de Autodesk siguen pendientes de validación.

Este paquete está preparado para instalarse desde el catálogo local Herramientas CAD locales. No es una publicación en el directorio público ni conecta un servidor remoto.
