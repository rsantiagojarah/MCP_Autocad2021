using System;
using System.Collections.Generic;
using System.Linq;
using CadMcp.Contracts;
using Newtonsoft.Json.Linq;

namespace CadMcp.Core;

// Shared validation also runs inside Autodesk, independently of the MCP transport.
public static class ToolArgs
{
    public static string Text(JObject a, string key, string? fallback = null)
    {
        var v = a[key];
        if (v == null && fallback != null) return fallback;
        Validation.Require(v?.Type == JTokenType.String, "Falta texto: " + key);
        var value = v!.Value<string>()!;
        Validation.Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 1024, "Texto inválido: " + key);
        return value;
    }
    public static double Number(JObject a, string key, double? fallback = null, bool positive = false)
    {
        var v = a[key];
        if (v == null && fallback.HasValue) v = new JValue(fallback.Value);
        Validation.Require(v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer), "Falta número: " + key);
        var n = v!.Value<double>();
        Validation.Require(Validation.Finite(n) && (!positive || n > 0), "Número inválido: " + key);
        return n;
    }
    public static bool Flag(JObject a, string key, bool fallback = false)
    {
        if (a[key] == null) return fallback;
        Validation.Require(a[key]!.Type == JTokenType.Boolean, "Booleano inválido: " + key);
        return a[key]!.Value<bool>();
    }
    public static Vec3 Point(JObject a, string key, Vec3? fallback = null)
    {
        if (a[key] == null && fallback != null) return fallback;
        Validation.Require(a[key] is JObject, "Falta punto: " + key);
        var point = Validation.Arguments<Vec3>((JObject)a[key]!);
        Validation.Require(((JObject)a[key]!).Properties().Count() == 3 && a[key]!["x"] != null && a[key]!["y"] != null && a[key]!["z"] != null, "Indica x, y, z: " + key);
        Validation.Point(point); return point;
    }
    public static List<T> List<T>(JObject a, string key, int min = 1, int max = 10000)
    {
        Validation.Require(a[key] is JArray, "Falta lista: " + key);
        var array = (JArray)a[key]!;
        Validation.Require(array.Count >= min && array.Count <= max && array.All(v => v.Type != JTokenType.Null), "Tamaño o contenido de lista inválido: " + key);
        try { return array.ToObject<List<T>>(Newtonsoft.Json.JsonSerializer.Create(Wire.Settings))!; }
        catch (Newtonsoft.Json.JsonException ex) { throw new CadFault("INVALID_ARGUMENT", ex.Message); }
    }
    public static string[] Handles(JObject a, string key = "handles", int max = 1000)
    {
        var values = List<string>(a, key, 1, max).ToArray();
        Validation.Require(values.All(v => !string.IsNullOrWhiteSpace(v)) && values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Length, "Handles vacíos o duplicados.");
        return values;
    }
    public static (int offset, int limit) Page(JObject a, int maximum = 500)
    {
        foreach (var key in new[] { "offset", "limit" }) Validation.Require(a[key] == null || a[key]!.Type == JTokenType.Integer, "Paginación entera requerida.");
        var offset = (int?)a["offset"] ?? 0; var limit = (int?)a["limit"] ?? 100;
        Validation.Require(offset >= 0 && offset <= 1000000 && limit >= 1 && limit <= maximum, "Paginación inválida.");
        return (offset, limit);
    }
}
