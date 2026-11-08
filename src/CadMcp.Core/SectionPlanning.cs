using System;
using System.Collections.Generic;
using CadMcp.Contracts;

namespace CadMcp.Core;

public static class SectionPlanning
{
    public const int MaximumSections = 200;
    public const int MaximumSolidPairs = 1000;

    public static double[] Stations(double start, double end, double interval)
    {
        Validation.Require(Validation.Finite(start) && Validation.Finite(end) && Validation.Finite(interval)
            && end >= start && interval > 0, "Rango de estaciones o intervalo inválido.");
        var count = Math.Floor((end - start) / interval);
        Validation.Require(Validation.Finite(count) && count < MaximumSections, "Máximo 200 secciones por operación; aumenta el intervalo.");
        var stations = new List<double>();
        var tolerance = Math.Min(interval / 4, Math.Max(1, end - start) * 1e-10);
        for (var i = 0; i <= (int)count; i++)
        {
            var station = start + i * interval;
            if (station < end - tolerance) stations.Add(station);
        }
        // Include each endpoint once, even when the interval does not divide the range.
        stations.Add(end);
        Validation.Require(stations.Count <= MaximumSections, "Máximo 200 secciones por operación.");
        return stations.ToArray();
    }

    public static void Ranges(double leftWidth, double rightWidth, double elevationMin, double elevationMax)
    {
        Validation.Require(Validation.Finite(leftWidth) && Validation.Finite(rightWidth) && leftWidth > 0 && rightWidth > 0,
            "Los anchos izquierdo y derecho deben ser positivos.");
        Validation.Require(Validation.Finite(elevationMin) && Validation.Finite(elevationMax) && elevationMax > elevationMin,
            "La cota máxima debe superar la mínima.");
    }

    public static Vec3 Position(Vec3 origin, int index, int columns, double columnSpacing, double rowSpacing)
    {
        Validation.Point(origin);
        Validation.Require(index >= 0 && columns >= 1 && columns <= 50 && Validation.Finite(columnSpacing)
            && Validation.Finite(rowSpacing) && columnSpacing > 0 && rowSpacing > 0, "Distribución de vistas inválida.");
        var point = new Vec3 { X = origin.X + (index % columns) * columnSpacing,
            Y = origin.Y - (index / columns) * rowSpacing, Z = origin.Z };
        Validation.Point(point);
        return point;
    }
}
