"""Interactive MCP handshake for either server adapter; supports an optional live CAD check."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading

parser = argparse.ArgumentParser()
parser.add_argument("dotnet")
parser.add_argument("server")
parser.add_argument("--require-live", action="store_true")
parser.add_argument("--report", default="artifacts/test-results/sdk-stdio-integration.json")
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
process = subprocess.Popen([args.dotnet, args.server], cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
responses = queue.Queue()
errors = []
report = {"passed": False, "server": args.server, "dotnet": args.dotnet, "checks": []}

def consume_stdout():
    for line in process.stdout:
        try:
            responses.put(json.loads(line))
        except Exception as error:
            responses.put({"stdoutParseError": str(error), "line": line[:500]})
    responses.put({"closed": True})

def consume_stderr():
    for line in process.stderr:
        errors.append(line)

threading.Thread(target=consume_stdout, daemon=True).start()
threading.Thread(target=consume_stderr, daemon=True).start()
sequence = 0

def request(method, parameters):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": sequence, "method": method, "params": parameters}) + "\n")
    process.stdin.flush()
    while True:
        response = responses.get(timeout=45)
        assert not response.get("stdoutParseError"), response
        assert not response.get("closed"), "Server closed before responding"
        if "id" not in response:
            continue
        assert response["id"] == sequence, response
        assert "error" not in response, response
        return response["result"]

def tool(name, arguments, expect_error=False):
    result = request("tools/call", {"name": name, "arguments": arguments})
    payload = json.loads(result["content"][0]["text"])
    assert bool(result.get("isError")) == expect_error, result
    assert bool(payload["ok"]) != expect_error, payload
    return payload

try:
    initialized = request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "CadMcpSdkCheck", "version": "0.1.0"}})
    report["protocolVersion"] = initialized["protocolVersion"]
    report["serverInfo"] = initialized["serverInfo"]
    process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
    process.stdin.flush()
    catalog = request("tools/list", {})["tools"]
    names = {item["name"] for item in catalog}
    assert len(names) == 71 and "civil_create_cogo_points" in names and "cad_list_sessions" in names, names
    assert {"civil_get_section_sources", "civil_set_section_sources", "civil_create_multiple_section_views", "civil_update_section_views", "civil_add_solid_sections", "civil_generate_cross_sections"} <= names
    assert {"cad_create_retaining_wall", "civil_create_tin_surface", "civil_create_alignment", "civil_create_pipe_network", "civil_create_corridor"} <= names
    for item in catalog:
        if item["name"] in {"cad_create_retaining_wall", "civil_create_layout_profile", "civil_add_network_parts"}:
            assert "operationId" in item["inputSchema"]["required"], item
    report["checks"].extend(["interactive_initialize", "71_tools", "section_tools", "extended_tools", "mutation_UUID_schema"])
    assert request("ping", {}) == {}
    report["checks"].append("ping")
    discovery = tool("cad_list_sessions", {})["result"]
    sessions = discovery["sessions"]
    report["connectionState"] = discovery.get("connectionState")
    report["otherUserBridgeCount"] = discovery.get("otherUserBridgeCount")
    report["liveSessionCount"] = len(sessions)
    invalid = tool("cad_query_entities", {"sessionId": "invalid", "documentId": "invalid", "limit": 501}, expect_error=True)
    assert invalid["error"]["code"] == "INVALID_ARGUMENT", invalid
    report["checks"].append("tool_validation")
    section_base = {"sessionId":"invalid", "documentId":"d", "operationId":"not-a-uuid", "coordinateSpace":"WCS", "units":"Meters"}
    section_cases = {
        "civil_set_section_sources": {"sampleLineGroupHandle":"A", "sourceHandles":["B"]},
        "civil_create_multiple_section_views": {"sampleLineGroupHandle":"A", "namePrefix":"CHECK", "position":{"x":0,"y":0,"z":0}, "leftWidth":5,"rightWidth":5,"elevationMin":-1,"elevationMax":4},
        "civil_update_section_views": {"sectionViewHandles":["A"]},
        "civil_add_solid_sections": {"sectionViewHandles":["A"],"solidHandles":["B"]},
        "civil_generate_cross_sections": {"alignmentHandle":"A","name":"CHECK","interval":1,"position":{"x":0,"y":0,"z":0},"leftWidth":5,"rightWidth":5,"elevationMin":-1,"elevationMax":4,"solidHandles":["B"]}
    }
    for section_name, extra in section_cases.items():
        rejected = tool(section_name, {**section_base, **extra}, expect_error=True)
        assert rejected["error"]["code"] == "INVALID_ARGUMENT", rejected
    report["checks"].append("five_section_mutations_reject_invalid_UUID_before_dispatch")
    invalid_wall = tool("cad_create_retaining_wall", {
        "sessionId": "invalid", "documentId": "d", "operationId": "00000000-0000-0000-0000-000000000001", "coordinateSpace": "WCS", "units": "Meters",
        "sourceHandle": "6D43", "stemThickness": 0.25, "stemHeight": 3, "heel": 2, "toe": 2, "footingThickness": 0.5
    }, expect_error=True)
    assert invalid_wall["error"]["code"] == "INVALID_ARGUMENT", invalid_wall
    # Binding must reject missing nested coordinates, rather than inventing zero.
    malformed = request("tools/call", {"name": "civil_add_network_parts", "arguments": {
        "sessionId": "invalid", "documentId": "d", "operationId": "00000000-0000-0000-0000-000000000002", "coordinateSpace": "WCS", "units": "Meters", "networkHandle": "A",
        "pipes": [{"familyHandle": "B", "sizeHandle": "C", "start": {"x": 0, "y": 0, "z": 0}, "end": {"x": 1, "y": 0}}]
    }})
    assert malformed.get("isError"), malformed
    report["checks"].extend(["wall_wrapper_validation", "required_nested_coordinates"])
    if args.require_live:
        assert sessions, "No live CAD bridge detected for this Windows user"
        session = sessions[0]
        documents = tool("cad_list_documents", {"sessionId": session["sessionId"]})["result"]["documents"]
        active = next(document for document in documents if document["active"])
        target = {"sessionId": session["sessionId"], "documentId": active["documentId"]}
        info = tool("cad_get_document_info", target)["result"]
        geometry = tool("cad_query_entities", {**target, "limit": 10})["result"]
        report["document"] = info
        report["firstPageEntities"] = len(geometry["entities"])
        report["checks"].append("live_document_query")
        if session["civil3d"]:
            surfaces = tool("civil_list_surfaces", {**target, "limit": 100})["result"]
            report["surfaceCountFirstPage"] = len(surfaces["surfaces"])
            report["checks"].append("live_civil_surface_query")
    report["passed"] = True
except Exception as error:
    report["error"] = repr(error)
    raise
finally:
    try:
        process.stdin.close()
    except BrokenPipeError:
        pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.terminate()
        process.wait(timeout=5)
        report["eofShutdown"] = "terminated_after_timeout"
    else:
        report["eofShutdown"] = "clean" if process.returncode == 0 else f"exit_{process.returncode}"
    if report["eofShutdown"] != "clean":
        report["passed"] = False
        report.setdefault("error", "Server did not shut down cleanly after stdin EOF")
    path = pathlib.Path(args.report)
    if not path.is_absolute():
        path = root / path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    path.with_suffix(".stderr.log").write_text("".join(errors), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))

if not report["passed"]:
    raise SystemExit(1)
