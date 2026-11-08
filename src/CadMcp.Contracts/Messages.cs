using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CadMcp.Contracts;

public static class Wire
{
    public const int Version = 1;
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.None,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        DateParseHandling = DateParseHandling.None,
        MaxDepth = 48,
        MissingMemberHandling = MissingMemberHandling.Error,
        Culture = System.Globalization.CultureInfo.InvariantCulture
    };
    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);
    public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings)
        ?? throw new ArgumentException("Mensaje JSON vacío.");
}

public sealed class BridgeRequest
{
    public int Version { get; set; } = Wire.Version;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("D");
    public string Method { get; set; } = "";
    public string? DocumentId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.AddSeconds(30);
    public JObject Arguments { get; set; } = new JObject();
}

public sealed class BridgeError
{
    public string Code { get; set; } = "INTERNAL_ERROR";
    public string Message { get; set; } = "";
}

public sealed class BridgeResponse
{
    public string RequestId { get; set; } = "";
    public bool Ok { get; set; }
    public JToken? Result { get; set; }
    public BridgeError? Error { get; set; }
    public double QueueMilliseconds { get; set; }
    public double ExecutionMilliseconds { get; set; }
    public static BridgeResponse Success(string id, object result) => new BridgeResponse
        { RequestId = id, Ok = true, Result = JToken.FromObject(result, JsonSerializer.Create(Wire.Settings)) };
    public static BridgeResponse Fail(string id, string code, string message) => new BridgeResponse
        { RequestId = id, Error = new BridgeError { Code = code, Message = message } };
}

public sealed class CadFault : Exception
{
    public string Code { get; }
    public CadFault(string code, string message) : base(message) { Code = code; }
}

public sealed class Vec3
{
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [JsonProperty(Required = Required.Always)]
    public double X { get; set; }
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [JsonProperty(Required = Required.Always)]
    public double Y { get; set; }
#if NET8_0_OR_GREATER
    [System.Text.Json.Serialization.JsonRequired]
#endif
    [JsonProperty(Required = Required.Always)]
    public double Z { get; set; }
}

public sealed class EntitySpec
{
    public string Type { get; set; } = "";
    public string Layer { get; set; } = "0";
    public List<Vec3> Points { get; set; } = new List<Vec3>();
    public bool Closed { get; set; }
}

public sealed class CreateEntitiesArgs
{
    public string CoordinateSpace { get; set; } = "";
    public string Units { get; set; } = "";
    public List<EntitySpec> Entities { get; set; } = new List<EntitySpec>();
}

public sealed class LayerSpec
{
    public string Name { get; set; } = "";
    public int ColorIndex { get; set; } = 7;
}

public sealed class UpdateEntitySpec
{
    public string Handle { get; set; } = "";
    public string? Layer { get; set; }
    public int? ColorIndex { get; set; }
}

public sealed class QueryEntitiesArgs
{
    public string? Layer { get; set; }
    public string? DxfType { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public Vec3? Min { get; set; }
    public Vec3? Max { get; set; }
}

public sealed class CogoArgs
{
    public string CoordinateSpace { get; set; } = "";
    public string Units { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Vec3> Points { get; set; } = new List<Vec3>();
}
