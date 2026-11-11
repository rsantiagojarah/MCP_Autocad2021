"""Create an editable architectural reconstruction from the supplied two-floor image.

Geometry is in metres. The user supplied the 10 m frontage; all other dimensions
are explicitly indicative. No existing drawing is read, changed, or overwritten.
"""
from __future__ import annotations
import sys, math, json, collections
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.tools/drawing-libs'))
import ezdxf
from shapely.geometry import Polygon, LineString, Point, box
from shapely.ops import unary_union
from ezdxf import disassemble
from ezdxf.path import make_path
from ezdxf.enums import TextEntityAlignment
from ezdxf.colors import rgb2int
from ezdxf.math import BoundingBox2d

OUT = ROOT / 'output/planos/vivienda_referencia_10m'
OUT.mkdir(parents=True, exist_ok=True)
doc = ezdxf.new('R2018', setup=True)
doc.units = 6
doc.header['$MEASUREMENT'] = 1
doc.header['$LWDISPLAY'] = True
doc.header['$LTSCALE'] = 1
doc.header['$INSBASE'] = (0, 0, 0)
doc.header['$PSLTSCALE'] = 0
doc.header['$LUNITS'] = 2
doc.header['$LUPREC'] = 2
doc.header['$AUNITS'] = 0
doc.header['$DIMASSOC'] = 2
doc.styles.new('ARQ', dxfattribs={'font': 'arial.ttf'})
doc.styles.new('ARQ_BOLD', dxfattribs={'font': 'arialbd.ttf'})
for name, desc, pattern in [
    ('ARQ_OCULTO', 'Proyeccion discontinua en metros', [.36, .24, -.12]),
    ('ARQ_EJE', 'Eje trazo punto en metros', [.83, .6, -.1, .03, -.1]),
]:
    doc.linetypes.new(name, dxfattribs={'description': desc, 'pattern': pattern})

# Codes are hundredths of a millimetre, not geometry units.
LAYERS = {
    'A_MURO_CORTE': (50, 7, None),
    'A_MURO_RELLENO': (9, 7, None),
    'A_LIMITE': (35, 7, None),
    'A_PUERTA': (25, 7, None),
    'A_VENTANA': (25, 7, None),
    'A_MUEBLE': (18, 7, None),
    'A_MUEBLE_DETALLE': (9, 8, (85, 85, 85)),
    'A_SANITARIO': (18, 7, None),
    'A_ESCALERA': (18, 7, None),
    'A_PISO_TRAMA': (9, 9, (180, 180, 180)),
    'A_PROYECCION': (13, 8, (115, 115, 115)),
    'A_VACIO': (9, 9, (155, 155, 155)),
    'A_JARDIN': (9, 8, (105, 125, 108)),
    'A_TEXTO': (13, 7, None),
    'A_COTA': (13, 7, None),
    'A_ROTULO': (18, 7, None),
    'A_EJE': (13, 8, None),
    'A_VPORT_NOPLOT': (9, 7, None),
}
for name, (weight, color, rgb) in LAYERS.items():
    attrs = dict(color=color, lineweight=weight)
    if rgb: attrs['true_color'] = rgb2int(rgb)
    if name == 'A_PROYECCION': attrs['linetype'] = 'ARQ_OCULTO'
    if name == 'A_EJE': attrs['linetype'] = 'ARQ_EJE'
    if name == 'A_VPORT_NOPLOT': attrs['plot'] = 0
    doc.layers.new(name, dxfattribs=attrs)

ms = doc.modelspace()
space = ms
ox = oy = 0.0
inventory = []
doors = []
room_labels = []
wall_solids = []
quality_checks = {}
wardrobes = []

def pt(x, y): return (round(x+ox, 6), round(y+oy, 6))
def line(x1, y1, x2, y2, layer='A_MUEBLE', **kw):
    return space.add_line(pt(x1,y1), pt(x2,y2), dxfattribs={'layer':layer, **kw})
def poly(points, layer='A_MUEBLE', closed=False, **kw):
    return space.add_lwpolyline([pt(x,y) for x,y in points], close=closed,
                              dxfattribs={'layer':layer, **kw})
def rect(x, y, w, h, layer='A_MUEBLE', **kw):
    return poly([(x,y),(x+w,y),(x+w,y+h),(x,y+h)], layer, True, **kw)
def circle(x, y, r, layer='A_MUEBLE', **kw):
    return space.add_circle(pt(x,y),r,dxfattribs={'layer':layer,**kw})
def arc(x,y,r,a,b,layer='A_MUEBLE_DETALLE'):
    return space.add_arc(pt(x,y),r,a,b,dxfattribs={'layer':layer})
def ellipse(x,y,rx,ry,layer='A_MUEBLE_DETALLE'):
    if rx>=ry:
        return space.add_ellipse(pt(x,y),(rx,0),ry/rx,dxfattribs={'layer':layer})
    return space.add_ellipse(pt(x,y),(0,ry),rx/ry,dxfattribs={'layer':layer})
def text(s,x,y,h=.17,layer='A_TEXTO',bold=False,align='CENTER',rotation=0):
    entity=space.add_text(s,dxfattribs={'height':h,'style':'ARQ_BOLD' if bold else 'ARQ',
                                     'layer':layer,'rotation':rotation})
    entity.set_placement(pt(x,y),align=getattr(TextEntityAlignment,align))
    return entity
def rr(x,y,w,h,r=.06,layer='A_MUEBLE'):
    r=min(r,w/3,h/3)
    k=math.tan(math.pi/8)
    v=[(x+r,y,0),(x+w-r,y,k),(x+w,y+r,0),(x+w,y+h-r,k),
       (x+w-r,y+h,0),(x+r,y+h,k),(x,y+h-r,0),(x,y+r,k)]
    return space.add_lwpolyline([(a+ox,b+oy,c) for a,b,c in v],format='xyb',close=True,
                               dxfattribs={'layer':layer})

def hatch_polygon(points,layer='A_PISO_TRAMA',pattern=None,spacing=.45):
    hatch=space.add_hatch(dxfattribs={'layer':layer})
    if pattern == 'GRID':
        hatch.set_pattern_fill('ARQ_BALDOSA', scale=1, definition=[
            [0,(0,0),(0,spacing),[]], [90,(0,0),(spacing,0),[]]])
    elif pattern:
        hatch.set_pattern_fill(pattern,scale=spacing)
    else: hatch.set_solid_fill(color=256)
    hatch.dxf.hatch_style=0  # Normal: honour all interior islands in DXF/PDF.
    hatch.paths.add_polyline_path([pt(x,y) for x,y in points],is_closed=True,flags=1)
    return hatch

def tiled(x,y,w,h,spacing=.45):
    return hatch_polygon([(x,y),(x+w,y),(x+w,y+h),(x,y+h)],pattern='GRID',spacing=spacing)

