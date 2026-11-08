# AutoCAD y Civil 3D 2021

Complemento local para ChatGPT de escritorio y Codex, versión 0.4.0. Servidor: .NET 10 con SDK oficial MCP para C# 1.2.0.
Host .NET seleccionado: C:\Users\rsant\Desktop\ProyectosPy\MCP_Autocad2021\.tools\dotnet10\dotnet.exe. Conserva esa instalación y la carpeta del proyecto en su ubicación actual.

Abre AutoCAD o Civil 3D 2021, carga CadMcp.AutoCAD.dll mediante NETLOAD y comprueba CADMCPSTATUS. Ese plugin está en el paquete Autodesk original del proyecto. Abre un chat local nuevo y selecciona este complemento para consultar sesiones y documentos.

No convierte unidades ni coordenadas implícitamente. Las escrituras requieren WCS, unidades coincidentes y un UUID por operación. El historial de operaciones solo dura hasta reiniciar el puente. Los casos de prueba de AutoCAD y Civil 3D están documentados en artifacts/test-results del proyecto; no se ha validado el rendimiento de terrenos grandes.

Este paquete está preparado para instalarse desde el catálogo local Herramientas CAD locales. No es una publicación en el directorio público ni conecta un servidor remoto.
