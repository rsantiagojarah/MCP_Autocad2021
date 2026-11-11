"""Create the prepared reference drawing exclusively through production MCP tools."""
import sys,json,math,uuid,collections,argparse
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'.tools/drawing-libs'))
sys.path.insert(0,str(ROOT/'tests'))
import ezdxf
from ezdxf import disassemble
from ezdxf.path import make_path
from mcp_stdio_client import Client

OUT=ROOT/'output/planos/vivienda_referencia_10m'
DOC='47156328-4a99-4636-a4dc-eaf2df3d1959'
SESSION='cadmcp-8626fb3142287ab7-24664'
source=ezdxf.readfile(OUT/'Vivienda_10m_plantas_A01.dxf')
sheet=source.layouts.get('A-01_PLANTAS_A2')
geometry=[];circles=[];texts=[];hatches=[];overrides=[]
PREFIX='V10_'

def conv(p,paper=False):
    x,y=float(p[0]),float(p[1])
    if paper:x,y=x*.075-3.7,y*.075-4.6
    elif x>13:x-=1.075
    return {'x':round(x*1000,4),'y':round(y*1000,4),'z':0.0}
def addpoly(points,layer,closed,paper=False):
    pp=[conv(p,paper) for p in points]
    if len(pp)>2 and pp[0]==pp[-1]:pp.pop();closed=True
    clean=[]
    for p in pp:
        if not clean or p!=clean[-1]:clean.append(p)
    if len(clean)<(3 if closed else 2):return None
    idx=len(geometry)
    geometry.append({'type':'polyline','points':clean,'layer':PREFIX+layer,'closed':closed})
    return idx

def convert(entities,paper=False):
    for e in disassemble.recursive_decompose(entities):
        kind=e.dxftype();layer=e.dxf.layer
        if kind in ('VIEWPORT','HATCH'):continue
        if kind=='LINE':
            idx=len(geometry)
            geometry.append({'type':'line','points':[conv(e.dxf.start,paper),conv(e.dxf.end,paper)],'layer':PREFIX+layer})
            if e.dxf.lineweight>=0:overrides.append((idx,e.dxf.lineweight))
        elif kind=='CIRCLE':circles.append({'center':conv(e.dxf.center,paper),'radius':e.dxf.radius*(75 if paper else 1000),'layer':PREFIX+layer})
        elif kind in ('TEXT','MTEXT'):
            alignment=e.get_placement()[0].name if kind=='TEXT' else 'CENTER'
            anchor=e.get_placement()[1] if kind=='TEXT' else e.dxf.insert
            contents=e.dxf.text if kind=='TEXT' else e.plain_text()
            if paper:
                contents=contents.replace('Mobiliario agrupado en bloques reutilizables.','Mobiliario con entidades CAD editables.')
                contents=contents.replace('TEXT, DIMENSION, INSERT y HATCH nativos.','TEXT, CIRCLE, LWPOLYLINE y HATCH nativos.')
                contents=contents.replace('Geometría a tamaño real en metros.','Geometría a tamaño real en milímetros.')
                contents=contents.replace('MODELO: METROS','MODELO: MILÍMETROS')
            texts.append({'position':conv(anchor,paper),'text':contents,'height':(e.dxf.height if kind=='TEXT' else e.dxf.char_height)*(75 if paper else 1000),
                          'rotation':math.radians(e.dxf.rotation),'layer':PREFIX+layer,
                          'alignment':{'CENTER':'baseCenter','RIGHT':'baseRight'}.get(alignment,'baseLeft'),
                          'bold':'BOLD' in e.dxf.style})
        elif kind in ('LWPOLYLINE','POLYLINE','ARC','ELLIPSE','SPLINE'):
            p=make_path(e)
            addpoly(list(p.flattening(.025 if paper else .001)),layer,p.is_closed,paper)
        elif kind in ('SOLID','TRACE'):
            addpoly(list(e.vertices()),layer,True,paper)
        elif kind=='POINT':continue
        else:raise RuntimeError('Unsupported entity '+kind)