def make_block(name, fn):
    global space,ox,oy
    previous=(space,ox,oy)
    space=doc.blocks.new(name)
    ox=oy=0
    fn()
    space,ox,oy=previous

def insert(name,x,y,rot=0,scale=1,kind=None):
    e=space.add_blockref(name,pt(x,y),dxfattribs={'rotation':rot,'xscale':scale,
                                              'yscale':scale,'layer':'A_MUEBLE'})
    if space is ms: inventory.append({'block':name,'handle':e.dxf.handle,'kind':kind or name,
                                     'point':pt(x,y),'rotation':rot})
    return e

# Real reusable CAD blocks with native curves, not raster traces.
def bed():
    rr(-.85,-1.05,1.7,2.1,.06)
    rect(-.9,.96,1.8,.12)
    rr(-.73,.48,.68,.4,.08,'A_MUEBLE_DETALLE')
    rr(.05,.48,.68,.4,.08,'A_MUEBLE_DETALLE')
    poly([(-.8,.34),(-.55,.29),(-.25,.36),(.2,.29),(.8,.34)],'A_MUEBLE_DETALLE')
    line(-.8,-.77,.8,-.77,'A_MUEBLE_DETALLE')
    for x in [-1.25,.95]:
        rr(x,.49,.32,.4,.03)
        circle(x+.16,.69,.07,'A_MUEBLE_DETALLE')
make_block('M_CAMA_DOBLE_170x210',bed)

def sofa():
    rr(-1.15,-.43,2.3,.86,.09)
    rr(-1.08,.21,2.16,.16,.04,'A_MUEBLE_DETALLE')
    rr(-1.08,-.34,.18,.66,.04)
    rr(.9,-.34,.18,.66,.04)
    for x in [-.87,-.29,.29]: rr(x,-.32,.56,.5,.05,'A_MUEBLE_DETALLE')
make_block('M_SOFA_3_PLAZAS',sofa)
def armchair():
    rr(-.4,-.4,.8,.8,.1)
    rr(-.32,.2,.64,.13,.04,'A_MUEBLE_DETALLE')
    rr(-.25,-.28,.5,.46,.06,'A_MUEBLE_DETALLE')
    line(-.32,-.29,-.32,.22); line(.32,-.29,.32,.22)
make_block('M_BUTACA',armchair)
def chair():
    rr(-.22,-.23,.44,.46,.06)
    rr(-.24,.18,.48,.08,.025,'A_MUEBLE_DETALLE')
    line(-.18,-.16,.18,-.16,'A_MUEBLE_DETALLE')
make_block('M_SILLA',chair)
def dining():
    rr(-.48,-.95,.96,1.9,.05)
    rr(-.43,-.9,.86,1.8,.03,'A_MUEBLE_DETALLE')
    for y in [-.62,0,.62]:
        insert('M_SILLA',-.78,y,90)
        insert('M_SILLA',.78,y,270)
make_block('M_COMEDOR_6',dining)
def toilet():
    rr(-.22,.2,.44,.2,.035,'A_SANITARIO')
    rr(-.2,-.32,.4,.52,.16,'A_SANITARIO')
    ellipse(0,-.055,.145,.205,'A_SANITARIO')
    circle(.13,.3,.018,'A_MUEBLE_DETALLE')
make_block('M_INODORO',toilet)
def sink():
    rr(-.3,-.23,.6,.46,.05,'A_SANITARIO')
    ellipse(0,-.02,.22,.14,'A_SANITARIO')
    circle(0,.14,.022,'A_SANITARIO')
    line(0,.13,0,.05,'A_SANITARIO')
    circle(0,-.02,.018,'A_MUEBLE_DETALLE')
make_block('M_LAVATORIO',sink)
def shower():
    rect(-.45,-.45,.9,.9,'A_SANITARIO')
    rect(-.39,-.39,.78,.78,'A_MUEBLE_DETALLE')
    circle(.28,.28,.045,'A_MUEBLE_DETALLE')
    for x,y in [(-.39,-.39),(.39,-.39),(-.39,.39),(.39,.39)]:
        line(x,y,.28,.28,'A_MUEBLE_DETALLE')
    line(-.15,.43,.15,.43,'A_VENTANA')
make_block('M_DUCHA_90',shower)
def cooktop():
    rr(-.3,-.3,.6,.6,.03)
    for x in [-.16,.16]:
        for y in [-.13,.16]:
            circle(x,y,.09,'A_MUEBLE_DETALLE')
            line(x-.12,y,x+.12,y,'A_MUEBLE_DETALLE')
            line(x,y-.12,x,y+.12,'A_MUEBLE_DETALLE')
    for x in [-.15,-.05,.05,.15]: circle(x,-.25,.016,'A_MUEBLE_DETALLE')
make_block('M_COCINA_4_HORNILLAS',cooktop)
def kitchen_sink():
    rr(-.45,-.26,.9,.52,.05)
    rr(-.38,-.18,.4,.36,.06,'A_MUEBLE_DETALLE')
    for x in [.13,.19,.25,.31,.37]: line(x,-.17,x,.17,'A_MUEBLE_DETALLE')
    circle(-.17,0,.025,'A_MUEBLE_DETALLE')
    arc(-.07,.18,.06,0,180,'A_SANITARIO')
make_block('M_FREGADERO',kitchen_sink)
def washer():
    rr(-.3,-.3,.6,.6,.025)
    circle(0,-.01,.22,'A_MUEBLE_DETALLE')
    circle(0,-.01,.18,'A_MUEBLE_DETALLE')
    line(-.27,.23,.27,.23,'A_MUEBLE_DETALLE')
make_block('M_LAVADORA',washer)
def car():
    outline=[(-.78,-2.1),(-.9,-1.8),(-.92,-.9),(-.93,.9),(-.87,1.8),
             (-.69,2.12),(-.42,2.22),(.42,2.22),(.69,2.12),(.87,1.8),
             (.93,.9),(.92,-.9),(.9,-1.8),(.78,-2.1)]
    poly(outline,closed=True)
    rr(-.8,-2.02,1.6,4.08,.3,'A_MUEBLE_DETALLE')
    poly([(-.69,.72),(-.57,1.3),(.57,1.3),(.69,.72)],'A_MUEBLE_DETALLE',True)
    poly([(-.67,-.9),(-.55,-1.44),(.55,-1.44),(.67,-.9)],'A_MUEBLE_DETALLE',True)
    rr(-.62,-.79,1.24,1.41,.11,'A_MUEBLE_DETALLE')
    for x in [-.89,.79]:
        rr(x,1.4,.12,.36,.03,'A_MUEBLE_DETALLE')
        rr(x,-1.65,.12,.36,.03,'A_MUEBLE_DETALLE')
    for x in [-1.02,.9]: rr(x,.47,.12,.28,.02,'A_MUEBLE_DETALLE')
    line(-.6,1.65,.6,1.65,'A_MUEBLE_DETALLE')
    line(-.6,-1.8,.6,-1.8,'A_MUEBLE_DETALLE')
