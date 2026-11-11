"""Reconcile only this task's known handles against the reviewed R01 geometry."""
import sys,json,uuid,collections,math
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tests'))
from mcp_stdio_client import Client
OUT=ROOT/'output/planos/vivienda_referencia_10m'
old=json.loads((OUT/'despues_ajustes.json').read_text(encoding='utf-8'))
new=json.loads((OUT/'revision_R01_payload.json').read_text(encoding='utf-8'))
original=json.loads((OUT/'operaciones_mcp.json').read_text(encoding='utf-8'))
checks=json.loads((OUT/'revision_geometrica_R01.json').read_text(encoding='utf-8'))
assert not checks['doorConflicts'] and not checks['furnitureWallConflicts'] and checks['wallHatches']==0
client=Client(ROOT/'.tools/dotnet10/dotnet.exe',ROOT/'artifacts/expanded-0.6.0/server/CadMcp.Server.dll',ROOT)
target={'sessionId':'cadmcp-8626fb3142287ab7-24664','documentId':'47156328-4a99-4636-a4dc-eaf2df3d1959'}
logfile=OUT/'revision_R01_operations.json'
log=json.loads(logfile.read_text(encoding='utf-8')) if logfile.exists() else {}
def call(key,name,args,write=True,coords=False):
    args={**target,**args}
    if coords:args.update(coordinateSpace='WCS',units='Millimeters')
    if write:
        if key in log:
            assert log[key]['name']==name and log[key]['arguments']==args,key
            if 'result' in log[key]:return log[key]['result']
        else:log[key]={'name':name,'arguments':args,'operationId':str(uuid.uuid4())}
        logfile.write_text(json.dumps(log,ensure_ascii=False,indent=2),encoding='utf-8')
        args={**args,'operationId':log[key]['operationId']}
    res=client.tool(name,args)
    if not res['ok']:raise RuntimeError(json.dumps({'key':key,'response':res},ensure_ascii=False))
    if write:
        log[key]['result']=res['result']
        logfile.write_text(json.dumps(log,ensure_ascii=False,indent=2),encoding='utf-8')
    return res['result']
def rows():
    result=[];offset=0
    while True:
        r=call('query','cad_query_entities',{'limit':500,'offset':offset},False)
        result+=r['entities']
        if r['nextOffset'] is None:return result
        offset=r['nextOffset']
def gkey(s):
    pts=tuple((round(p['x'],2),round(p['y'],2)) for p in s['points'])
    if s.get('closed'):
        canon=[]
        for seq in [pts,tuple(reversed(pts))]:
            k=seq.index(min(seq));canon.append(seq[k:]+seq[:k])
        pts=min(canon)
    else:pts=min(pts,tuple(reversed(pts)))
    return (s['type'],s['layer'],bool(s.get('closed')),pts)
