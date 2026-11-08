using System;
using CadMcp.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CadMcp.Core;

public static class Validation
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new CadFault("INVALID_ARGUMENT", message);
    }
    public static T Arguments<T>(JObject args) where T : class
    {
        try { return args.ToObject<T>(JsonSerializer.Create(Wire.Settings)) ?? throw new CadFault("INVALID_ARGUMENT", "Faltan argumentos."); }
        catch (JsonException ex) { throw new CadFault("INVALID_ARGUMENT", ex.Message); }
    }
    public static void Point(Vec3? p)
    {
        Require(p != null && Finite(p.X) && Finite(p.Y) && Finite(p.Z), "Las coordenadas deben ser números finitos.");
    }
    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static void Geometry(CreateEntitiesArgs args)
    {
        Require(args.CoordinateSpace == "WCS", "Esta versión requiere coordinateSpace=WCS.");
        Require(!string.IsNullOrWhiteSpace(args.Units), "Indica explícitamente units.");
        Require(args.Entities != null && args.Entities.Count > 0 && args.Entities.Count <= 10000, "El lote admite entre 1 y 10000 entidades.");
        var vertexCount = 0;
        foreach (var entity in args.Entities!)
        {
            Require(entity != null && entity.Points != null, "Entidad o puntos nulos.");
            Require(entity!.Type == "point" || entity.Type == "line" || entity.Type == "polyline", "Tipo permitido: point, line o polyline.");
            Require(!string.IsNullOrWhiteSpace(entity.Layer), "La capa es obligatoria.");
            var points = entity.Points!;
            vertexCount += points.Count;
            Require(vertexCount <= 100000, "Máximo 100000 vértices por lote.");
            Require(entity.Type == "point" ? points.Count == 1 : entity.Type == "line" ? points.Count == 2 : points.Count >= (entity.Closed ? 3 : 2), "Número de puntos inválido.");
            foreach (var p in points) Point(p);
            if (entity.Type != "point")
            {
                for (var i = 1; i < points.Count; i++)
                    Require(points[i].X != points[i - 1].X || points[i].Y != points[i - 1].Y || (entity.Type == "line" && points[i].Z != points[i - 1].Z), "Hay vértices consecutivos coincidentes.");
            }
            if (entity.Type == "polyline")
            {
                foreach (var p in points) Require(p.Z == points[0].Z, "La polilínea 2D requiere una elevación constante.");
                if (entity.Closed) Require(points[0].X != points[points.Count - 1].X || points[0].Y != points[points.Count - 1].Y, "No repitas el primer vértice: utiliza closed=true.");
            }
        }
    }
    public static void Query(QueryEntitiesArgs args)
    {
        Require(args.Limit >= 1 && args.Limit <= 500, "limit debe estar entre 1 y 500.");
        Require(args.Offset >= 0 && args.Offset <= 1000000, "offset debe estar entre 0 y 1000000.");
        Require((args.Min == null) == (args.Max == null), "Indica min y max juntos.");
        if (args.Min != null && args.Max != null)
        {
            Point(args.Min); Point(args.Max);
            Require(args.Min.X <= args.Max.X && args.Min.Y <= args.Max.Y && args.Min.Z <= args.Max.Z, "Caja de consulta inválida.");
        }
    }
}