make_block('M_AUTO_SEDAN_190x440',car)
def tree():
    p=[]
    for i in range(96):
        a=i*2*math.pi/96
        r=.48+.045*math.sin(11*a)+.025*math.sin(19*a)
        p.append((r*math.cos(a),r*math.sin(a)))
    poly(p,'A_JARDIN',True)
    circle(0,0,.05,'A_JARDIN')
    for i in range(8):
        a=i*math.pi/4+.17
        line(0,0,.33*math.cos(a),.33*math.sin(a),'A_JARDIN')
make_block('M_ARBUSTO',tree)

def door(x,y,width=.85,angle=0,hand=1):
    """Hinge at x,y. Closed leaf along angle, opens 90 degrees to hand."""
    a=math.radians(angle); b=a+hand*math.pi/2
    # Hinge at the room-side wall face, not buried in the wall centre.
    thick=.2 if abs(x-6.95)<.01 or abs(y-5.85)<.01 or abs(y-4.2)<.01 or abs(y-4.1)<.01 or abs(y-.1)<.01 else .15
    x+=-math.sin(a)*hand*thick/2
    y+=math.cos(a)*hand*thick/2
    vx,vy=math.cos(b),math.sin(b)
    nx,ny=-vy*.035,vx*.035
    poly([(x,y),(x+width*vx,y+width*vy),
          (x+width*vx+nx,y+width*vy+ny),(x+nx,y+ny)],'A_PUERTA',True)
    start,end=(angle,angle+90) if hand>0 else (angle-90,angle)
    arc(x,y,width,start,end,'A_MUEBLE_DETALLE')
    circle(x+width*.87*vx+nx*1.6,y+width*.87*vy+ny*1.6,.018,'A_PUERTA')
    doors.append({'hinge':pt(x,y),'width':width,'angle':angle,'hand':hand})

def window_h(x1,x2,y,t=.2):
    for dy in [-t/2,-.035,.035,t/2]: line(x1,y+dy,x2,y+dy,'A_VENTANA')
    for x in [x1,(x1+x2)/2,x2]: line(x,y-t/2,x,y+t/2,'A_VENTANA')
def window_v(x,y1,y2,t=.2):
    for dx in [-t/2,-.035,.035,t/2]: line(x+dx,y1,x+dx,y2,'A_VENTANA')
    for y in [y1,(y1+y2)/2,y2]: line(x-t/2,y,x+t/2,y,'A_VENTANA')

def closet(x,y,w,h,vertical=False):
    if space is ms:wardrobes.append(box(x+ox,y+oy,x+ox+w,y+oy+h))
    rect(x,y,w,h)
    n=max(2,round((h if vertical else w)/.65))
    for i in range(1,n):
        if vertical: line(x,y+h*i/n,x+w,y+h*i/n,'A_MUEBLE_DETALLE')
        else: line(x+w*i/n,y,x+w*i/n,y+h,'A_MUEBLE_DETALLE')
    if vertical:
        line(x+w*.35,y+.08,x+w*.35,y+h-.08,'A_MUEBLE_DETALLE')
        for k in range(int(h/.18)):
            a=y+.1+k*.18
            poly([(x+.12,a),(x+w*.63,a+.07),(x+w*.63,a-.07),(x+.12,a)],'A_MUEBLE_DETALLE')
    else:
        line(x+.08,y+h*.6,x+w-.08,y+h*.6,'A_MUEBLE_DETALLE')
    # No text embedded in a wardrobe at an unreadable size.

def rug(x,y,w,h):
    rr(x,y,w,h,.06,'A_MUEBLE_DETALLE')
    rr(x+.06,y+.06,w-.12,h-.12,.04,'A_MUEBLE_DETALLE')

def living(x,y,upper=False):
    # A generous circulation strip remains along the east side.
    rug(x-.45,y-.55,3.8,2.9)
    insert('M_SOFA_3_PLAZAS',x+1.4,y+2.0 if upper else y-.12,0 if upper else 180)
    insert('M_SOFA_3_PLAZAS',x+3.3 if upper else x+3,y+1.1,270)
    insert('M_BUTACA',x+.28 if upper else x+.25,y+.9 if upper else y+1.8,90)
    rr(x+1.05 if upper else x+.7,y+.75,1.35,.65,.13)
    rr(x+1.11 if upper else x+.76,y+.81,1.23,.53,.1,'A_MUEBLE_DETALLE')
    circle(x+1.98 if upper else x+1.63,y+1.08,.1,'A_MUEBLE_DETALLE')
    rect(x-.45,y+.1,.28,1.45)
    line(x-.32,y+.2,x-.32,y+1.45,'A_MUEBLE_DETALLE')
    rect(x-.4,y+.35,.06,.95,'A_MUEBLE_DETALLE')
    rr(x+2.35,y-.42,.38,.38,.03)
    circle(x+2.54,y-.23,.11,'A_MUEBLE_DETALLE')

def bath(x,y,w=2.7,h=1.65,door_side='left'):
    tiled(x,y,w,h,.4)
    insert('M_DUCHA_90',x+w-.54,y+h-.54)
    insert('M_INODORO',x+.48,y+h-.4)
    insert('M_LAVATORIO',x+.52 if w<2.3 else x+1.25,y+.36 if w<2.3 else y+h-.36)
    line(x+w-1.04,y+h-1.02,x+w-1.04,y+h-.06,'A_VENTANA')

def garden(x,y,w,h,seed=0):
    rect(x,y,w,h,'A_JARDIN')
    # Sparse dot/short-grass texture, native HATCH, no independent hatch strokes.
    hatch_polygon([(x+.04,y+.04),(x+w-.04,y+.04),(x+w-.04,y+h-.04),(x+.04,y+h-.04)],
                  pattern='ANSI31',spacing=.055)
    for i,(a,b,s) in enumerate([(.18,.23,.63),(.67,.19,.55),(.87,.68,.62),
                               (.43,.82,.65),(.1,.72,.48)]):
        if w*s<.5: continue
        xx=x+.35+(w-.7)*a; yy=y+.35+(h-.7)*b
        insert('M_ARBUSTO',xx,yy,17*i+seed,s,kind='vegetacion')

def void(x,y,w,h,label=True):
    rect(x,y,w,h,'A_VACIO')
    line(x,y,x+w,y+h,'A_VACIO');line(x+w,y,x,y+h,'A_VACIO')
    if label: text('VACÍO',x+w/2,y+h/2,.15,'A_TEXTO')

