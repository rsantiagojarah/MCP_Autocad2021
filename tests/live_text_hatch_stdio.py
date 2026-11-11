"""Exercise the production MCP bridge in an explicitly selected empty test drawing.

Leaves all test geometry in a dedicated layer. Does not save/close/restart Autodesk.
"""
import argparse
import datetime
import json
import pathlib
import queue
import subprocess
import threading
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("dotnet")
parser.add_argument("server")
parser.add_argument("--session-id", required=True)
parser.add_argument("--document-id", required=True)
parser.add_argument("--allow-empty-drawing-write-test", action="store_true", required=True)
parser.add_argument("--report", default="artifacts/test-results/text-hatch/live-native-mcp.json")
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
report_path = pathlib.Path(args.report)
if not report_path.is_absolute():
    report_path = root / report_path
report_path.parent.mkdir(parents=True, exist_ok=True)
report = {"passed": False, "transport": "MCP stdio -> production Named Pipe bridge -> Autodesk 2021",
          "recordedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "checks": [], "evidence": {}}
process = subprocess.Popen([args.dotnet, args.server], cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
responses = queue.Queue()
stderr = []


def consume():
    for line in process.stdout:
        try:
            responses.put(json.loads(line))
        except Exception as error:
            responses.put({"parseError": str(error), "line": line[:500]})
    responses.put({"closed": True})


threading.Thread(target=consume, daemon=True).start()
threading.Thread(target=lambda: stderr.extend(process.stderr), daemon=True).start()
sequence = 0
target = {"sessionId": args.session_id, "documentId": args.document_id}


def request(method, parameters):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": sequence, "method": method, "params": parameters}) + "\n")
    process.stdin.flush()
    while True:
        result = responses.get(timeout=45)
        assert not result.get("closed") and not result.get("parseError"), result
        if "id" not in result:
            continue
        assert result["id"] == sequence and "error" not in result, result
        return result["result"]


def raw(name, parameters):
    result = request("tools/call", {"name": name, "arguments": parameters})
    return json.loads(result["content"][0]["text"])


def tool(name, extra=None, write=False, coordinates=False, operation_id=None):
    parameters = {**target, **(extra or {})}
    if write:
        parameters["operationId"] = operation_id or str(uuid.uuid4())
    if coordinates:
        parameters.update(coordinateSpace="WCS", units=report["document"]["units"])
    result = raw(name, parameters)
    assert result["ok"], {"tool": name, "response": result}
    return result["result"]


def check(condition, name, evidence=None):
    assert condition, {"check": name, "evidence": evidence}
    report["checks"].append(name)
    if evidence is not None:
        report["evidence"][name] = evidence
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PASS " + name, flush=True)


def reject(name, extra, code=None, coordinates=True):
    parameters = {**target, **extra, "operationId": str(uuid.uuid4())}
    if coordinates:
        parameters.update(coordinateSpace="WCS", units=report["document"]["units"])
    result = raw(name, parameters)
    check(not result["ok"] and (code is None or result["error"]["code"] == code), "reject_" + name + "_" + (code or "invalid_boundary"), result)


def geometry(handle):
    return tool("cad_get_entity_geometry", {"handle": handle})


try:
    request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "LiveTextHatchTest", "version": "0.5.0"}})
    process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
    process.stdin.flush()
    discovery = raw("cad_list_sessions", {})["result"]
    report["discovery"] = discovery
    sessions = discovery["sessions"]
    session = next((s for s in sessions if s["sessionId"] == args.session_id), None)
    assert session is not None, "La sesión indicada no respondió bajo esta identidad de Windows: " + json.dumps(discovery, ensure_ascii=False)
    check(session["bridgeVersion"] == "0.5.0" and "cad.create_hatch" in session["methods"], "production_bridge_0_5_0", session)
    report["bridgeInstanceId"] = session["bridgeInstanceId"]
    report["document"] = tool("cad_get_document_info")
    initial = tool("cad_query_entities", {"limit": 1})
    check(not initial["entities"] and initial["nextOffset"] is None, "empty_test_drawing_preflight", report["document"])
    token = uuid.uuid4().hex[:8]
    layer = "MCP_TEXT_HATCH_TEST_" + token
    report["testLayer"] = layer
    check(any(f["font"].casefold() == "arial" for f in tool("cad_list_fonts", {"fontType": "ttf", "limit": 500})["fonts"]), "Arial_available")
    check(any(f["font"].casefold() == "txt.shx" for f in tool("cad_list_fonts", {"fontType": "shx", "limit": 500})["fonts"]), "SHX_available")
    check(any(p["name"].upper() == "ANSI31" for p in tool("cad_list_hatch_patterns", {"limit": 500})["patterns"]), "ANSI31_available")
    tool("cad_upsert_layers", {"layers": [{"name": layer, "colorIndex": 3}]}, write=True)
    texts = []
    for index, kind in enumerate(["text", "mtext", "text"]):
        texts.append(tool("cad_create_text", {"position": {"x": 0, "y": 20 + index * 3, "z": 0}, "text": "Existing " + kind, "height": 1, "layer": layer, "type": kind}, write=True, coordinates=True))
    style_args = {"name": "MCP_ARIAL_" + token, "font": "Arial", "fixedHeight": 0, "widthFactor": .9, "obliqueAngle": .1}
    style_id = str(uuid.uuid4())
    arial = tool("cad_upsert_text_style", style_args, write=True, coordinates=True, operation_id=style_id)
    check(arial["fontFamily"].casefold() == "arial", "Arial_style_created", arial)
    check(tool("cad_upsert_text_style", style_args, write=True, coordinates=True, operation_id=style_id) == arial, "style_retry_deduplicated")
    reject("cad_upsert_text_style", style_args, "STYLE_EXISTS")
    changed_style = tool("cad_upsert_text_style", {**style_args, "updateExisting": True, "widthFactor": .8}, write=True, coordinates=True)
    check(changed_style["handle"] == arial["handle"] and changed_style["widthFactor"] == .8, "explicit_shared_style_update")
    reject("cad_upsert_text_style", {"name": "MCP_BAD_" + token, "font": "__MCP_NOT_INSTALLED__"}, "FONT_NOT_FOUND")
    shx = tool("cad_upsert_text_style", {"name": "MCP_SHX_" + token, "font": "txt.shx"}, write=True, coordinates=True)
    check(shx["fontFile"].lower().endswith("txt.shx"), "SHX_style_created", shx)
    assigned = tool("cad_set_text_style", {"handles": [t["handle"] for t in texts[:2]], "styleHandle": arial["handle"]}, write=True)
    check(not assigned["styleDefinitionChanged"] and all(t["styleHandle"] == arial["handle"] for t in assigned["texts"]), "Arial_assigned_to_existing_TEXT_and_MTEXT", assigned)
    edit = tool("cad_update_text", {"handles": [t["handle"] for t in texts[:2]], "text": "Updated native text", "height": 1.5, "rotation": .25, "alignment": "middleCenter"}, write=True, coordinates=True)
    check(all(t["height"] == 1.5 and abs(t["rotation"] - .25) < 1e-8 and t["alignment"] == "MiddleCenter" for t in edit["texts"]), "text_height_rotation_alignment_updated", edit)
    check([t["handle"] for t in edit["texts"]] == [t["handle"] for t in texts[:2]], "text_handles_preserved")
    other = geometry(texts[2]["handle"])
    check(other["text"] == texts[2]["text"] and other["styleHandle"] == texts[2]["styleHandle"], "unselected_text_unchanged")
    selected = tool("cad_create_text", {"position": {"x": 0, "y": 29, "z": 0}, "text": "SHX native", "height": 1, "layer": layer, "styleHandle": shx["handle"]}, write=True, coordinates=True)
    check(selected["styleHandle"] == shx["handle"], "create_text_selects_SHX_style")

    def rectangle(x, y, size, z=0, closed=True):
        points = [{"x": px, "y": py, "z": z} for px, py in [(x, y), (x + size, y), (x + size, y + size), (x, y + size)]]
        return tool("cad_create_entities", {"entities": [{"type": "polyline", "closed": closed, "layer": layer, "points": points}]}, write=True, coordinates=True)["handles"][0]

    outer = rectangle(0, 0, 10)
    island = rectangle(3, 3, 2)
    boundary2 = rectangle(15, 0, 5)
    hatch_args = {"outerHandles": [outer], "pattern": "ANSI31", "layer": layer, "scale": 1, "angle": .15, "associative": True, "colorIndex": 3}
    hatch_uuid = str(uuid.uuid4())
    ansi = tool("cad_create_hatch", hatch_args, write=True, coordinates=True, operation_id=hatch_uuid)
    check(ansi["dxfType"] == "HATCH" and ansi["pattern"] == "ANSI31", "native_ANSI31_created", ansi)
    check(tool("cad_create_hatch", hatch_args, write=True, coordinates=True, operation_id=hatch_uuid) == ansi, "hatch_retry_deduplicated")
    conflict = {**target, **hatch_args, "pattern": "SOLID", "operationId": hatch_uuid, "coordinateSpace": "WCS", "units": report["document"]["units"]}
    check(raw("cad_create_hatch", conflict)["error"]["code"] == "REQUEST_ID_CONFLICT", "hatch_UUID_conflict_rejected")
    solid = tool("cad_create_hatch", {**hatch_args, "outerHandles": [boundary2], "pattern": "SOLID"}, write=True, coordinates=True)
    check(solid["dxfType"] == "HATCH" and solid["pattern"] == "SOLID", "native_SOLID_created", solid)
    cutout = tool("cad_create_hatch", {**hatch_args, "islandHandles": [island]}, write=True, coordinates=True)
    cutout_info = tool("cad_get_hatch", {"handle": cutout["handle"]})
    outer_area, island_area = geometry(outer)["area"], geometry(island)["area"]
    check(cutout_info["loopCount"] == 2 and outer_area == 100 and island_area == 4
          and ("area" not in cutout_info or abs(cutout_info["area"] - 96) < 1e-6), "native_island_loops_and_exact_boundaries",
          {"hatch": cutout_info, "outerArea": outer_area, "islandArea": island_area, "expectedNetArea": outer_area-island_area})
    if "area" not in cutout_info:
        report.setdefault("warnings", []).append("Hatch.Area unavailable; native loops and exact source contour areas were independently verified.")
    edited_hatch = tool("cad_update_hatch", {"handle": ansi["handle"], "scale": 2, "angle": .5, "colorIndex": 5, "associative": False}, write=True, coordinates=True)
    check(edited_hatch["handle"] == ansi["handle"] and edited_hatch["scale"] == 2 and abs(edited_hatch["angle"] - .5) < 1e-8 and edited_hatch["colorIndex"] == 5 and not edited_hatch["associative"], "hatch_properties_updated_in_place", edited_hatch)
    restored = tool("cad_update_hatch", {"handle": ansi["handle"], "associative": True}, write=True, coordinates=True)
    check(restored["associative"] and restored["loops"][0]["associatedHandles"] == [outer], "hatch_associativity_restored", restored)
    changed_pattern = tool("cad_update_hatch", {"handle": ansi["handle"], "pattern": "SOLID"}, write=True, coordinates=True)
    check(changed_pattern["pattern"] == "SOLID" and changed_pattern["handle"] == ansi["handle"], "hatch_pattern_updated_in_place")
    tool("cad_update_hatch", {"handle": ansi["handle"], "pattern": "ANSI31"}, write=True, coordinates=True)
    # Move the island alone; the associated hatch must recalculate from area 4 to the same area at a new location.
    tool("cad_transform_entities", {"handles": [island], "displacement": {"x": 1, "y": 0, "z": 0}}, write=True, coordinates=True)
    moving_info = tool("cad_get_hatch", {"handle": cutout["handle"]})
    check(moving_info["associative"] and moving_info["loops"][1]["associatedHandles"] == [island]
          and geometry(island)["vertices"][0]["point"]["x"] == 4
          and ("area" not in moving_info or abs(moving_info["area"] - 96) < 1e-6), "associative_hatch_tracks_moved_island", moving_info)
    open_boundary = rectangle(30, 0, 3, closed=False)
    raised_island = rectangle(4, 4, 1, z=2)
    before = tool("cad_query_entities", {"layer": layer, "limit": 500})["entities"]
    reject("cad_create_hatch", {**hatch_args, "pattern": "__MCP_NO_PATTERN__"}, "INVALID_HATCH_PATTERN")
    reject("cad_create_hatch", {**hatch_args, "outerHandles": [open_boundary]})
    reject("cad_create_hatch", {**hatch_args, "islandHandles": [raised_island]})
    reject("cad_create_hatch", {**hatch_args, "islandHandles": [boundary2]})
    reject("cad_update_hatch", {"handle": ansi["handle"], "scale": 0}, "INVALID_ARGUMENT")
    reject("cad_update_text", {"handles": [texts[0]["handle"], outer], "text": "Must not persist"}, "INVALID_OBJECT")
    check(geometry(texts[0]["handle"])["text"] == "Updated native text", "invalid_text_batch_rolled_back")
    after = tool("cad_query_entities", {"layer": layer, "limit": 500})["entities"]
    check(len(before) == len(after), "invalid_hatches_leave_no_partial_entities")
    types = [e["dxfType"] for e in after]
    check(types.count("TEXT") == 3 and types.count("MTEXT") == 1 and types.count("HATCH") == 3, "native_TEXT_MTEXT_HATCH_confirmed", {"types": types, "entities": after})
    check(all(geometry(h)["dxfType"] == "LWPOLYLINE" for h in [outer, island, boundary2]), "source_contours_preserved")
    report["handles"] = {"texts": [t["handle"] for t in texts] + [selected["handle"]], "ansi31": ansi["handle"], "solid": solid["handle"], "withIsland": cutout["handle"], "outer": outer, "island": island}
    report["passed"] = True
except Exception as error:
    report["error"] = repr(error)
    raise
finally:
    process.stdin.close()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.terminate()
        process.wait(timeout=5)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    report_path.with_suffix(".stderr.log").write_text("".join(stderr), encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "checks": len(report["checks"]), "report": str(report_path), "error": report.get("error")}, ensure_ascii=False), flush=True)
