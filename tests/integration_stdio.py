"""Exercise the published compatibility server over real MCP stdio. No CAD needed."""
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
server = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / "artifacts/compatibility-net8/server/CadMcp.Server.dll"

messages = [
    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "cadmcp-test", "version": "1"}}},
    {"jsonrpc": "2.0", "method": "notifications/initialized"},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
    {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "cad_list_sessions", "arguments": {}}},
    {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "cad_create_entities", "arguments": {}}},
    {"jsonrpc": "2.0", "id": 5, "method": "ping"},
    {"jsonrpc": "2.0", "id": 6, "method": "unsupported_method"},
    {"jsonrpc": "2.0", "id": 7, "method": "tools/call", "params": {"name": "cad_query_entities", "arguments": {"sessionId": "invalid", "documentId": "d", "limit": 100000}}},
    {"jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": {"name": "cad_list_sessions", "arguments": []}},
]
payload = "\n".join(json.dumps(m) for m in messages) + "\n"
run = subprocess.run(["dotnet", str(server)], input=payload, text=True, encoding="utf-8", capture_output=True, timeout=45, cwd=root)
assert run.returncode == 0, run.stderr
responses = [json.loads(line) for line in run.stdout.splitlines()]
assert len(responses) == 8, responses
by_id = {r["id"]: r for r in responses}
assert by_id[1]["result"]["protocolVersion"] == "2025-11-25"
tools = by_id[2]["result"]["tools"]
assert len(tools) == 89 and len({t["name"] for t in tools}) == 89
assert "sessions" in json.loads(by_id[3]["result"]["content"][0]["text"])["result"]
for i in (4, 7):
    assert by_id[i]["result"]["isError"]
    assert json.loads(by_id[i]["result"]["content"][0]["text"])["error"]["code"] == "INVALID_ARGUMENT"
assert by_id[5]["result"] == {}
assert by_id[6]["error"]["code"] == -32601
assert by_id[8]["error"]["code"] == -32602

# Malformed input must produce a parse error without logging into protocol stdout.
bad = subprocess.run(["dotnet", str(server)], input="{bad json}\n", text=True, encoding="utf-8", capture_output=True, timeout=10, cwd=root)
assert json.loads(bad.stdout)["error"]["code"] == -32700
assert "id" in json.loads(bad.stdout) and json.loads(bad.stdout)["id"] is None

invalid = subprocess.run(["dotnet", str(server)], input=json.dumps({"jsonrpc": "2.0", "id": 20, "method": {"bad": True}}) + "\n", text=True, encoding="utf-8", capture_output=True, timeout=10, cwd=root)
assert invalid.returncode == 0 and json.loads(invalid.stdout)["error"]["code"] == -32600

invalid_init = subprocess.run(["dotnet", str(server)], input=json.dumps({"jsonrpc": "2.0", "id": 21, "method": "initialize", "params": {"protocolVersion": {}}}) + "\n", text=True, encoding="utf-8", capture_output=True, timeout=10, cwd=root)
assert invalid_init.returncode == 0 and json.loads(invalid_init.stdout)["error"]["code"] == -32602

report = {"passed": True, "tools": len(tools), "checks": ["initialize", "notification", "tool_catalog", "session_discovery", "argument_validation", "ping", "unknown_method", "parse_error", "clean_stdout", "clean_shutdown"]}
out = root / "artifacts/test-results/stdio-integration.json"
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report))