def stairs(x,y,w=2.75,h=4.15):
    # Two 1.20 m flights, a 0.18 m central well and one common landing.
    fw=(w-.22)/2
    run=h-1.05
    for xx in [x,x+fw+.22]:
        rect(xx,y,fw,run,'A_ESCALERA')
        for i in range(1,10): line(xx,y+i*run/10,xx+fw,y+i*run/10,'A_ESCALERA')
    rect(x,y+run,w,1.05,'A_ESCALERA')
    for xx in [x+fw+.055,x+fw+.165]: line(xx,y+.05,xx,y+run,'A_PUERTA')
    line(x+.55,y+.35,x+.55,y+run+.45,'A_COTA')
    line(x+.55,y+run+.45,x+w-.55,y+run+.45,'A_COTA')
    line(x+w-.55,y+run+.45,x+w-.55,y+.35,'A_COTA')
    poly([(x+w-.66,y+.55),(x+w-.55,y+.35),(x+w-.44,y+.55)],'A_COTA')
    text('SUBE',x+.38,y+.6,.115,rotation=90)
    # Stair break and projection beyond cut plane.
    poly([(x+fw+.22,y+run*.53),(x+fw+.22+.18,y+run*.60),
          (x+w-.18,y+run*.48),(x+w,y+run*.55)],'A_PUERTA')
    text('ESCALERA',x+w/2,y+run+.65,.15,bold=True)

def kitchen1():
    tiled(3.76,11.55,2.08,4.5,.45)
    rect(3.78,11.57,.6,4.45)
    rect(4.38,15.42,1.43,.6)
    insert('M_COCINA_4_HORNILLAS',4.08,13.55)
    insert('M_FREGADERO',4.95,15.72)
    rect(3.8,11.6,.58,.72)
    line(3.85,11.7,4.32,12.22,'A_MUEBLE_DETALLE')
    for yy in [12.4,12.9,14.1,14.7,15.1]: line(3.8,yy,4.35,yy,'A_MUEBLE_DETALLE')
    # Breakfast ledge and stools.
    rect(5.35,13.05,.48,1.6)
    for yy in [13.4,14.12]: circle(5.02,yy,.19)

def kitchen2():
    tiled(1.36,10.45,4.48,1.95,.45)
    rect(1.38,11.78,4.42,.6)
    rect(1.38,10.47,.6,1.31)
    insert('M_COCINA_4_HORNILLAS',2.8,12.08)
    insert('M_FREGADERO',1.68,11.0,90)
    rect(5.12,11.8,.64,.56)
    rect(2.8,10.47,2.0,.6)
    for xx in [2.95,3.7,4.45]: insert('M_SILLA',xx,10.05,180,scale=.8)
    for xx in [3.3,3.9,4.5]: line(xx,11.8,xx,12.35,'A_MUEBLE_DETALLE')

class WallSet:
    def __init__(self): self.boxes=[]; self.specs=[]
    def h(self,y,a,b,t=.15,openings=()):
        self.specs.append(('h',y,a,b,t,list(openings)))
        return self._segments(a,b,openings,lambda lo,hi:(lo,y-t/2,hi,y+t/2))
    def v(self,x,a,b,t=.15,openings=()):
        self.specs.append(('v',x,a,b,t,list(openings)))
        return self._segments(a,b,openings,lambda lo,hi:(x-t/2,lo,x+t/2,hi))
    def _segments(self,a,b,openings,box):
        cur=a
        for lo,hi in sorted(openings):
            assert a<=lo<hi<=b,(a,b,lo,hi)
            if lo>cur: self.boxes.append(box(cur,lo))
            cur=hi
        if cur<b:self.boxes.append(box(cur,b))
    def render(self):
        # Join complete outside corners; retain the exact clear openings.
        self.boxes=[]
        for axis,c,a,b,t,opens in self.specs:
            ext=[0.,0.]
            for j,end in enumerate([a,b]):
                for ax,cc,aa,bb,tt,oo in self.specs:
                    if ax==axis or abs(cc-end)>1e-7 or not aa-1e-7<=c<=bb+1e-7:continue
                    if any(lo+1e-7<c<hi-1e-7 for lo,hi in oo):continue
                    ext[j]=max(ext[j],tt/2)
            aa,bb=a-ext[0],b+ext[1]
            if axis=='h':self._segments(aa,bb,opens,lambda lo,hi:(lo,c-t/2,hi,c+t/2))
            else:self._segments(aa,bb,opens,lambda lo,hi:(c-t/2,lo,c+t/2,hi))
        wall_solids.append(unary_union([box(a+ox,b+oy,c+ox,d+oy) for a,b,c,d in self.boxes]))
        # Exact orthogonal union on the arrangement of rectangle edges. Only
        # exterior/interior boundaries survive: no black junction seams.
        xs=sorted({round(v,5) for b in self.boxes for v in [b[0],b[2]]})
        ys=sorted({round(v,5) for b in self.boxes for v in [b[1],b[3]]})
        filled=set()
        for i in range(len(xs)-1):
            cx=(xs[i]+xs[i+1])/2
            active=[b for b in self.boxes if b[0]-1e-7<cx<b[2]+1e-7]
            for j in range(len(ys)-1):
                cy=(ys[j]+ys[j+1])/2
                if any(b[1]-1e-7<cy<b[3]+1e-7 for b in active): filled.add((i,j))
        edges=set()
        for i,j in filled:
            if (i,j-1) not in filled: edges.add(((i,j),(i+1,j)))
            if (i+1,j) not in filled: edges.add(((i+1,j),(i+1,j+1)))
            if (i,j+1) not in filled: edges.add(((i+1,j+1),(i,j+1)))
            if (i-1,j) not in filled: edges.add(((i,j+1),(i,j)))
        starts=collections.defaultdict(set)
        for a,b in edges: starts[a].add(b)
        rings=[]
        while edges:
            a,b=min(edges); first=a; ring=[a]
            while True:
                edges.remove((a,b)); starts[a].remove(b)
                ring.append(b)
                if b==first:break
                choices=starts[b]
                if not choices: raise RuntimeError('Open wall boundary')
                a,b=b,min(choices)
            points=[(xs[i],ys[j]) for i,j in ring[:-1]]
            # Remove collinear vertices.
            clean=[]
            for n,p in enumerate(points):
                prev=points[n-1]; nex=points[(n+1)%len(points)]
                if abs((p[0]-prev[0])*(nex[1]-p[1])-(p[1]-prev[1])*(nex[0]-p[0]))>1e-10:
                    clean.append(p)
            if len(clean)>2:rings.append(clean)
        for ring in rings:
            poly(ring,'A_MURO_CORTE',True)
        return len(rings)