convert(source.modelspace())
convert(sheet,True)

for paper,layout in [(False,source.modelspace()),(True,sheet)]:
    for h in layout.query('HATCH'):
        loops=[]
        for path in h.paths:
            points=[(v[0],v[1]) for v in path.vertices]
            area=sum(a[0]*b[1]-a[1]*b[0] for a,b in zip(points,points[1:]+points[:1]))
            idx=addpoly(points,'A_MURO_CORTE' if h.dxf.layer=='A_MURO_RELLENO' else h.dxf.layer,True,paper)
            assert idx is not None
            loops.append((idx, bool(path.path_type_flags&1)))
        pattern=h.dxf.pattern_name
        # A square net and garden grass are Autodesk PAT definitions, not lines.
        scale=1
        if pattern=='ARQ_BALDOSA':pattern='NET';scale=140
        elif pattern=='ANSI31':pattern='GRASS';scale=35
        hatches.append({'loops':loops,'pattern':pattern,'scale':scale,'layer':PREFIX+h.dxf.layer,'colorIndex':256})

print(json.dumps({'preparedGeometry':len(geometry),'nativeCircles':len(circles),'texts':len(texts),'nativeHatches':len(hatches)}),flush=True)
if '--payload' in sys.argv:
    dest=Path(sys.argv[sys.argv.index('--payload')+1])
    dest.write_text(json.dumps({'geometry':geometry,'circles':circles,'texts':texts,'hatches':hatches},ensure_ascii=False),encoding='utf-8')
if '--execute' not in sys.argv:sys.exit(0)

logpath=OUT/'operaciones_mcp.json'
log=json.loads(logpath.read_text(encoding='utf-8')) if logpath.exists() else {}
client=Client(ROOT/'.tools/dotnet10/dotnet.exe',ROOT/'artifacts/expanded-0.6.0/server/CadMcp.Server.dll',ROOT)
def call(key,name,args=None,write=False,coordinates=False):
    args={'sessionId':SESSION,'documentId':DOC,**(args or {})}
    if coordinates:args.update(coordinateSpace='WCS',units='Millimeters')
    if write:
        if key in log:
            assert log[key]['name']==name and log[key]['arguments']==args,('Retry differs',key)
            if 'result' in log[key]:return log[key]['result']
        else:
            log[key]={'name':name,'arguments':args,'operationId':str(uuid.uuid4())}
            logpath.write_text(json.dumps(log,ensure_ascii=False,indent=2),encoding='utf-8')
        args={**args,'operationId':log[key]['operationId']}
    res=client.tool(name,args)
    if not res['ok']:raise RuntimeError(json.dumps({'tool':name,'key':key,'result':res},ensure_ascii=False))
    if write:
        log[key]['result']=res['result']
        logpath.write_text(json.dumps(log,ensure_ascii=False,indent=2),encoding='utf-8')
    return res['result']

