"""Resume the remaining tests on the exact layer/document recorded by the initial live test."""
import argparse
import datetime
import json
import pathlib
import uuid
from mcp_stdio_client import Client

parser = argparse.ArgumentParser()
parser.add_argument("dotnet")
parser.add_argument("server")
parser.add_argument("--initial-report", required=True)
parser.add_argument("--report", default="artifacts/test-results/text-hatch/live-native-mcp-complete.json")
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
previous = json.loads(pathlib.Path(args.initial_report).read_text(encoding="utf-8"))
report = {**previous, "passed": False, "resumedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()}
report.pop("error", None)
target = {"sessionId": previous["evidence"]["production_bridge_0_5_0"]["sessionId"], "documentId": previous["document"]["documentId"]}
layer = previous["testLayer"]
assert layer.startswith("MCP_TEXT_HATCH_TEST_")
client = Client(args.dotnet, args.server, root)


def tool(name, extra=None, write=False, coords=False):
    parameters = {**target, **(extra or {})}
    if write:
        parameters["operationId"] = str(uuid.uuid4())
    if coords:
        parameters.update(coordinateSpace="WCS", units=previous["document"]["units"])
    result = client.tool(name, parameters)
    assert result["ok"], {"tool": name, "response": result}
    return result["result"]


def check(condition, name, evidence=None):
    assert condition, {"check": name, "evidence": evidence}
    report["checks"].append(name)
    if evidence is not None:
        report["evidence"][name] = evidence
    print("PASS " + name, flush=True)


def reject(name, extra, code=None):
    parameters = {**target, **extra, "operationId": str(uuid.uuid4()), "coordinateSpace": "WCS", "units": previous["document"]["units"]}
    result = client.tool(name, parameters)
    check(not result["ok"] and (code is None or result["error"]["code"] == code), "reject_"+name+"_"+(code or "invalid_boundary"), result)


def geometry(handle):
    return tool("cad_get_entity_geometry", {"handle": handle})


try:
    sessions = client.tool("cad_list_sessions", {})["result"]["sessions"]
    session = next(s for s in sessions if s["sessionId"] == target["sessionId"])
    assert session["bridgeInstanceId"] == previous["bridgeInstanceId"], "El puente se reinició; no se reanuda una prueba con identificadores anteriores."
    tool("cad_get_document_info")  # also requires that this exact document remains active
    objects = tool("cad_query_entities", {"layer": layer, "limit": 500})
    assert objects["nextOffset"] is None
    rows = objects["entities"]
    ansi_handle = previous["evidence"]["native_ANSI31_created"]["handle"]
    solid_handle = previous["evidence"]["native_SOLID_created"]["handle"]
    hatches = [tool("cad_get_hatch", {"handle": e["handle"]}) for e in rows if e["dxfType"] == "HATCH"]
    cutout = next(h for h in hatches if h["loopCount"] == 2)
    outer = cutout["loops"][0]["associatedHandles"][0]
    island = cutout["loops"][1]["associatedHandles"][0]
    boundary2 = next(h for h in hatches if h["handle"] == solid_handle)["loops"][0]["associatedHandles"][0]
    required = {ansi_handle, solid_handle, cutout["handle"], outer, island, boundary2}
    assert required <= {e["handle"] for e in rows}, "Los objetos ya no pertenecen a la capa de ensayo registrada."
    outer_geometry, island_geometry = geometry(outer), geometry(island)
    expected = outer_geometry["area"] - island_geometry["area"]
    native_area = cutout.get("area")
    check(expected == 96 and cutout["loopCount"] == 2 and "Outermost" in cutout["loops"][0]["loopType"] and "Default" in cutout["loops"][1]["loopType"]
          and (native_area is None or abs(native_area - expected) < 1e-6), "native_island_loops_and_exact_boundaries", {"hatch": cutout, "outerArea": outer_geometry["area"], "islandArea": island_geometry["area"], "expectedNetArea": expected})
    if native_area is None:
        report.setdefault("warnings", []).append("AutoCAD 2021 did not expose Hatch.Area for this patterned hatch. Native outer/default loops and exact source areas 100/4 were independently verified.")
    edit = tool("cad_update_hatch", {"handle": ansi_handle, "scale": 2, "angle": .5, "colorIndex": 5, "associative": False}, write=True, coords=True)
    check(edit["handle"] == ansi_handle and edit["scale"] == 2 and abs(edit["angle"]-.5) < 1e-8 and edit["colorIndex"] == 5 and not edit["associative"], "hatch_properties_updated_in_place", edit)
    restored = tool("cad_update_hatch", {"handle": ansi_handle, "associative": True}, write=True, coords=True)
    check(restored["associative"] and restored["loops"][0]["associatedHandles"] == [outer], "hatch_associativity_restored", restored)
    pattern = tool("cad_update_hatch", {"handle": ansi_handle, "pattern": "SOLID"}, write=True, coords=True)
    check(pattern["pattern"] == "SOLID" and pattern["handle"] == ansi_handle, "hatch_pattern_updated_in_place", pattern)
    tool("cad_update_hatch", {"handle": ansi_handle, "pattern": "ANSI31"}, write=True, coords=True)
    tool("cad_transform_entities", {"handles": [island], "displacement": {"x": 1, "y": 0, "z": 0}}, write=True, coords=True)
    moving = tool("cad_get_hatch", {"handle": cutout["handle"]})
    island_after = geometry(island)
    check(moving["associative"] and moving["loops"][1]["associatedHandles"] == [island]
          and island_after["vertices"][0]["point"]["x"] == 4, "native_associative_island_moved", moving)

    def rectangle(x, y, size, z=0, closed=True):
        points = [{"x": px, "y": py, "z": z} for px, py in [(x,y),(x+size,y),(x+size,y+size),(x,y+size)]]
        return tool("cad_create_entities", {"entities":[{"type":"polyline","closed":closed,"layer":layer,"points":points}]}, write=True, coords=True)["handles"][0]

    open_boundary = rectangle(30,0,3,closed=False)
    raised = rectangle(4,4,1,z=2)
    before = tool("cad_query_entities", {"layer":layer,"limit":500})["entities"]
    hatch_args = {"outerHandles":[outer],"pattern":"ANSI31","layer":layer}
    reject("cad_create_hatch", {**hatch_args,"pattern":"__MCP_NO_PATTERN__"}, "INVALID_HATCH_PATTERN")
    reject("cad_create_hatch", {**hatch_args,"outerHandles":[open_boundary]})
    reject("cad_create_hatch", {**hatch_args,"islandHandles":[raised]})
    reject("cad_create_hatch", {**hatch_args,"islandHandles":[boundary2]})
    reject("cad_update_hatch", {"handle":ansi_handle,"scale":0}, "INVALID_ARGUMENT")
    text_handle = previous["evidence"]["Arial_assigned_to_existing_TEXT_and_MTEXT"]["texts"][0]["handle"]
    reject("cad_update_text", {"handles":[text_handle,outer],"text":"Must not persist"}, "INVALID_OBJECT")
    check(geometry(text_handle)["text"] == "Updated native text", "invalid_text_batch_rolled_back")
    after = tool("cad_query_entities", {"layer":layer,"limit":500})["entities"]
    check(len(before) == len(after), "invalid_hatches_leave_no_partial_entities")
    types = [e["dxfType"] for e in after]
    check(types.count("TEXT")==3 and types.count("MTEXT")==1 and types.count("HATCH")==3, "native_TEXT_MTEXT_HATCH_confirmed", {"types":types,"entities":after})
    check(all(geometry(h)["dxfType"]=="LWPOLYLINE" for h in [outer,island,boundary2]), "source_contours_preserved")
    report["handles"]={"ansi31":ansi_handle,"solid":solid_handle,"withIsland":cutout["handle"],"outer":outer,"island":island}
    report["passed"]=True
except Exception as error:
    report["error"]=repr(error)
    raise
finally:
    client.close()
    path=root/args.report
    path.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    print(json.dumps({"passed":report["passed"],"checks":len(report["checks"]),"report":str(path),"error":report.get("error")},ensure_ascii=False),flush=True)