def common_walls(w,second=False):
    if not second:
        w.v(.1,.1,22.2,.2)
        w.h(22.2,.1,9.9,.2)
        w.v(9.9,.1,22.2,.2,[(5.5,7.3),(9.1,9.85),(13.05,14.55),(17.8,19.55)])
        w.h(.1,.1,9.9,.2,[(1.25,4.25),(4.55,5.55)])
    else:
        rect(0,0,10,22.3,'A_LIMITE')
        # Remaining ground-floor envelope is shown as a light projection.
        rect(.1,.1,9.8,22.1,'A_PROYECCION')
        w.v(9.9,4.2,20.2,.2,[(5.5,7.3),(9.1,9.85),(13.05,14.55),(17.8,19.55)])
    w.v(1.25,4.2 if second else 5.85,19.05 if second else 21.1,.2,
        [(6.6,8.2),(12.85,14.4),(16.85,18.35)]+([] if second else [(19.55,20.4)]))
    w.v(6.95,4.2 if second else 4.1,17.25,.2,([(4.35,5.2)] if second else [])+[(8.9,9.65),(10.75,11.7),(12.9,13.75),(15.65,16.4)])
    w.h(16.15,1.25,5.95,.15,[(4.85,5.75)])
    w.h(19.05,1.25,5.95,.2,[(2.05,3.55),(4.5,5.35)])
    w.v(5.95,16.15,20.2,.15)
    w.h(20.2,5.95,9.9,.2,[(6.65,8.85)])
    w.h(17.25,5.95,9.9,.15,[(6.1,6.85)])
    w.h(15.35,6.95,9.9,.15)
    w.h(12.5,6.95,9.9,.2,[(7.4,9.25)])
    w.h(10.35,6.95,9.9,.2,[(7.3,8.15)])
    w.h(8.65,6.95,9.9,.15)
    w.v(8.25,8.65,10.35,.15,[(8.9,9.6)])
    w.h(4.2 if second else 4.1,6.95,9.9,.2,[] if second else [(7.2,8.15)])
    for x,y1,y2 in [(9.9,5.5,7.3),(9.9,9.1,9.85),(9.9,13.05,14.55),(9.9,17.8,19.55),
                     (1.25,6.6,8.2),(1.25,12.85,14.4),(1.25,16.85,18.35)]: window_v(x,y1,y2)
    window_h(6.65,8.85,20.2)
    window_h(7.4,9.25,12.5)
    window_h(2.05,3.55,19.05)
    window_h(7.3,8.15,10.35)
    # Bedroom and service doors open into clear wall-side space.
    door(4.85,16.15,.9,0,1)
    door(6.1,17.25,.75,0,1)
    door(6.95,12.9,.85,90,-1)
    door(6.95,15.65,.75,90,-1)
    door(6.95,8.9,.75,90,-1)
    door(8.25,8.9,.7,90,-1)
    if second:door(6.95,4.35,.85,90,1)
    else:door(7.2,4.1,.95,0,1)

def bedrooms():
    # Left bed oriented to match the horizontal headboard in the reference.
    insert('M_CAMA_DOBLE_170x210',2.95,17.62,90)
    closet(5.21,17.1,.6,1.75,True)
    insert('M_CAMA_DOBLE_170x210',8.35,18.7,90,scale=.9)
    closet(6.12,18.3,.6,1.6,True)
    insert('M_CAMA_DOBLE_170x210',8.75,14.05,90,scale=.85)
    closet(7.08,14.25,.55,1.0,True)

def label(name,x,y,note=None):
    text(name,x,y,.15,bold=False)
    if note:text(note,x,y-.24,.115)
    room_labels.append({'name':name,'point':pt(x,y)})

def dimension(a,b,base,vertical=False,txt=None):
    dim=ms.add_linear_dim(base=pt(*base),p1=pt(*a),p2=pt(*b),angle=90 if vertical else 0,
                         text=txt or '<>', dxfattribs={'layer':'A_COTA'},
                         override={'dimtxt':.16,'dimasz':.105,'dimexo':.09,'dimexe':.1,
                                   'dimgap':.065,'dimtad':1,'dimdec':2,'dimzin':0,
                                   'dimtxsty':'ARQ','dimblk':'ARCHTICK','dimclrd':256,
                                   'dimclre':256,'dimclrt':256,'dimlwd':13,'dimlwe':13})
    dim.render()

def annotations(second=False):
    dimension((0,0),(10,0),(0,-.65),txt='10.00')
    dimension((0,0),(0,22.3),(-1.05,0),True,txt='22.30 REF.')
    dimension((0,22.3),(1.25,22.3),(0,22.86),txt='1.25')
    dimension((1.25,22.3),(6.95,22.3),(0,22.86),txt='5.70 REF.')
    dimension((6.95,22.3),(10,22.3),(0,22.86),txt='3.05 REF.')
    # Level markers are graphic datums, not surveyed elevations.
    for xx,yy,s in [(6.25,5.25 if second else 6.1,'NIVEL 2' if second else 'NIVEL 1')]:
        circle(xx,yy,.08,'A_COTA')
        line(xx-.14,yy,xx+.14,yy,'A_COTA');line(xx,yy-.14,xx,yy+.14,'A_COTA')
        text(s,xx,yy-.27,.11)

def floor_one():
    w=WallSet();common_walls(w)
    w.h(5.85,1.25,6.95,.2,[(1.7,4.3),(5.8,6.75)])
    window_h(1.7,4.3,5.85)
    door(6.75,5.85,.95,180,-1)
    w.h(11.45,1.25,5.95,.15,[(1.55,2.35),(4.6,5.45)])
    w.v(3.65,11.45,16.15,.15,[(13.0,14.5)])
    window_v(3.65,13,14.5,.15)
    w.v(5.95,11.45,16.15,.15,[(12.0,12.85)])
    w.h(12.4,1.25,3.65,.15,[(1.55,2.35)])
    w.h(15.4,1.25,3.65,.15)
    w.h(21.1,1.25,3.65,.15)
    w.v(3.65,19.05,21.1,.15,[(19.2,19.95)])
    door(3.65,19.2,.75,90,1)
    window_v(1.25,19.55,20.4)
    door(1.55,11.45,.8,0,-1)
    door(5.95,12,.85,90,1)
    door(1.55,12.4,.8,0,1)
    w.render()
    # Native tile and concrete hatches; all furniture remains block-editable.
    tiled(.25,.25,.78,21.75,.48)
    tiled(1.36,11.55,2.18,.74,.4)
    tiled(1.36,12.5,2.18,2.8,.55)
    insert('M_LAVADORA',2.7,11.92)
    insert('M_FREGADERO',3.27,11.92,scale=.55)
    kitchen1()
    bath(1.36,19.17,2.17,1.8)
    bath(7.08,15.46,2.68,1.66)
    # Compact guest WC with a clear opening and no fixture collision.
    tiled(8.36,8.76,1.4,1.47,.35)
    insert('M_INODORO',9.38,9.8)
    insert('M_LAVATORIO',8.75,10.02,scale=.85)
    closet(7.08,9.75,1.04,.48)
    stairs(7.08,5.3,2.68,3.2)
    living(1.92,6.62)
    insert('M_COMEDOR_6',3.65,10.27,90)
    bedrooms()
    garden(3.79,20.4,5.96,1.6,5)
    tiled(3.79,19.23,2.0,.95,.5)
    garden(7.09,10.5,2.66,1.84,3)
    garden(7.14,.3,2.54,2.0,4)
    tiled(4.4,.3,2.43,2.02,.5)
    # Front access: paving and broad entrance treads.
    tiled(4.4,2.42,2.43,1.6,.5)
    for i in range(7): line(4.4,4.2+i*.23,6.82,4.2+i*.23,'A_ESCALERA')
    for x in [4.4,6.82]:line(x,4.2,x,5.65,'A_ESCALERA')
    poly([(5.62,4.45),(5.62,5.4),(5.52,5.22),(5.62,5.4),(5.72,5.22)],'A_COTA')
    # Low garden curb / garage edging.
    rect(4.24,.3,.12,3.75,'A_LIMITE')
    insert('M_AUTO_SEDAN_190x440',2.75,2.85)
    for y in [.12,.18]:line(1.25,y,4.25,y,'A_PUERTA')
    for i in range(16):line(1.25+i*3/15,.12,1.25+i*3/15,.18,'A_PUERTA')
    door(4.55,.1,1.0,0,1)
    label('SALA',4.3,8.62)
    label('COMEDOR',5.35,10.25)
    label('COCINA',4.98,12.65)
    label('PATIO',2.45,14.9,'de servicio')
    label('LAV.',1.92,12.15)
    label('DORMITORIO 01',3.55,18.72)
    label('DORMITORIO 02',8.75,19.82)
    label('DORMITORIO 03',8.85,12.75)
    label('BAÑO',2.45,19.7)
    label('BAÑO',8.35,15.68)
    label('S.H.',9.35,9.15)
    label('JARDÍN',8.45,10.65)
    label('JARDÍN POSTERIOR',7.5,21.75)
    label('COCHERA',2.77,5.31)
    text('ACCESO',5.65,3.15,.15,bold=True)
    text('JARDÍN',8.5,.42,.15,bold=False)
    annotations()