try:
    info=call('info','cad_get_document_info')
    assert info['units']=='Millimeters',info
    if not log:
        q=call('empty','cad_query_entities',{'limit':1})
        assert not q['entities'],'The originally empty target drawing now has geometry; stop to avoid mixing drawings.'
    # Respect the user's millimetre drawing; 10 m is represented as 10,000 mm.
    weights={l.dxf.name:l.dxf.lineweight for l in source.layers if l.dxf.name.startswith('A_')}
    layers=[{'name':PREFIX+name,'colorIndex':9 if name in ['A_PISO_TRAMA','A_VACIO','A_PROYECCION'] else 7,
             'lineweight':weight,'linetype':'Continuous'} for name,weight in weights.items()]
    call('layers','cad_upsert_layers',{'layers':layers},True)
    call('layers_aux','cad_upsert_layers',{'layers':[
        {'name':PREFIX+'0','colorIndex':7,'lineweight':13},
        {'name':PREFIX+'Defpoints','colorIndex':7,'lineweight':13}]},True)
    call('linetypes','cad_load_linetypes',{'file':'acadiso.lin','names':['DASHED','CENTER']},True)
    call('projection','cad_set_layer_line_properties',{'layers':[PREFIX+'A_PROYECCION'],'linetype':'DASHED'},True)
    call('axis','cad_set_layer_line_properties',{'layers':[PREFIX+'A_EJE'],'linetype':'CENTER'},True)
    call('settings','cad_set_line_settings',{'globalScale':30,'newEntityScale':1,'displayLineweight':True},True)
    style=call('arial','cad_upsert_text_style',{'name':'V10_ARQ_ARIAL','font':'Arial','fixedHeight':0},True,True)
    bold=call('bold','cad_upsert_text_style',{'name':'V10_ARQ_TITULO','font':'Arial Black','fixedHeight':0},True,True)
    handles=[]
    for n in range(0,len(geometry),600):
        key='geometry_'+str(n) if n==0 else 'geometry_remaining_'+str(n)+'_v2'
        r=call(key,'cad_create_entities',{'entities':geometry[n:n+600]},True,True)
        handles.extend(r['handles'])
        print('Created linework',len(handles),'/',len(geometry),flush=True)
    if overrides:
        call('explicit_weights','cad_update_entities',{'entities':[{'handle':handles[i],'lineweight':w} for i,w in overrides]},True)
    circle_handles=[]
    for n,s in enumerate(circles):
        circle_handles.append(call('circle_'+str(n),'cad_create_circle',s,True,True)['handle'])
        if n%30==0:print('Created native circles',n+1,'/',len(circles),flush=True)
    texthandles=[]
    for n,s in enumerate(texts):
        args=dict(s);args['styleHandle']=bold['handle'] if args.pop('bold') else style['handle']
        texthandles.append(call('text_'+str(n),'cad_create_text',args,True,True)['handle'])
        if n%30==0:print('Created native text',n+1,'/',len(texts),flush=True)
    hatch_handles=[]
    for n,h in enumerate(hatches):
        args={k:v for k,v in h.items() if k!='loops'}
        args['outerHandles']=[handles[i] for i,outer in h['loops'] if outer]
        args['islandHandles']=[handles[i] for i,outer in h['loops'] if not outer]
        args['associative']=True
        created=call('hatch_'+str(n),'cad_create_hatch',args,True,True)
        hatch_handles.append(created['handle'])
        print('Created native HATCH',n+1,'/',len(hatches),h['pattern'],flush=True)
    call('view','cad_set_view',{'orientation':'top'},True)
    rows=[];offset=0
    while True:
        r=call('verify','cad_query_entities',{'offset':offset,'limit':500})
        rows.extend(r['entities'])
        if r['nextOffset'] is None:break
        offset=r['nextOffset']
    actual=collections.Counter(r['dxfType'] for r in rows)
    assert actual['HATCH']==len(hatches) and actual['TEXT']==len(texts) and actual['CIRCLE']==len(circles),actual
    assert len(rows)==len(handles)+len(circle_handles)+len(texthandles)+len(hatch_handles),(len(rows),len(handles))
    check=call('weights_check','cad_get_line_properties',{'handles':[handles[0],circle_handles[0],texthandles[0]]})
    report={'passed':True,'transport':'MCP stdio -> production bridge 0.6.0 -> AutoCAD 2021',
            'documentId':DOC,'documentName':info['name'],'units':info['units'],
            'frontageMillimetres':10000,'depthMillimetres':22300,'depthReferenceOnly':True,
            'entityCount':len(rows),'nativeEntityTypes':dict(actual),'nativeHatchHandles':hatch_handles,
            'linePropertyEvidence':check,'activeDrawingSaved':False,'previousGeometryCount':0,
            'layerNames':[x['name'] for x in layers],'notes':[
                'Created directly using MCP tools in the previously empty active drawing.',
                'Furniture consists of individually editable native entities; no raster underlay.',
                'Separate DXF includes reusable blocks, dimensions and an A2 paperspace layout.',
                'Current MCP supports no save or paperspace creation tool; current drawing remains unsaved.'
            ]}
    (OUT/'verificacion_mcp_autocad.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False),flush=True)
finally:client.close()
