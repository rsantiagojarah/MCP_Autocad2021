"""Commit the final wardrobe and underlay corrections using known R01 handles."""
import sys,json,uuid,collections
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'output/planos/vivienda_referencia_10m'
sys.path.insert(0,str(ROOT/'tests'))
from mcp_stdio_client import Client
old=json.loads((OUT/'revision_R01_before_finalQA.json').read_text(encoding='utf-8'))
new=json.loads((OUT/'revision_R01_final_payload.json').read_text(encoding='utf-8'))
mapping=json.loads((OUT/'revision_R01_handles.json').read_text(encoding='utf-8'))
qa=json.loads((OUT/'revision_geometrica_R01.json').read_text(encoding='utf-8'))
for k,v in qa.items():
    if 'Conflicts' in k:assert not v,(k,v)
assert old['circles']==new['circles']
def key(s):
    pts=tuple((round(p['x'],2),round(p['y'],2)) for p in s['points'])
    if s.get('closed'):
        seqs=[]
        for t in [pts,tuple(reversed(pts))]:
            k=t.index(min(t));seqs.append(t[k:]+t[:k])
        pts=min(seqs)
    else:pts=min(pts,tuple(reversed(pts)))
    return(s['type'],s['layer'],bool(s.get('closed')),pts)
for a,b in zip(old['hatches'],new['hatches']):
    assert a['pattern']==b['pattern'] and a['layer']==b['layer']
    assert sorted((key(old['geometry'][i]),o) for i,o in a['loops'])==sorted((key(new['geometry'][i]),o) for i,o in b['loops'])
client=Client(ROOT/'.tools/dotnet10/dotnet.exe',ROOT/'artifacts/expanded-0.6.0/server/CadMcp.Server.dll',ROOT)
target={'sessionId':'cadmcp-8626fb3142287ab7-24664','documentId':'47156328-4a99-4636-a4dc-eaf2df3d1959'}
lp=OUT/'revision_R01_final_operations.json'
log=json.loads(lp.read_text(encoding='utf-8')) if lp.exists() else {}
def call(k,n,args,write=True,coords=False):
    args={**target,**args}
    if coords:args.update(coordinateSpace='WCS',units='Millimeters')
    if write:
        if k not in log:log[k]={'name':n,'arguments':args,'operationId':str(uuid.uuid4())}
        assert log[k]['name']==n and log[k]['arguments']==args
        if 'result' in log[k]:return log[k]['result']
        lp.write_text(json.dumps(log,ensure_ascii=False,indent=2),encoding='utf-8')
        args={**args,'operationId':log[k]['operationId']}
    r=client.tool(n,args);assert r['ok'],r
    if write:
        log[k]['result']=r['result'];lp.write_text(json.dumps(log,ensure_ascii=False,indent=2),encoding='utf-8')
    return r['result']
try:
    pools=collections.defaultdict(collections.deque)
    for s,h in zip(old['geometry'],mapping['geometry']):pools[key(s)].append(h)
    keep=set();add=[];mapped=[None]*len(new['geometry'])
    for i,s in enumerate(new['geometry']):
        p=pools[key(s)]
        if p:mapped[i]=p.popleft();keep.add(mapped[i])
        else:add.append(i)
    protected={mapping['geometry'][i] for h in old['hatches'] for i,o in h['loops']}
    assert protected.issubset(keep)
    if add:
        r=call('new_geometry','cad_create_entities',{'entities':[new['geometry'][i] for i in add]},coords=True)
        for i,h in zip(add,r['handles']):mapped[i]=h
    remove=[h for h in mapping['geometry'] if h not in keep]
    if remove:call('old_geometry','cad_delete_entities',{'handles':remove})
    for i,(a,b) in enumerate(zip(old['texts'],new['texts'])):
        if a!=b:call('text_'+str(i),'cad_update_text',{'handles':[mapping['texts'][i]],'text':b['text'],'position':b['position'],'height':b['height']},coords=True)
    call('view','cad_set_view',{'orientation':'top'})
    rows=[];offset=0
    while True:
        r=call('read','cad_query_entities',{'offset':offset,'limit':500},False);rows+=r['entities']
        if r['nextOffset'] is None:break
        offset=r['nextOffset']
    assert not any(r['dxfType']=='HATCH' and r['layer']=='V10_A_MURO_RELLENO' for r in rows)
    report=json.loads((OUT/'verificacion_revision_R01_mcp.json').read_text(encoding='utf-8'))
    report.update(entityCount=len(rows),nativeTypes=dict(collections.Counter(r['dxfType'] for r in rows)),
                  geometryChecks=qa,finalFurnitureCheck=True)
    (OUT/'verificacion_revision_R01_mcp.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    (OUT/'revision_R01_final_handles.json').write_text(json.dumps({'geometry':mapped,'circles':mapping['circles'],'texts':mapping['texts']}),encoding='utf-8')
    print(json.dumps({'passed':True,'created':len(add),'replaced':len(remove),'entities':len(rows),'checks':qa}),flush=True)
finally:client.close()
