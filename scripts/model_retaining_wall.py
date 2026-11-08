"""Create the requested retaining wall through the official MCP stdio server.

The prepared request and operation UUID are saved before sending any mutation.
Use --inspect to inspect and prepare, then --create to execute that exact request.
"""
import argparse
import json
import math
from pathlib import Path
import queue
import subprocess
import threading
import uuid

ROOT = Path(__file__).resolve().parents[1]
PLAN = ROOT / 'artifacts/retaining-wall/request.json'
REPORT = ROOT / 'artifacts/retaining-wall/result.json'

class Mcp:
    def __init__(self):
        self.process = subprocess.Popen(
            [str(ROOT / '.tools/dotnet10/dotnet.exe'), str(ROOT / 'artifacts/expanded-0.3.0/server/CadMcp.Server.dll')],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding='utf-8', bufsize=1)
        self.responses = queue.Queue()
        self.errors = []
        self.sequence = 0
        def consume():
            for line in self.process.stdout:
                try: self.responses.put(json.loads(line))
                except Exception as error: self.responses.put({'error': str(error)})
            self.responses.put({'error': 'MCP server closed'})
        threading.Thread(target=consume, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()
        self.request('initialize', {'protocolVersion':'2025-11-25', 'capabilities':{}, 'clientInfo':{'name':'RetainingWallModel','version':'0.3.0'}})
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()
    def request(self, method, params):
        self.sequence += 1
        self.process.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.sequence,'method':method,'params':params})+'\n')
        self.process.stdin.flush()
        while True:
            reply = self.responses.get(timeout=50)
            if 'id' not in reply and 'error' not in reply: continue
            if 'error' in reply: raise RuntimeError(reply['error'])
            if reply.get('id') != self.sequence: raise RuntimeError('Uncorrelated MCP response')
            return reply['result']
    def tool(self, name, arguments):
        result = self.request('tools/call', {'name':name,'arguments':arguments})
        payload = json.loads(result['content'][0]['text'])
        if not payload.get('ok'): raise RuntimeError(json.dumps(payload, ensure_ascii=False))
        return payload['result']
    def close(self):
        self.process.stdin.close()
        try: self.process.wait(timeout=5)
        except subprocess.TimeoutExpired: self.process.terminate()

def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding='utf-8')

def inspect(mcp):
    sessions = mcp.tool('cad_list_sessions', {})['sessions']
    assert len(sessions) == 1, 'Expected one Autodesk session'
    session = sessions[0]
    assert 'cad.create_retaining_wall' in session['methods'], 'Wall modeling is unavailable'
    documents = mcp.tool('cad_list_documents', {'sessionId':session['sessionId']})['documents']
    active = [d for d in documents if d['active']]
    assert len(active) == 1, 'Expected one active drawing'
    target = {'sessionId':session['sessionId'],'documentId':active[0]['documentId']}
    info = mcp.tool('cad_get_document_info', target)
    assert info['units'] == 'Meters', 'Dimensions require a drawing in meters'
    rows = mcp.tool('cad_query_entities', {**target,'limit':500})
    assert rows['nextOffset'] is None, 'Drawing is no longer the single-polyline source drawing'
    assert len(rows['entities']) == 1 and rows['entities'][0]['dxfType'] == 'LWPOLYLINE', 'Expected exactly one source polyline and no existing wall'
    source = mcp.tool('cad_get_entity_geometry', {**target,'handle':rows['entities'][0]['handle'],'limit':500})
    assert not source['closed'] and source['nextOffset'] is None, 'Expected an open polyline'
    arguments = {**target,'operationId':str(uuid.uuid4()),'coordinateSpace':'WCS','units':'Meters',
        'sourceHandle':source['handle'],'stemThickness':0.25,'stemHeight':3.0,'heel':2.0,'toe':2.0,
        'footingThickness':0.5,'stemLayer':'MURO_PANTALLA','footingLayer':'MURO_ZAPATA'}
    plan = {'document':info, 'bridgeInstanceId':session['bridgeInstanceId'], 'source':source, 'arguments':arguments,'sent':False}
    if PLAN.exists() and json.loads(PLAN.read_text(encoding='utf-8')).get('sent'):
        raise RuntimeError('A mutation has already been sent. Inspect its saved UUID/status before preparing another.')
    save(PLAN, plan)
    return plan

def create(mcp):
    plan = json.loads(PLAN.read_text(encoding='utf-8'))
    args = plan['arguments']
    target = {key:args[key] for key in ('sessionId','documentId')}
    sessions = mcp.tool('cad_list_sessions', {})['sessions']
    assert any(s['sessionId']==args['sessionId'] and s['bridgeInstanceId']==plan['bridgeInstanceId'] for s in sessions), 'Bridge restarted; inspect operation history before proceeding'
    source = mcp.tool('cad_get_entity_geometry', {**target,'handle':args['sourceHandle'],'limit':500})
    assert source == plan['source'], 'Source geometry changed since preparation'
    if plan.get('sent'):
        result = mcp.tool('cad_get_request_status', {'sessionId':args['sessionId'],'requestId':args['operationId']})
        assert result['state']=='completed' and result['response']['ok'], 'Previous request is unresolved; do not duplicate'
        wall = result['response']['result']
    else:
        plan['sent'] = True
        save(PLAN, plan)
        wall = mcp.tool('cad_create_retaining_wall', args)
        save(REPORT, {'wall':wall,'operationId':args['operationId'],'verificationPending':True})
    stem = mcp.tool('cad_get_entity_geometry', {**target,'handle':wall['stemHandle']})
    base = mcp.tool('cad_get_entity_geometry', {**target,'handle':wall['footingHandle']})
    top = wall['footingTopElevation']
    assert stem['dxfType']=='3DSOLID' and base['dxfType']=='3DSOLID'
    for actual, expected in [(stem['min']['z'],top),(stem['max']['z'],top+3),(base['min']['z'],top-.5),(base['max']['z'],top)]:
        assert math.isclose(actual,expected,abs_tol=1e-6), ('Incorrect solid elevation',actual,expected)
    assert mcp.tool('cad_get_entity_geometry', {**target,'handle':args['sourceHandle'],'limit':500}) == source, 'Source polyline changed'
    result = {'wall':wall,'stem':stem,'footing':base,'operationId':args['operationId'],'verified':True}
    save(REPORT,result)
    result['view'] = mcp.tool('cad_set_view', {**target,'operationId':str(uuid.uuid4()),'orientation':'isometric'})
    save(REPORT,result)
    return result

if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--inspect',action='store_true')
    parser.add_argument('--create',action='store_true')
    options=parser.parse_args()
    assert options.inspect != options.create, 'Select exactly one of --inspect or --create'
    mcp=Mcp()
    try: print(json.dumps(inspect(mcp) if options.inspect else create(mcp), ensure_ascii=False))
    finally: mcp.close()
