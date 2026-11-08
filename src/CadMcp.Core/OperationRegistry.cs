using System;
using System.Collections.Generic;
using CadMcp.Contracts;
using Newtonsoft.Json.Linq;

namespace CadMcp.Core;

public sealed class OperationRegistry
{
    private readonly Dictionary<string, Func<BridgeRequest, JToken>> handlers = new Dictionary<string, Func<BridgeRequest, JToken>>(StringComparer.Ordinal);
    private readonly HashSet<string> mutations = new HashSet<string>(StringComparer.Ordinal);
    public IEnumerable<string> Methods => handlers.Keys;
    public void Register(string method, Func<BridgeRequest, JToken> handler, bool mutates = false)
    {
        if (handlers.ContainsKey(method)) throw new InvalidOperationException("Operación duplicada: " + method);
        handlers.Add(method, handler);
        if (mutates) mutations.Add(method);
    }
    public bool IsMutation(string method) => mutations.Contains(method);
    public JToken Execute(BridgeRequest request)
    {
        if (!handlers.TryGetValue(request.Method, out var handler))
            throw new CadFault("UNSUPPORTED_OPERATION", "Esta sesión no ofrece " + request.Method);
        return handler(request);
    }
}

public interface ICadModule
{
    void Register(OperationRegistry registry);
}