def ckey(s):return (s['layer'],round(s['radius'],2),round(s['center']['x'],2),round(s['center']['y'],2))
try:
    info=call('info','cad_get_document_info',{},False)
    assert info['units']=='Millimeters'
    # A stable baseline makes restarts use the same IDs and avoids deleting
    # unrelated objects the user may add while reviewing the drawing.
    baseline=OUT/'revision_R01_baseline.json'
    if baseline.exists():live=json.loads(baseline.read_text(encoding='utf-8'))
    else:
        live=rows();baseline.write_text(json.dumps(live,ensure_ascii=False,indent=2),encoding='utf-8')
    alive={r['handle'] for r in live}
    gh=original['geometry_0']['result']['handles']+original['geometry_remaining_600_v2']['result']['handles']
    ch=[original['circle_'+str(i)]['result']['handle'] for i in range(len(old['circles']))]
    th=[original['text_'+str(i)]['result']['handle'] for i in range(len(old['texts']))]
    own=set(gh+ch+th+[v['result']['handle'] for k,v in original.items() if k.startswith('hatch_') and 'result' in v])
    other={r['handle'] for r in live if r['handle'] not in own}
    # Remove all surviving wall fills and floor patterns before changing their
    # associative boundaries. The title-block scale bars are kept.
    remove_h=[r['handle'] for r in live if r['handle'] in own and r['dxfType']=='HATCH' and r['layer']!='V10_A_ROTULO']
    if remove_h:call('remove_old_hatches','cad_delete_entities',{'handles':remove_h})
    pools=collections.defaultdict(collections.deque)
    for s,h in zip(old['geometry'],gh):
        if h in alive:pools[gkey(s)].append(h)
    mapped=[None]*len(new['geometry']);keep=set();add=[]
    for i,s in enumerate(new['geometry']):
        p=pools[gkey(s)]
        if p:mapped[i]=p.popleft();keep.add(mapped[i])
        else:add.append(i)
    for start in range(0,len(add),500):
        ids=add[start:start+500]
        r=call('new_geometry_'+str(start),'cad_create_entities',{'entities':[new['geometry'][i] for i in ids]},coords=True)
        for i,h in zip(ids,r['handles']):mapped[i]=h
    remove=[h for h in gh if h in alive and h not in keep]
    if remove:call('replace_old_geometry','cad_delete_entities',{'handles':remove})
    print(f'Linework corrected: {len(add)} new, {len(remove)} replaced, {len(keep)} preserved.',flush=True)
    cp=collections.defaultdict(collections.deque)
    for s,h in zip(old['circles'],ch):
        if h in alive:cp[ckey(s)].append(h)
    circle_map=[];keepc=set()
    for i,s in enumerate(new['circles']):
        p=cp[ckey(s)]
        if p:h=p.popleft();keepc.add(h)
        else:h=call('new_circle_'+str(i),'cad_create_circle',s,coords=True)['handle']
        circle_map.append(h)
    removec=[h for h in ch if h in alive and h not in keepc]
    if removec:call('replace_old_circles','cad_delete_entities',{'handles':removec})
    assert len(new['texts'])==len(th)
    plain=original['arial']['result']['handle'];bold=original['bold']['result']['handle']
    stylegroups=collections.defaultdict(list)
    for i,(a,b,h) in enumerate(zip(old['texts'],new['texts'],th)):
        assert h in alive
        if a==b:continue
        args={k:v for k,v in b.items() if k not in ['layer','bold']}
        args['handles']=[h]
        call('text_'+str(i),'cad_update_text',args,coords=True)
        stylegroups[bold if b['bold'] else plain].append(h)
    for style,hs in stylegroups.items():call('text_style_'+style,'cad_set_text_style',{'handles':hs,'styleHandle':style})
    print('Furniture and room labels corrected.',flush=True)
    newh=[]
    for i,h in enumerate(new['hatches']):
        if h['layer']=='V10_A_ROTULO':continue
        args={k:v for k,v in h.items() if k!='loops'}
        args.update(outerHandles=[mapped[j] for j,outer in h['loops'] if outer],
                    islandHandles=[mapped[j] for j,outer in h['loops'] if not outer],associative=True)
        r=call('hatch_'+str(i),'cad_create_hatch',args,coords=True)
        # Compare the actual native filled area with all explicit polygon loops.
        expected=0
        for j,outer in h['loops']:
            pts=new['geometry'][j]['points']
            area=abs(sum(a['x']*b['y']-a['y']*b['x'] for a,b in zip(pts,pts[1:]+pts[:1])))/2
            expected+=area if outer else -area
        if 'area' in r:assert abs(r['area']-expected)<max(.1,expected*1e-7),(i,r['area'],expected)
        r['expectedBoundaryArea']=expected
        assert r['loopCount']==len(h['loops'])
        newh.append(r)
        print('Native clipped HATCH verified',len(newh),flush=True)
    call('view','cad_set_view',{'orientation':'top'})
    final=rows()
    byhandle={r['handle']:r for r in final}
    for h,r in zip([h for h in new['hatches'] if h['layer']!='V10_A_ROTULO'],newh):
        nativeArea=sum(byhandle[mapped[j]]['area']*(1 if outer else -1) for j,outer in h['loops'])
        assert abs(nativeArea-r['expectedBoundaryArea'])<max(.1,nativeArea*1e-7)
    (OUT/'revision_R01_handles.json').write_text(json.dumps({'geometry':mapped,'circles':circle_map,'texts':th}),encoding='utf-8')
    assert other.issubset({r['handle'] for r in final})
    assert not any(r['dxfType']=='HATCH' and r['layer']=='V10_A_MURO_RELLENO' for r in final)
    assert all(r['associative'] for r in final if r['dxfType']=='HATCH')
    # The two formerly blocked routes are checked against the complete new
    # furniture geometry in the source review, not merely hatch visibility.
    report={'passed':True,'revision':'R01','transport':'MCP -> AutoCAD 2021 native database',
            'document':info['name'],'documentId':target['documentId'],'units':info['units'],
            'entityCount':len(final),'nativeTypes':dict(collections.Counter(r['dxfType'] for r in final)),
            'unrelatedEntitiesPreserved':sorted(other),'geometryChecks':checks,
            'nativeClippedHatches':newh,'wallHatches':0,'drawingSaved':False}
    (OUT/'verificacion_revision_R01_mcp.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'passed':True,'entityCount':len(final),'nativeTypes':report['nativeTypes'],'wallHatches':0}),flush=True)
finally:client.close()