def floor_two():
    w=WallSet();common_walls(w,True)
    w.h(4.2,1.25,6.95,.2,[(1.7,4.55),(5.55,6.5)])
    window_h(1.7,4.55,4.2)
    door(6.5,4.2,.95,180,-1)
    w.h(10.35,1.25,5.95,.15,[(2.6,5.75)])
    w.h(12.5,1.25,6.95,.2,[(1.8,3.1),(6.1,6.85)])
    window_h(1.8,3.1,12.5)
    w.v(5.95,10.35,16.15,.15,[(10.75,11.6),(12.65,13.45),(14.4,15.25)])
    w.v(3.65,12.5,16.15,.15)
    w.h(14.2,3.65,5.95,.15)
    w.h(15.35,1.25,3.65,.15)
    door(5.95,10.75,.85,90,1)
    door(5.95,12.65,.8,90,1)
    w.render()
    living(1.92,7.25,upper=True)
    insert('M_COMEDOR_6',3.9,5.85)
    kitchen2()
    bedrooms()
    bath(7.08,15.46,2.68,1.66)
    tiled(3.76,12.61,2.08,1.48,.4)
    insert('M_DUCHA_90',4.25,13.55,scale=.86)
    insert('M_INODORO',5.35,13.7)
    insert('M_LAVATORIO',4.35,12.93,180,scale=.85)
    tiled(8.36,8.76,1.4,1.47,.35)
    insert('M_INODORO',9.38,9.8)
    insert('M_LAVATORIO',8.75,10.02,scale=.85)
    closet(7.08,9.75,1.04,.48)
    stairs(7.08,5.3,2.68,3.2)
    # Compact upper internal flight, distinct from the common entrance stair.
    for i in range(8):line(3.77,14.32+i*.22,5.82,14.32+i*.22,'A_ESCALERA')
    line(4.7,14.45,4.7,15.85,'A_COTA')
    poly([(4.6,15.65),(4.7,15.85),(4.8,15.65)],'A_COTA')
    text('SUBE',5.32,15.2,.115,rotation=90)
    void(.15,.15,9.7,2.7)
    void(.15,2.85,.95,19.3,False)
    text('VACÍO LATERAL',.62,10.2,.13,rotation=90)
    void(1.38,12.65,2.13,2.54)
    void(7.08,10.5,2.68,1.84)
    void(1.4,19.25,4.4,2.7)
    void(5.95,20.36,3.9,1.6)
    # Ground floor garden/roof contours, clear in a light projection.
    poly([(1.4,19.25),(3.65,19.25),(3.65,21.1),(1.4,21.1)],'A_PROYECCION',True)
    tiled(1.4,2.95,5.4,1.08,.28)
    for yy in [2.88,2.96]:line(1.25,yy,6.95,yy,'A_PUERTA')
    for xx in [1.25+i*.45 for i in range(14)]:line(xx,2.88,xx,2.96,'A_PUERTA')
    for xx in [1.25,6.95]: line(xx,2.88,xx,4.12,'A_PUERTA')
    label('SALA',3.8,7.35)
    label('COMEDOR',5.5,5.92)
    label('COCINA',4.2,11.3)
    label('DORMITORIO 01',3.55,18.72)
    label('DORMITORIO 02',8.75,19.82)
    label('DORMITORIO 03',8.85,12.75)
    label('BAÑO',8.35,15.68)
    label('BAÑO',5.38,12.8)
    label('S.H.',9.35,9.15)
    label('TERRAZA',4.2,3.4)
    annotations(True)

floor_one()
ox=16
floor_two()
ox=oy=0

def polygon_of(e):
    try:
        if e.dxftype()=='CIRCLE':return Point(e.dxf.center.x,e.dxf.center.y).buffer(e.dxf.radius,quad_segs=24)
        p=make_path(e)
        if not p.is_closed:return None
        result=Polygon([(v.x,v.y) for v in p.flattening(.001)])
        return result.buffer(0) if not result.is_valid else result
    except (TypeError,ValueError,AttributeError):return None

# Opaque furnishings are achieved geometrically: trim the floor pattern out
# of their complete footprints. No white solids, display-order tricks or
# independent strokes are substituted for native HATCH entities.
footprints=[];block_footprints=[]
for ref in list(ms.query('INSERT')):
    parts=[]
    for e in disassemble.recursive_decompose([ref]):
        q=polygon_of(e)
        if q is not None and q.area>1e-7:parts.append(q)
    if parts:
        shape=unary_union(parts).buffer(0)
        footprints.append(shape)
        block_footprints.append((ref.dxf.name,shape))
for e in list(ms):
    if e.dxf.layer=='A_MUEBLE' and e.dxftype()!='INSERT':
        q=polygon_of(e)
        if q is not None and q.area>.001:footprints.append(q)
