using System.Collections.Generic;

namespace CadMcp.Contracts;

public sealed class CogoUpdateSpec
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public string Handle { get; set; } = "";
    public Vec3? Position { get; set; }
    public string? Description { get; set; }
    public string? Name { get; set; }
}
public sealed class StationOffsetSpec
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public double Station { get; set; }
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public double Offset { get; set; }
}
public sealed class PviSpec
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public double Station { get; set; }
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public double Elevation { get; set; }
}
public sealed class PipeSpec
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public string FamilyHandle { get; set; } = "";
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public string SizeHandle { get; set; } = "";
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public Vec3 Start { get; set; } = new Vec3();
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public Vec3 End { get; set; } = new Vec3();
}
public sealed class StructureSpec
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public string FamilyHandle { get; set; } = "";
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public string SizeHandle { get; set; } = "";
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public Vec3 Position { get; set; } = new Vec3();
    public double Rotation { get; set; }
}
public sealed class CorridorTargetSpec
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public int Index { get; set; }
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [Newtonsoft.Json.JsonProperty(Required = Newtonsoft.Json.Required.Always)]
    public string[] TargetHandles { get; set; } = System.Array.Empty<string>();
}
