"""Apply final furniture clearances and remove redundant wall boundary curves via MCP."""
import sys,json,collections,uuid,statistics
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tests'))
from mcp_stdio_client import Client
out=ROOT/'output/planos/vivienda_referencia_10m'
old=json.loads((out/'antes_ajustes.json').read_text(encoding='utf-8'))
new=json.loads((out/'despues_ajustes.json').read_text(encoding='utf-8'))
log=json.loads((out/'operaciones_mcp.json').read_text(encoding='utf-8'))
refpath=out/'ajustes_mcp.json'
ref=json.loads(refpath.read_text(encoding='utf-8')) if refpath.exists() else {}
target={'sessionId':'cadmcp-8626fb3142287ab7-24664','documentId':'47156328-4a99-4636-a4dc-eaf2df3d1959'}
handles=log['geometry_0']['result']['handles']+log['geometry_remaining_600_v2']['result']['handles']
client=Client(ROOT/'.tools/dotnet10/dotnet.exe',ROOT/'artifacts/expanded-0.6.0/server/CadMcp.Server.dll',ROOT)
def call(key,name,args,write=True,coords=False):
    args={**target,**args}
    if coords:args.update(coordinateSpace='WCS',units='Millimeters')
    if write:
        if key not in ref:ref[key]={'operationId':str(uuid.uuid4())}
        if 'result' in ref[key]:return ref[key]['result']
        refpath.write_text(json.dumps(ref,ensure_ascii=False,indent=2),encoding='utf-8')
        args['operationId']=ref[key]['operationId']
    result=client.tool(name,args)
    assert result['ok'],result
    if write:
        ref[key]['result']=result['result']
        refpath.write_text(json.dumps(ref,ensure_ascii=False,indent=2),encoding='utf-8')
    return result['result']
try:
    movements=collections.defaultdict(list)
    assert len(old['geometry'])==len(new['geometry'])==len(handles)
    for i,(a,b) in enumerate(zip(old['geometry'],new['geometry'])):
        if a==b:continue
        assert a['type']==b['type'] and a['layer']==b['layer']
        ab=[(min(p[k] for p in a['points']),max(p[k] for p in a['points'])) for k in ['x','y']]
        bb=[(min(p[k] for p in b['points']),max(p[k] for p in b['points'])) for k in ['x','y']]
        # Curve tessellation may differ by <1 mm after a pure translation.
        # Recover the intentional whole-centimetre displacement, preserving the
        # existing native object rather than rebuilding its approximation.
        delta=tuple(round(((bb[k][0]+bb[k][1]-ab[k][0]-ab[k][1])/2)/10)*10 for k in [0,1])
        assert all(abs(bb[k][j]-ab[k][j]-delta[k])<2 for k in [0,1] for j in [0,1]),(i,ab,bb)
        movements[delta].append(handles[i])
    for i,(a,b) in enumerate(zip(old['circles'],new['circles'])):
        if a==b:continue
        dx=round(b['center']['x']-a['center']['x'],3);dy=round(b['center']['y']-a['center']['y'],3)
        movements[(dx,dy)].append(log['circle_'+str(i)]['result']['handle'])
    for i,((dx,dy),hs) in enumerate(sorted(movements.items())):
        call('move_'+str(i),'cad_transform_entities',{'handles':hs,'displacement':{'x':dx,'y':dy,'z':0}},coords=True)
    for i,(a,b) in enumerate(zip(old['texts'],new['texts'])):
        if a!=b:
            call('label_'+str(i),'cad_update_text',{'handles':[log['text_'+str(i)]['result']['handle']],
                  'position':b['position'],'text':b['text']},coords=True)
    # Keep the associative hatch boundaries; remove identical older outlines.
    boundary={i for h in new['hatches'] for i,outer in h['loops']}
    unique={};redundant=[]
    for i,g in enumerate(new['geometry']):
        if g['type']!='polyline' or not g.get('closed') or g['layer']!='V10_A_MURO_CORTE':continue
        pts=tuple((p['x'],p['y']) for p in g['points'])
        seq=[]
        for pp in [pts,tuple(reversed(pts))]:
            k=pp.index(min(pp));seq.append(pp[k:]+pp[:k])
        key=min(seq)
        if key in unique:
            prev=unique[key]
            if i in boundary and prev not in boundary:redundant.append(handles[prev]);unique[key]=i
            elif i not in boundary:redundant.append(handles[i])
        else:unique[key]=i
    if redundant:call('remove_redundant_wall_curves','cad_delete_entities',{'handles':redundant})
    call('final_view','cad_set_view',{'orientation':'top'})
    rows=[];offset=0
    while True:
        r=call('read','cad_query_entities',{'limit':500,'offset':offset},False)
        rows+=r['entities']
        if r['nextOffset'] is None:break
        offset=r['nextOffset']
    layers=call('layers','cad_list_layers',{'limit':100},False)
    report=json.loads((out/'verificacion_mcp_autocad.json').read_text(encoding='utf-8'))
    report['entityCount']=len(rows)
    report['nativeEntityTypes']=dict(collections.Counter(r['dxfType'] for r in rows))
    report['finalRefinement']={'movedEntities':sum(map(len,movements.values())),
                               'redundantWallCurvesRemoved':len(redundant)}
    report['nativeLayers']=layers
    assert report['nativeEntityTypes']['HATCH']==23
    assert len(rows)==1379-len(redundant)
    (out/'verificacion_mcp_autocad.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'passed':True,'entities':len(rows),'removedDuplicateWallCurves':len(redundant),'nativeTypes':report['nativeEntityTypes']}),flush=True)
finally:client.close()