occluders=unary_union(footprints)
symbol_occluders=unary_union([shape for name,shape in block_footprints])
clipped_hatches=0;islands=0
for h in list(ms.query('HATCH')):
    if h.dxf.layer!='A_PISO_TRAMA':continue
    outlines=[Polygon([(v[0],v[1]) for v in p.vertices]) for p in h.paths if p.path_type_flags&1]
    area=unary_union(outlines).difference(occluders.buffer(.004))
    area=area.difference(unary_union(wall_solids))
    h.paths.clear()
    polygons=[area] if area.geom_type=='Polygon' else list(area.geoms)
    for q in polygons:
        if q.area<.001:continue
        h.paths.add_polyline_path(list(q.exterior.coords)[:-1],is_closed=True,flags=1)
        for hole in q.interiors:
            if Polygon(hole).area<.0001:continue
            h.paths.add_polyline_path(list(hole.coords)[:-1],is_closed=True,flags=0)
            islands+=1
    if not len(h.paths):ms.delete_entity(h)
    clipped_hatches+=1

# Remove floor-level rug and cabinet divider strokes where furniture covers
# them. Internal details inside the symbol blocks remain intact.
clipped_strokes=0
for e in list(ms):
    if e.dxf.layer!='A_MUEBLE_DETALLE' or e.dxftype() not in ['LINE','LWPOLYLINE']:continue
    p=make_path(e)
    path=LineString([(v.x,v.y) for v in p.flattening(.001)])
    bounds=path.bounds
    is_rug=p.is_closed and bounds[2]-bounds[0]>3 and bounds[3]-bounds[1]>2.5
    cut=path.difference((occluders if is_rug else symbol_occluders).buffer(.004))
    if abs(cut.length-path.length)<1e-5:continue
    ms.delete_entity(e);clipped_strokes+=1
    pieces=[cut] if cut.geom_type=='LineString' else list(getattr(cut,'geoms',[]))
    for piece in pieces:
        if piece.geom_type=='LineString' and piece.length>.004:
            poly(list(piece.coords),'A_MUEBLE_DETALLE')

# Measure actual door-swing sectors against wall masses and furnishing
# footprints, rather than considering a successful API call a design check.
wall_mass=unary_union(wall_solids)
door_conflicts=[]
for d in doors:
    x,y=d['hinge'];a=math.radians(d['angle']);r=d['width']*.985
    sweep=Polygon([(x,y)]+[(x+r*math.cos(a+d['hand']*math.pi/2*i/48),
                            y+r*math.sin(a+d['hand']*math.pi/2*i/48)) for i in range(49)])
    wa=sweep.intersection(wall_mass).area
    fa=sweep.intersection(occluders).area
    if wa>.0002 or fa>.0002:door_conflicts.append({'door':d,'wallArea':wa,'furnitureArea':fa})
furniture_conflicts=[]
for name,shape in block_footprints:
    area=shape.intersection(wall_mass).area
    if area>.0002:furniture_conflicts.append({'block':name,'area':area,'bounds':list(shape.bounds)})
quality_checks.update(clippedFloorHatches=clipped_hatches,hatchIslands=islands,
                      clippedUnderlayStrokes=clipped_strokes,
                      doorConflicts=door_conflicts,furnitureWallConflicts=furniture_conflicts,
                      wallHatches=0)
wardrobe_conflicts=[]
for name,shape in block_footprints:
    if name=='M_ARBUSTO':continue
    for closet_shape in wardrobes:
        area=shape.intersection(closet_shape).area
        if area>.0002:wardrobe_conflicts.append({'block':name,'area':area})
quality_checks['wardrobeFurnitureConflicts']=wardrobe_conflicts
pair_conflicts=[]
for i,(name,a) in enumerate(block_footprints):
    if name=='M_ARBUSTO':continue
    for other,b in block_footprints[i+1:]:
        if other=='M_ARBUSTO':continue
        area=a.intersection(b).area
        if area>.0002:pair_conflicts.append({'first':name,'second':other,'area':area})
quality_checks['furniturePairConflicts']=pair_conflicts
(OUT/'revision_geometrica_R01.json').write_text(json.dumps(quality_checks,ensure_ascii=False,indent=2),encoding='utf-8')
print('Geometry review: '+json.dumps(quality_checks,ensure_ascii=False),flush=True)

# A real A2 paperspace layout; model geometry stays full-size in metres.
ps=doc.layouts.get('Layout1')
doc.layouts.rename('Layout1','A-01_PLANTAS_A2')
ps.page_setup(size=(594,420),margins=(0,0,0,0),units='mm',scale=(1,1),
              device='DWG To PDF.pc3',name='ISO_A2')
ps.dxf_layout.dxf.plot_layout_flags |= 128 | 512 # Plot lineweights; plot paperspace last.
ps.dxf_layout.dxf.current_style_sheet=''
for off,cx in [(0,116),(16,315)]:
    vp=ps.add_viewport(center=(cx,210),size=(188,330),view_center_point=(5+off,11.15),
                       view_height=24.75,status=2,dxfattribs={'layer':'A_VPORT_NOPLOT'})
    vp.dxf.flags |= 16384 # Lock viewport scale and position.

space=ps
rect(10,10,574,400,'A_ROTULO',lineweight=35)
line(10,397,584,397,'A_ROTULO')
text('VIVIENDA  /  DOS NIVELES',18,405,3.4,bold=True,align='LEFT')
text('RECONSTRUCCIÓN ARQUITECTÓNICA DE REFERENCIA',578,405,2.5,align='RIGHT')
for cx,num,title in [(116,'01','PRIMER PISO'),(315,'02','SEGUNDO PISO')]:
    circle(cx-42,385,4,'A_ROTULO')
    text(num,cx-42,383.8,2.7,bold=True)
    text(title,cx-30,383.5,4.1,bold=True,align='LEFT')
    text('ESCALA 1:75  |  UNIDADES: m',cx,372.5,2.05)

# Sidebar has a regular grid, a generous margin, and readable technical notes.
line(424,46,424,391,'A_ROTULO',lineweight=13)
sx=435
def sidebar_heading(s,y):
    text(s,sx,y,2.8,bold=True,align='LEFT')
    line(sx,y-3,575,y-3,'A_ROTULO',lineweight=13)
def note(lines,y,h=2.25,step=4.4):
    for s in lines:text(s,sx,y,h,align='LEFT');y-=step
    return y
sidebar_heading('CRITERIO DE RECONSTRUCCIÓN',383)
note(['Frente indicado por el usuario: 10.00 m.',
      'Fondo adoptado por proporción: 22.30 m.',
      'Cotas interiores y distribución aproximadas.',
      'Referencia: imagen de dos plantas.',
      'Confirmar medidas con un levantamiento.'],373)
sidebar_heading('VALORACIÓN DE LÍNEAS',338)
rows=[('Muros cortados',.50),('Límite / elementos principales',.35),
      ('Puertas y ventanas',.25),('Mobiliario y escaleras',.18),
      ('Textos, cotas y proyecciones',.13),('Detalles, tramas y vegetación',.09)]
