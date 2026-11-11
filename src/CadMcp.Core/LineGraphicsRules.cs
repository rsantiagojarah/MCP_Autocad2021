using System;
using System.Linq;
using CadMcp.Contracts;

namespace CadMcp.Core;

public static class LineGraphicsRules
{
    // Native AutoCAD values: hundredths of a millimetre; negative values are inheritance modes.
    private static readonly int[] Weights = { -3,-2,-1,0,5,9,13,15,18,20,25,30,35,40,50,53,60,70,80,90,100,106,120,140,158,200,211 };
    public static int[] Lineweights(bool layer = false) => Weights.Where(w => !layer || w != -1 && w != -2).ToArray();
    public static void Weight(int weight, bool layer = false)
    {
        if (!Lineweights(layer).Contains(weight)) throw new CadFault("INVALID_LINEWEIGHT",
            "Grosor no admitido. Usa cad_list_lineweights. Valores en centésimas de mm (25=0.25 mm); -1=ByLayer, -2=ByBlock, -3=Default. Las capas no admiten ByLayer/ByBlock.");
    }
    public static void Scale(double scale)
    {
        Validation.Require(Validation.Finite(scale) && scale > 0, "La escala del tipo de línea debe ser un número finito mayor que cero.");
    }
    public static void Name(string name)
    {
        Validation.Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 255
            && name.IndexOfAny(new[] { '*','?','[',']','~',',','|','/','\\','\r','\n' }) < 0,
            "Nombre de tipo de línea inválido; indica nombres explícitos, sin comodines.");
    }
}