for i,(s,mm) in enumerate(rows):
    yy=327-i*9
    line(sx,yy,sx+20,yy,'A_ROTULO',lineweight=round(mm*100))
    text(s,sx+25,yy-.9,2.05,align='LEFT')
    text(f'{mm:.2f} mm',575,yy-.9,2.05,align='RIGHT')
sidebar_heading('SÍMBOLOS Y CONVENCIONES',261)
line(sx,249,sx+24,249,'A_PROYECCION',ltscale=7)
text('Proyección / elemento sobre corte',sx+29,248.2,2.05,align='LEFT')
rect(sx,232,22,9,'A_VACIO')
line(sx,232,sx+22,241,'A_VACIO');line(sx+22,232,sx,241,'A_VACIO')
text('Vacío de doble altura / patio inferior',sx+29,235,2.05,align='LEFT')
line(sx,222,sx+22,222,'A_VENTANA')
line(sx,223.5,sx+22,223.5,'A_VENTANA')
text('Carpintería y acristalamiento',sx+29,221.5,2.05,align='LEFT')
sidebar_heading('CONTENIDO DEL ARCHIVO CAD',205)
note(['Geometría a tamaño real en metros.',
      'Mobiliario agrupado en bloques reutilizables.',
      'TEXT, DIMENSION, INSERT y HATCH nativos.',
      'Propiedades gráficas organizadas por capa.',
      'Aberturas y giros de puertas representados.',
      'La imagen no permite verificar estructura,',
      'instalaciones ni dimensiones definitivas.'],194)
sidebar_heading('CONTROL DE ESCALA',151)
text('Imprimir A2 al 100 %, sin ajustar al papel.',sx,141,2.2,align='LEFT')
text('0',sx,129,2.1,align='LEFT')
for i in range(5):
    xx=sx+i*13.333333
    rect(xx,120,13.333333,3,'A_ROTULO',lineweight=13)
    if i%2==0:hatch_polygon([(xx,120),(xx+13.333333,120),(xx+13.333333,123),(xx,123)],'A_ROTULO')
    text(str(i+1),xx+13.333333,127,2.1)
text('m',sx+72,127,2.1)
text('ESCALA GRÁFICA 1:75',sx,113,2.2,bold=True,align='LEFT')
sidebar_heading('ESTADO DEL DOCUMENTO',95)
note(['Estudio gráfico de distribución.',
      'Medidas referenciales, excepto el frente.',
      'No constituye un plano para construcción.'],84,h=2.15)

# Bottom title block, intentionally restrained and without invented signatories.
line(10,42,584,42,'A_ROTULO',lineweight=25)
for xx in [340,442,511]:line(xx,10,xx,42,'A_ROTULO')
text('ARQUITECTURA',18,34,2.3,bold=True,align='LEFT')
text('PLANTAS AMOBLADAS',18,22,5.0,bold=True,align='LEFT')
text('REPRODUCCIÓN SEGÚN IMAGEN PROPORCIONADA',18,14.5,2.2,align='LEFT')
text('FORMATO / ESCALA',348,34,2.05,align='LEFT')
text('A2  /  1:75',348,22,4.0,bold=True,align='LEFT')
text('MODELO: METROS',348,14.5,2.05,align='LEFT')
text('FECHA / REVISIÓN',450,34,2.05,align='LEFT')
text('08.11.2026',450,23,3.5,bold=True,align='LEFT')
text('R01 - REVISADO',450,14.5,2.05,align='LEFT')
text('LÁMINA',519,34,2.1,align='LEFT')
text('A-01',519,17,7.2,bold=True,align='LEFT')

# Modelspace default view includes both floors. Paper layout is initially active.
doc.set_modelspace_vport(height=26,center=(13,11.15))
doc.layouts.set_active_layout('A-01_PLANTAS_A2')
doc.header['$TILEMODE']=0
file=OUT/'Vivienda_10m_plantas_A01.dxf'
doc.saveas(file)

audit=doc.audit()
assert not audit.errors,[(e.code,e.message) for e in audit.errors]
counts=collections.Counter(e.dxftype() for e in ms)
assert counts['HATCH']>=10 and counts['INSERT']>=50
manifest={'drawing':file.name,'frontageMetres':10,'depthMetres':22.3,
          'depthSource':'inferred from reference image; unverified',
          'units':'Metres','paper':'A2 594 x 420 mm','plotScale':'1:75',
          'modelEntityCount':sum(counts.values()),'modelEntityTypes':dict(counts),
          'blockDefinitions':len([b for b in doc.blocks if b.name.startswith('M_')]),
          'furniture':inventory,'doors':doors,'roomLabels':room_labels,
          'layers':{name:{'lineweightMm':v[0]/100,'colorIndex':v[1]} for name,v in LAYERS.items()},
          'auditErrors':len(audit.errors),'auditFixes':len(audit.fixes)}
(OUT/'verificacion_geometria.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')

if '--no-pdf' not in sys.argv:
    from ezdxf.addons.drawing import Frontend,RenderContext,layout,config
    from ezdxf.addons.drawing.pymupdf import PyMuPdfBackend
    ctx=RenderContext(doc)
    backend=PyMuPdfBackend()
    cfg=config.Configuration(background_policy=config.BackgroundPolicy.WHITE,
                             color_policy=config.ColorPolicy.COLOR,
                             lineweight_policy=config.LineweightPolicy.ABSOLUTE,
                             min_lineweight=.025)
    Frontend(ctx,backend,config=cfg).draw_layout(ps,finalize=True)
    pdf=backend.get_pdf_bytes(layout.Page(594,420,layout.Units.mm),
                             settings=layout.Settings(fit_page=False,scale=1),
                             render_box=BoundingBox2d([(0,0),(594,420)]))
    (OUT/'Vivienda_10m_plantas_A01.pdf').write_bytes(pdf)
    import fitz
    with fitz.open(stream=pdf,filetype='pdf') as rendered:
        rendered[0].get_pixmap(matrix=fitz.Matrix(1.6,1.6),alpha=False).save(str(OUT/'Vista_previa_A01.png'))
        assert len(rendered)==1
        # Backend rounds PDF media to integer points; set exact ISO A2 size
        # without rescaling the vector content or changing lineweights.
        rendered[0].set_mediabox(fitz.Rect(0,0,594*72/25.4,420*72/25.4))
        pdf=rendered.tobytes(garbage=3,deflate=True)
    (OUT/'Vivienda_10m_plantas_A01.pdf').write_bytes(pdf)
    with fitz.open(stream=pdf,filetype='pdf') as rendered:
        rendered[0].get_pixmap(matrix=fitz.Matrix(1.6,1.6),alpha=False).save(str(OUT/'Vista_previa_A01.png'))
        assert abs(rendered[0].rect.width/72*25.4-594)<.1
    print('PDF A2 generated and rendered',flush=True)

print(json.dumps({k:manifest[k] for k in ['drawing','modelEntityCount','modelEntityTypes','blockDefinitions','auditErrors','auditFixes']},ensure_ascii=False))
