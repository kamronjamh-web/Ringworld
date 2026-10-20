"""Weather Lung, editable Blender macro asset. Blender 4.3+.
blender -b --python source/build_weather_lung.py -- --out PATH --render
Metres, Z-up, +X circumferential tangent, +Y across Ringworld. Architecture only
in exports. Preview landscape, water and clouds are explicitly excluded.
"""
import bpy, bmesh, math, json, random, pathlib, sys, argparse
from mathutils import Vector, Matrix, Euler
from mathutils.bvhtree import BVHTree
P=argparse.ArgumentParser();P.add_argument('--out',default=str(pathlib.Path(__file__).resolve().parent.parent));P.add_argument('--render',action='store_true');P.add_argument('--preview-only',action='store_true');P.add_argument('--arch-height',type=float,default=4800);P.add_argument('--quick',action='store_true');P.add_argument('--export-anim',action='store_true');P.add_argument('--only',default='');P.add_argument('--anim-preview',action='store_true')
a=P.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []);OUT=pathlib.Path(a.out)
for p in ['exports/sectors','exports/modules','exports/chunks','previews','docs']:(OUT/p).mkdir(parents=True,exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True);random.seed(291)
S=bpy.context.scene;S.unit_settings.system='METRIC';S.unit_settings.scale_length=1.0
M={};ARCH=[];SECTORS={};MOVABLE=[]
def coll(name,parent=None):
 c=bpy.data.collections.new(name);(parent.children if parent else S.collection.children).link(c);return c
A=coll('01_ARCHITECTURE_Export');V=coll('02_MOVABLE_VANES_Export');PX=coll('03_FAR_PROXY_Hidden');ENV=coll('90_PREVIEW_ONLY_Landscape_Water_Clouds');BR=coll('04_BREATH_RIG');WT=coll('05_WATER_Export');RIG=coll('99_PREVIEW_ONLY_Cameras_Lights')
for i in range(12):SECTORS[i]=coll(f'Sector_{i:02d}',A)
def mat(n,c,metal=0,rough=.5,emission=0):
 m=bpy.data.materials.new(n);m.diffuse_color=(*c,1);m.use_backface_culling=True;m.use_nodes=True;p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=(*c,1);p.inputs['Metallic'].default_value=metal;p.inputs['Roughness'].default_value=rough
 if emission:p.inputs['Emission Color'].default_value=(*c,1);p.inputs['Emission Strength'].default_value=emission
 M[n]=m;return m
mat('Graphite_Structural_Alloy',(.054,.074,.081),.62,.36);mat('Ceramic_Ivory',(.68,.665,.60),.24,.42);mat('Ceramic_Pearl',(.84,.805,.71),.20,.42);mat('Ceramic_Slate',(.28,.36,.37),.48,.44);mat('Edge_Titanium',(.33,.43,.45),.76,.3);mat('Cavity_Dark',(.016,.027,.034),.28,.6);mat('Teal_Inlay',(.045,.57,.50),.4,.3,1.5);mat('Window_Graphite',(.025,.12,.135),.7,.18);mat('Gill_Cavity_Glow',(.012,.20,.18),.3,.5,.45);mat('Heat_Exchanger_Glow',(1.0,.42,.12),.2,.4,.001);mat('Mirror_Solar',(.55,.72,.78),1.0,.04);mat('Basin_Water',(.55,.86,.92),0,.03);mat('Preview_Beam',(1.0,.85,.55),0,.5,.001);mat('Preview_Hotspot',(1.0,.8,.45),0,.5,.001);[mat('Nozzle_Glow_%02d'%k,(.05,.75,.66),.3,.4,.4) for k in range(12)];mat('Ground_Moss',(.065,.105,.077),0,.94);mat('Ground_Rock',(.15,.18,.16),0,.95);mat('Preview_Water',(.034,.14,.17),.22,.19);mat('Preview_Foam',(.47,.66,.66),.1,.46);mat('Preview_Cloud',(.73,.81,.83),0,.85)
MATLIST=list(M.values())
_bw=M['Basin_Water'].node_tree.nodes['Principled BSDF'];_bw.inputs['Transmission Weight'].default_value=.93;_bw.inputs['IOR'].default_value=1.33;M['Preview_Beam'].node_tree.nodes['Principled BSDF'].inputs['Alpha'].default_value=.04;M['Preview_Hotspot'].node_tree.nodes['Principled BSDF'].inputs['Alpha'].default_value=.7
def mesh(n,vs,fs,m='Graphite_Structural_Alloy',c=None):
 c=c or SECTORS[0];me=bpy.data.meshes.new(n);me.from_pydata(vs,[],fs);me.update();ob=bpy.data.objects.new(n,me);c.objects.link(ob);me.materials.append(M[m]);bm=bmesh.new();bm.from_mesh(me);bmesh.ops.recalc_face_normals(bm,faces=bm.faces);bm.to_mesh(me);bm.free()
 if c in SECTORS.values() or c==V:ARCH.append(ob)
 return ob

def prism(n,poly,z0,z1,m,c):
 # Shape may be oblique; a true closed solid, with recalc outward normals.
 l=len(poly);vs=[(x,y,z0) for x,y in poly]+[(x,y,z1) for x,y in poly];fs=[tuple(reversed(range(l))),tuple(range(l,2*l))]+[(i,(i+1)%l,(i+1)%l+l,i+l) for i in range(l)]
 return mesh(n,vs,fs,m,c)
def cube(n,loc,scale,m,c,rz=0):
 x,y,z=loc;dx,dy,dz=[v*.5 for v in scale];co=math.cos(rz);si=math.sin(rz)
 poly=[(x+px*co-py*si,y+px*si+py*co) for px,py in [(-dx,-dy),(dx,-dy),(dx,dy),(-dx,dy)]]
 return prism(n,poly,z-dz,z+dz,m,c)
def beam(n,p,q,width,depth,m,c):
 p,q=Vector(p),Vector(q);axis=(q-p).normalized();side=axis.cross(Vector((0,0,1)))
 if side.length<.001:side=Vector((1,0,0))
 side.normalize();up=axis.cross(side).normalized();v=[]
 for end in [p,q]:
  for u,w in [(-1,-1),(1,-1),(1,1),(-1,1)]:v.append(tuple(end+side*u*width/2+up*w*depth/2))
 return mesh(n,v,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],m,c)
def sector(theta):return int((theta%(2*math.pi))/(2*math.pi)*12)%12
def pt(t,r,z=0):
 # r=0 inner basin lip, r=1 outer 28 x18 km extent.
 return ((9900+4100*r)*math.cos(t),(5650+3350*r)*math.sin(t),z)
def wedge(n,t0,t1,r0,r1,z0,z1,m,c,steps=4):
 vs=[]
 for z in [z0,z1]:
  for r in [r0,r1]:
   for j in range(steps+1):vs.append(pt(t0+(t1-t0)*j/steps,r,z))
 k=steps+1;fs=[]
 for j in range(steps):fs +=[(j,j+1,k+j+1,k+j),(2*k+j,3*k+j,3*k+j+1,2*k+j+1),(j,2*k+j,2*k+j+1,j+1),(k+j,k+j+1,3*k+j+1,3*k+j)]
 fs +=[(0,k,3*k,2*k),(k-1,2*k-1,4*k-1,3*k-1)]
 return mesh(n,vs,fs,m,c)
def radialbeam(n,t,r0,r1,z0,z1,w,d,m,c):return beam(n,pt(t,r0,z0),pt(t,r1,z1),w,d,m,c)
def wing(n,t0,t1,r0,r1,zi,zo,thick,m,c,skew=.03):
 # Oblique, sloping polygon panels: broad tapered tongues, not concentric seating tiers.
 vs=[]
 for dz in [-thick,0]:
  vs += [pt(t0,r0,zi+dz),pt(t1,r0,zi+dz),pt(t1+skew,r1,zo+dz),pt(t0+skew,r1,zo+dz)]
 return mesh(n,vs,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],m,c)
def wpt(t0,t1,r0,r1,zi,zo,sk,u,v):
 # Bilinear point on a wing()'s top face (u across, v root to tip).
 A,B,D,E=[Vector(q) for q in (pt(t0,r0,zi),pt(t1,r0,zi),pt(t0+sk,r1,zo),pt(t1+sk,r1,zo))];return (A*(1-u)+B*u)*(1-v)+(D*(1-u)+E*u)*v
def pm(o):return (pm(o.parent) if o.parent else Matrix.Identity(4))@o.matrix_basis
def adopt(ob,pv):
 # Re-parent a mesh built in world space under a pivot at rest, keeping its world pose (pivot rest rotation may be non-identity).
 ob.parent=pv;ob.matrix_parent_inverse=Matrix.Identity(4);mi=pm(pv).inverted()
 for v in ob.data.vertices:v.co=mi@v.co
 return ob
def loft(n,rings,m,c,cap=True):
 k=len(rings[0]);vs=[q for r in rings for q in r];fs=[(i*k+j,i*k+(j+1)%k,(i+1)*k+(j+1)%k,(i+1)*k+j) for i in range(len(rings)-1) for j in range(k)]
 if cap:fs+=[tuple(range(k)),tuple(range((len(rings)-1)*k,len(rings)*k))[::-1]]
 return mesh(n,vs,fs,m,c)
def circ(o,ax,r,seg=12):
 ax=Vector(ax).normalized();u=(ax.cross(Vector((0,0,1))) if abs(ax.z)<.9 else Vector((1,0,0))).normalized();w=ax.cross(u);return [tuple(Vector(o)+u*r*math.cos(2*math.pi*i/seg)+w*r*math.sin(2*math.pi*i/seg)) for i in range(seg)]
def cyl(n,p,q,r0,m,c,r1=None,seg=12):
 p,q=Vector(p),Vector(q);return loft(n,[circ(p,q-p,r0,seg),circ(q,q-p,r0 if r1 is None else r1,seg)],m,c)
def slab(n,quad,nv,h,m,c):
 d=Vector(nv).normalized()*h;return mesh(n,[*quad,*[tuple(Vector(q)+d) for q in quad]],[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],m,c)
def revolve(n,prof,seg,tf,m,c):
 l=len(prof);vs=[tf(x,r,2*math.pi*k/seg) for k in range(seg) for x,r in prof];fs=[(k*l+i,k*l+(i+1)%l,(k+1)%seg*l+(i+1)%l,(k+1)%seg*l+i) for k in range(seg) for i in range(l)]
 return mesh(n,vs,fs,m,c)
def bevel(ob,off,seg=1):
 bm=bmesh.new();bm.from_mesh(ob.data);bmesh.ops.bevel(bm,geom=list(bm.edges),offset=off,offset_type='OFFSET',segments=seg,affect='EDGES');bm.to_mesh(ob.data);bm.free()
_gb=[None]
def ground(x,y,z0=1000,dflt=330):
 # Highest existing architecture under (x,y); BVH of the unparented static meshes built on first use.
 if _gb[0] is None:
  vs=[];fs=[]
  for ob in ARCH:
   if ob.parent:continue
   o=len(vs);vs+=[tuple(v.co) for v in ob.data.vertices];fs+=[tuple(o+i for i in f.vertices) for f in ob.data.polygons]
  _gb[0]=BVHTree.FromPolygons(vs,fs)
 h=_gb[0].ray_cast(Vector((x,y,z0)),Vector((0,0,-1)),z0+300);return h[0].z if h[0] else dflt
# Shared breath cycle: Breath_Control custom props drive every moving pivot through drivers; baked Breath_Cycle actions mirror them for glTF.
BC=bpy.data.objects.new('Breath_Control',None);BR.objects.link(BC);BC.empty_display_type='ARROWS';BC.empty_display_size=400;BC.location=(-10500,0,5300)
for k,v in dict(breath_phase=0.0,cycle_seconds=180.0,timeline_frames=240.0,vane_amplitude_deg=35.0,gill_amplitude_deg=35.0,turbine_max_rpm=1.5,ripple_lag=0.012,sun_available=1.0).items():BC[k]=v
BC['info']='Inhale = breath_phase 0..0.5 (heaters ramp, vanes open, compressor spools up), exhale 0.5..1 (nozzle fires with racing teal pulses, gills flare, turbine coasts). sun_available 0..1 crossfades night heat-exchanger rings (0) with day heliostat mirrors, beams and hot spots (1). Timeline: 240 frames @24fps = one cycle (frame 1 = phase 0). cycle_seconds is the real-time period.'
BD=dict(va=35.,ga=35.,lag=.012,mr=1.5,cs=180.);BV={'sun':'sun_available','p':'breath_phase','va':'vane_amplitude_deg','ga':'gill_amplitude_deg','lag':'ripple_lag','mr':'turbine_max_rpm','cs':'cycle_seconds'}
def f_vane(p,i):return math.radians(BD['va'])*(.5-.5*math.cos(2*math.pi*(p-BD['lag']*i)))
def f_gill(p,k):return math.radians(BD['ga'])*math.sin(math.pi*min(1,max(0,(p-.5-BD['lag']*k)/.38)))**2
def f_revs():return max(1,round(BD['mr']/1.5*BD['cs']/60))
def f_turb(p,sg):return sg*2*math.pi*f_revs()*(p+.5*(1-math.cos(2*math.pi*p))/(2*math.pi))
# Driver expression templates (same math as the f_* mirrors above).
E_VANE='radians(va)*(0.5-0.5*cos(2*pi*(p-lag*%d)))';E_GILL='radians(ga)*pow(sin(pi*clamp((p-0.5-lag*%s)/0.38,0,1)),2)'
E_TURB='%d*2*pi*max(1,round(mr/1.5*cs/60))*(p+0.5*(1-cos(2*pi*p))/(2*pi))'
def mdrv(mn,sock,expr,names=()):
 fc=M[mn].node_tree.nodes['Principled BSDF'].inputs[sock].driver_add('default_value');d=fc.driver;d.type='SCRIPTED';d.expression=expr
 for nm in names:v=d.variables.new();v.name=nm;v.type='SINGLE_PROP';v.targets[0].id=BC;v.targets[0].data_path='["%s"]'%BV[nm]
 return fc
E_GUIDE='%d*2*pi*p'
def f_guide(p,sg):return sg*2*math.pi*p
def drv(ob,path,idx,expr,names=(),extra=None):
 fc=ob.driver_add(path,idx);d=fc.driver;d.type='SCRIPTED';d.expression=expr
 for nm in names:v=d.variables.new();v.name=nm;v.type='SINGLE_PROP';v.targets[0].id=BC;v.targets[0].data_path='["%s"]'%BV[nm]
 for nm,(t,dp) in (extra or {}).items():v=d.variables.new();v.name=nm;v.type='SINGLE_PROP';v.targets[0].id=t;v.targets[0].data_path=dp
 return fc
bpy.context.preferences.edit.keyframe_new_interpolation_type='LINEAR';BAKED=[]
def bake(ob,chans,step=6,offset=False):
 # One `Breath_Cycle_<pivot>` action per moving pivot, sampled every `step` frames over one 240-frame cycle (frames 1..241), cyclic.
 for path,idx,fn in chans:
  for f in range(1,242,step):
   getattr(ob,path)[idx]=fn((f-1)/240);ob.keyframe_insert(path,index=idx,frame=f)
  getattr(ob,path)[idx]=fn(0)
 act=ob.animation_data.action;act.name='Breath_Cycle_'+ob.name
 for fc in act.layers[0].strips[0].channelbag(act.slots[0]).fcurves:
  md=fc.modifiers.new('CYCLES')
  if offset:md.mode_after=md.mode_before='REPEAT_OFFSET'
 BAKED.append(ob)
# A broken crescent with broad irregular armor tongues and deep radial exhaust clefts.
for deg in range(0,360,5):
 dam=310<=deg<=335
 t0=math.radians(deg+.05);t1=math.radians(deg+4.95);mid=(t0+t1)/2;c=SECTORS[sector(mid)];idx=f'{deg:03d}'
 terrace=420+75*math.sin(mid*3)+45*math.sin(mid*7)
 # The narrow retaining collar follows the basin; outer bodies are unequal.
 wedge('Foundation_'+idx,t0,t1,.01,.24,-800,terrace-80,'Graphite_Structural_Alloy',c)
 wedge('Inner_lip_armor_'+idx,t0+.001,t1-.001,0,.12,terrace-10,terrace+35,'Ceramic_Ivory',c,3)
 wedge('Inner_teal_channel_'+idx,t0+.001,t1-.001,.113,.118,terrace+36,terrace+43,'Teal_Inlay',c,3)
 # Tall stepped cliff-like retaining wall below the lip: three set-back tiers, teal tier lights, and sluice mouths where the preview falls pour.
 for k,(r0_,z1_) in enumerate([(-.016,.34),(-.010,.62),(-.004,.90)]):
  wedge(f'Lip_wall_step_{idx}_{k}',t0+.0008,t1-.0008,r0_,.012,-300,terrace*z1_,'Graphite_Structural_Alloy' if k!=1 else 'Ceramic_Slate',c,2)
  wedge(f'Lip_wall_light_{idx}_{k}',t0+.002,t1-.002,r0_-.0012,r0_,terrace*z1_-26,terrace*z1_-18,'Teal_Inlay',c,2)
 wedge('Lip_pilaster_'+idx,t0-.0005,t0+.0018,-.008,.03,terrace-25,terrace+85,'Edge_Titanium',c,1)
 # Sealed basin liner: stepped (narrower toward the top) courses from the floor up to the waterline, all the way round.
 for k,(r0_,z0_,z1_) in enumerate([(-.060,-800,-560),(-.046,-560,-380),(-.032,-380,-250),(-.020,-250,-130)]):wedge(f'Basin_liner_{idx}_{k}',t0,t1,r0_,0,z0_,z1_,'Ceramic_Slate' if k%2 else 'Graphite_Structural_Alloy',c,2)
 wedge('Lip_window_band_'+idx,t0+.003,t1-.003,-.0025,0,terrace+4,terrace+14,'Window_Graphite',c,3)
 if deg%10==0:
  # Sluice mouth: dark spill opening with titanium jambs and a protruding spill lip, on every wall tier.
  wedge('Sluice_mouth_'+idx,t0-.007,t0+.007,-.022,-.003,terrace*.55,terrace+14,'Cavity_Dark',c,2)
  wedge('Sluice_spill_lip_'+idx,t0-.009,t0+.009,-.028,-.020,terrace*.55-14,terrace*.55,'Edge_Titanium',c,2)
  for sg in (-1,1):wedge(f'Sluice_jamb_{idx}_{sg}',t0+sg*.0085-.0013,t0+sg*.0085+.0013,-.024,-.001,terrace*.55,terrace+24,'Edge_Titanium',c,1)
 if deg%15==0:
  # Real apertures beneath a lip deck; access galleries are separately modeled on intake.
  for side,(ss,ee) in enumerate([(t0,t0+(t1-t0)*.3),(t0+(t1-t0)*.7,t1)]):wedge(f'Gallery_pier_{idx}_{side}',ss,ee,-.015,0,35,terrace-12,'Edge_Titanium',c,2)
  wedge('Gallery_walkway_'+idx,t0,t1,-.025,.03,15,35,'Ceramic_Slate',c,4)
 # Long low linear rim buildings that follow the rim: three stepped decks falling toward the basin, tangential chevron ridge roofs, faceted block ends, teal strip lights; gaps only at the exhaust clefts.
 rmax=.70+.29*(.5+.5*math.sin(mid*5+.8))
 if dam:
  # Massive dam section closing the old crescent gap: crest road, spillway gate bays with piers, downstream buttresses and teal lines; it ties into the rim buildings at both ends.
  ta=math.radians(deg);tb=math.radians(deg+5);zd=terrace+190
  wedge(f'Dam_body_{idx}',ta,tb,-.03,.40,-800,zd-40,'Ceramic_Slate',c,3);wedge(f'Dam_crest_{idx}',ta,tb,-.03,.34,zd-40,zd,'Ceramic_Ivory',c,3)
  wedge(f'Dam_crest_teal_{idx}',ta+.001,tb-.001,-.026,-.021,zd+1,zd+8,'Teal_Inlay',c,3);wedge(f'Dam_crest_teal_out_{idx}',ta+.001,tb-.001,.32,.326,zd+1,zd+8,'Teal_Inlay',c,3)
  for g in range(2):
   g0=ta+(tb-ta)*(g*.5+.08);g1=ta+(tb-ta)*(g*.5+.42)
   wedge(f'Dam_gate_dark_{idx}_{g}',g0,g1,-.038,-.030,terrace*.30,zd-70,'Cavity_Dark',c,2);wedge(f'Dam_gate_leaf_{idx}_{g}',g0+.001,g1-.001,-.045,-.038,terrace*.30+30,zd-100,'Edge_Titanium',c,2)
   wedge(f'Dam_gate_teal_{idx}_{g}',g0,g1,-.047,-.041,zd-102,zd-94,'Teal_Inlay',c,2);wedge(f'Dam_pier_{idx}_{g}',ta+(tb-ta)*g*.5-.0016,ta+(tb-ta)*g*.5+.0016,-.052,-.01,-300,zd+70,'Graphite_Structural_Alloy',c,1)
   bt=ta+(tb-ta)*(g*.5+.25);beam(f'Dam_buttress_{idx}_{g}',pt(bt,.36,zd-60),pt(bt,.64,terrace*.2),260,320,'Graphite_Structural_Alloy',c)
 elif deg%15!=10:
  ta=math.radians(deg);tb=math.radians(deg+5);bp=deg%15;arch=150<=deg<210
  tiers=[(.125,.31,terrace+110,-800),(.325,.52,terrace+210+25*math.sin(mid*9),-800),(.54,.62+.2*(.5+.5*math.sin(mid*5+.8)),terrace+95,-800)]
  for bi,(ra,rb,zt,zb) in enumerate(tiers):
   if arch and bi>0:continue
   wedge(f'Primary_dark_wing_{("in_","","out_")[bi]}{idx}',ta,tb,ra,rb,zb,zt-45,'Graphite_Structural_Alloy',c,2)
   wedge(f'Rim_roof_{bi}_{idx}',ta,tb,ra,rb,zt-45,zt,'Ceramic_Ivory' if (deg//5+bi)%2 else 'Ceramic_Pearl',c,2)
   wedge(f'Rim_window_band_{bi}_{idx}',ta,tb,ra-.003,ra,zt-85,zt-60,'Window_Graphite',c,2)
   wedge(f'Rim_teal_in_{bi}_{idx}',ta+.0006,tb-.0006,ra+.002,ra+.006,zt+1,zt+7,'Teal_Inlay',c,3)
   if bi==1:wedge(f'Rim_teal_out_{bi}_{idx}',ta+.0006,tb-.0006,rb-.006,rb-.002,zt+1,zt+7,'Teal_Inlay',c,3)
   if bi<2:
    rg=[]
    for k in range(5):
     tt=ta+(tb-ta)*k/4;e=(bp==0 and k==0) or (bp==5 and k==4);ins=.03 if e else .012;hh=0 if e else 18+30*(k%2)
     rg.append([pt(tt,ra+ins,zt+2),pt(tt,rb-ins,zt+2),pt(tt,(ra+rb)/2,zt+2+hh)])
    loft(f'Rim_ridge_{bi}_{idx}',rg,'Ceramic_Slate',c)
 else:
  # Open dark exhaust channels between the building blocks.
  wing('Exhaust_channel_floor_'+idx,t0+.004,t1-.004,.17,rmax-.08,terrace*.5+30,terrace*.5-40,75,'Graphite_Structural_Alloy',c,-.015)
  for sg_ in (0,1):wedge(f'Exhaust_trough_wall_{idx}_{sg_}',[t0+.002,t1-.007][sg_],[t0+.007,t1-.002][sg_],.17,rmax-.08,terrace*.5-20,terrace*.5+90,'Graphite_Structural_Alloy',c,2)
  for k in range(5):
   rr=.27+k*.10
   radialbeam(f'Exhaust_lamella_{idx}_{k}',mid,rr,rr+.065,terrace*.5+40,terrace*.5+30,250,35,'Edge_Titanium',c)
# Chamfer the large armor bodies (single-segment bmesh bevel; hard 45 degree facets catch light).
for ob in list(ARCH):
 if ob.name.startswith(('Rim_roof_','Inner_lip_armor_')):bevel(ob,5)
 elif ob.name.startswith('Primary_dark_wing_'):bevel(ob,8)
# The single tilted hollow intake. The arch is a lofted half-ellipse, not a closed solid mound.
H=a.arch_height
# y-z ellipse goes from low shoulder through high crest to opposite shoulder.
def ap(t,depth=0,skin=0):
 s=math.sin(t);return (-11400+2300*s+depth,4350*math.cos(t),260+(H-325)*s+skin)
def archstrip(n,t0,t1,d0,d1,s0,s1,m,c,steps=4):
 vs=[]
 for skin in [s0,s1]:
  for d in [d0,d1]:
   for j in range(steps+1):vs.append(ap(t0+(t1-t0)*j/steps,d,skin))
 k=steps+1;fs=[]
 for j in range(steps):fs +=[(j,j+1,k+j+1,k+j),(2*k+j,3*k+j,3*k+j+1,2*k+j+1),(j,2*k+j,2*k+j+1,j+1),(k+j,k+j+1,3*k+j+1,3*k+j)]
 fs +=[(0,k,3*k,2*k),(k-1,2*k-1,4*k-1,3*k-1)]
 return mesh(n,vs,fs,m,c)
C=SECTORS[6]
# Thick layered crescent hood: deep dark shell, three stacked armor bands of rising height (teal seams between), hinged gill bands, overhanging crest lip.
GILLS=[];GJ=set(range(6,28,3));LANEH=[45,95,150];SH=-330
def gill(j,lane,gi):
 # Outer-shell band segment hinged along its front (+X) tangent edge on Gill_Pivot_XX; free (rear) edge flares outward on exhale.
 t0=math.pi*j/36+.0035;t1=min(math.pi,math.pi*j/36+math.pi/12)-.0035;d0=-1490+lane*900;d1=d0+850;kc=(j-6)//3+(.5 if lane==2 else 0)
 H0,H1=Vector(ap(t0,d1,40)),Vector(ap(t1,d1,40));pv=bpy.data.objects.new('Gill_Pivot_%02d'%gi,None);V.objects.link(pv);pv.location=(H0+H1)/2
 tm=(t0+t1)/2;n=Vector((0,(H-325)*math.cos(tm),4350*math.sin(tm))).normalized();ex=H1-H0;ex=(ex-n*ex.dot(n)).normalized()
 if ex.cross(Vector((-1,0,0))).dot(n)<0:ex=-ex
 pv.rotation_mode='AXIS_ANGLE';pv.rotation_axis_angle=(0,*ex);pv['hinge_axis_blender']=[round(q,4) for q in ex];pv['hinge_note']='rotation_axis_angle[0] = flare angle (rad); axis tangent to arch at the plate front edge';pv['suggested_limit_degrees']=35
 adopt(archstrip('Gill_plate_%02d'%gi,t0,t1,d0,d1,3,LANEH[lane],'Ceramic_Pearl',V,9),pv)
 # Recessed dark cavity + teal glow slit under the plate (hidden while closed).
 archstrip('Gill_cavity_%02d'%gi,t0+.012,t1-.012,d0+70,d1-70,-6,8,'Gill_Cavity_Glow',C,3);archstrip('Gill_slit_%02d'%gi,t0+.02,t1-.02,d0+110,d0+140,7,10,'Teal_Inlay',C,3)
 bake(pv,[('rotation_axis_angle',0,lambda p:f_gill(p,kc))]);drv(pv,'rotation_axis_angle',0,E_GILL%repr(kc),('p','ga','lag'));MOVABLE.append(pv);GILLS.append(pv)
gi=0
for j in range(36):
 t0=math.pi*j/36;t1=math.pi*(j+1)/36
 archstrip(f'Intake_arch_dark_shell_{j:02d}',t0,t1,-1500,1200,SH,0,'Graphite_Structural_Alloy',C,3)
 if j%3==0:
  for lane in range(3):
   if j in GJ and lane>0:gill(j,lane,gi);gi+=1;continue
   d0=-1490+lane*900;d1=d0+850;delta=.0035
   archstrip(f'Intake_oblique_armor_{j:02d}_{lane}',t0+delta,min(math.pi,t0+math.pi/12)-delta,d0,d1,3,LANEH[lane],'Ceramic_Pearl' if lane!=0 else 'Ceramic_Ivory',C,9)
  if j>0:archstrip(f'Intake_panel_seam_{j:02d}',t0-.0033,t0+.0033,-1490,1160,0,38,'Cavity_Dark',C,2)
 archstrip(f'Intake_underside_rib_{j:02d}',t0,t0+.014,-1450,1150,SH-60,SH,'Edge_Titanium',C,1)
for k,d in enumerate([-1540,-790,600,1240]):
 archstrip(f'Intake_continuous_structural_ring_{k}',0,math.pi,d-55,d+55,SH-70,20,'Edge_Titanium',C,64)
 archstrip(f'Intake_teal_inlay_{k}',.02,math.pi-.02,d-16,d+16,22,35,'Teal_Inlay',C,64)
for k,dg in enumerate([-615,285]):archstrip(f'Intake_lane_seam_{k}',.02,math.pi-.02,dg-18,dg+18,0,34,'Teal_Inlay',C,36)
# Rim light strips on the front and rear shell faces.
archstrip('Intake_rim_light_front',.02,math.pi-.02,1196,1208,-210,-170,'Teal_Inlay',C,48);archstrip('Intake_rim_light_rear',.02,math.pi-.02,-1508,-1496,-210,-170,'Teal_Inlay',C,48)
# Overhanging crest lip: deep crown projecting forward over the opening, with a teal under-light.
for j in range(10,27):
 t0=math.pi*j/36+.004;t1=math.pi*(j+1)/36-.004
 archstrip(f'Crown_floating_armor_{j:02d}',t0,t1,1250,1900,-140,100,'Ceramic_Ivory',C,2)
archstrip('Crown_lip_light',math.pi*10/36+.004,math.pi*27/36-.004,1890,1906,-120,-95,'Teal_Inlay',C,48)
# Bladeless air-amplifier ring: an annular nozzle slit (12 emissive segments, each with its own phase-lagged emission driver) round the hood's inner front lip, plus two concentric guide-vane rings that counter-rotate slowly.
for k in range(12):
 archstrip(f'Nozzle_segment_{k:02d}',math.pi*k/12+.002,math.pi*(k+1)/12-.002,1050,1196,SH-12,SH+4,'Nozzle_Glow_%02d'%k,C,6)
 mdrv('Nozzle_Glow_%02d'%k,'Emission Strength','0.5+16*pow(max(0,-sin(2*pi*p)),2)*(0.2+0.8*pow(max(0,cos(4*pi*(%d/12-2*(p-0.5)))),3))'%k,('p',))
GRING=[];nrm_=Vector((4475,0,-2300)).normalized()
for nm,dd,rs,nb,sg in (('A',440,.90,24,1),('B',660,.86,20,-1)):
 cen=Vector((-11400+dd,0,260));pv=bpy.data.objects.new('Guide_Ring_'+nm,None);V.objects.link(pv);pv.location=cen;pv.rotation_mode='AXIS_ANGLE';pv.rotation_axis_angle=(0,*nrm_);pv['purpose']='Guide-vane ring concentric with the nozzle; rotation_axis_angle[0] radians about the arch-plane normal (stored), slow, 1 revolution per breath cycle, B counter-rotates'
 rp=lambda t,rr,dx=0,dd=dd,cen=cen:tuple(cen+(Vector(ap(t,dd,0))-cen)*rr+Vector((dx,0,0)))
 adopt(revolve(f'Guide_Ring_{nm}_band',[(-35,rs-.012),(35,rs-.012),(35,rs+.012),(-35,rs+.012)],72,lambda x,r,an,rp=rp:rp(an,r,x),'Edge_Titanium',V),pv)
 for i in range(nb):an=2*math.pi*i/nb;adopt(beam(f'Guide_Ring_{nm}_vane_{i}',rp(an,rs-.06,-110*sg),rp(an,rs,110*sg),44,240,'Ceramic_Slate',V),pv)
 bake(pv,[('rotation_axis_angle',0,lambda p,sg=sg:f_guide(p,sg))],6,True);drv(pv,'rotation_axis_angle',0,E_GUIDE%sg,('p',));MOVABLE.append(pv);GRING.append(pv)
# Bore lining: stacked parallel curved fins following the arch curvature (teal-edged), under a diagonal truss between the structural rings.
for k,d in enumerate([-1300,-1050,-540,-290,-40,210,860,1100]):
 archstrip(f'Bore_fin_{k}',.12,math.pi-.12,d-38,d+38,SH-260,SH,'Ceramic_Slate' if k%2 else 'Graphite_Structural_Alloy',C,28)
 archstrip(f'Bore_fin_edge_{k}',.12,math.pi-.12,d-20,d+20,SH-268,SH-250,'Teal_Inlay',C,28)
for j in range(36):
 t0=math.pi*j/36;t1=math.pi*(j+1)/36
 for b,(da,db) in enumerate([(-1485,-845),(660,1180)]):
  p,q=(da,db) if (j+b)%2 else (db,da);beam(f'Bore_truss_{j:02d}_{b}',ap(t0+.003,p,SH-6),ap(t1-.003,q,SH-6),26,30,'Edge_Titanium',C)
# Interior gantries hung from the shell on both bore walls, with rails and hangers.
for sg in (-1,1):
 for gy,gz in ((3300,2150),(3900,1250)):
  y=sg*gy;s_=math.sqrt(1-(gy/4350)**2);xc=-11400+2300*s_-150;zu=260+(H-325)*s_+SH
  cube(f'Gantry_deck_{sg}_{gy}',(xc,y,gz),(2400,80,14),'Ceramic_Slate',C)
  for r in (-1,1):cube(f'Gantry_rail_{sg}_{gy}_{r}',(xc,y+r*38,gz+14),(2400,6,10),'Edge_Titanium',C)
  for hx in (-1000,-340,340,1000):beam(f'Gantry_hanger_{sg}_{gy}_{hx}',(xc+hx,y,gz),(xc+hx,y,zu+10),14,14,'Edge_Titanium',C)
# Folded L/chevron plinth legs: each arch foot drops steeply down and outward, then folds to run along the rim as a long low plinth that merges into the rim buildings; cross seams, inset panels, teal edge lines and a chevron ridge break it up.
def tslab(n,p,q,w0,w1,h0,h1,m,c):
 p,q=Vector(p),Vector(q);d=(q-p).normalized();sd=d.cross(Vector((0,0,1))).normalized();up=sd.cross(d).normalized()
 return loft(n,[[tuple(e+sd*a*w/2+up*b*h/2) for a,b in ((-1,-1),(1,-1),(1,1),(-1,1))] for e,w,h in ((p,w0,h0),(q,w1,h1))],m,c)
for sign in (-1,1):
 pts=[(-11700,sign*3900,1150),(-12500,sign*5600,520),pt(math.radians(120 if sign>0 else 240),.72,330)];ws=[2900,2700,1500];hs=[950,620,330]
 for k in range(2):
  p,q=Vector(pts[k]),Vector(pts[k+1]);d=(q-p).normalized();sd=d.cross(Vector((0,0,1))).normalized();up=sd.cross(d).normalized();L=(q-p).length
  lw=lambda f:ws[k]+(ws[k+1]-ws[k])*f;lh=lambda f:hs[k]+(hs[k+1]-hs[k])*f;at=lambda f,o=0:p+(q-p)*f+up*(lh(f)/2+o)
  tslab(f'Arch_shoulder_buttress_{sign}_{k}',p,q,ws[k],ws[k+1],hs[k],hs[k+1],'Graphite_Structural_Alloy',C)
  tslab(f'Arch_shoulder_armor_{sign}_{k}',at(0,30),at(1,30),ws[k]*.92,ws[k+1]*.92,70,70,'Ceramic_Ivory',C)
  for sd_ in (-1,1):tslab(f'Arch_leg_light_{sign}_{k}_{sd_}',at(0,68)+sd*sd_*ws[k]*.4,at(1,68)+sd*sd_*ws[k+1]*.4,36,36,18,18,'Teal_Inlay',C)
  ns=max(2,int(L/340))
  for sm in range(1,ns):f=sm/ns;tslab(f'Arch_leg_seam_{sign}_{k}_{sm}',at(f,66),at(f,66)+d*34,lw(f)*.9,lw(f)*.9,16,16,'Cavity_Dark',C)
  for pb in range(3):f=.15+.3*pb;tslab(f'Arch_leg_panel_{sign}_{k}_{pb}',at(f,70)+sd*(pb-1)*lw(f)*.26,at(f+.1,70)+sd*(pb-1)*lw(f+.1)*.26,lw(f)*.22,lw(f+.1)*.22,14,14,'Cavity_Dark',C)
  if k==1:
   rg=[[tuple(at(f,86)+sd*a*lw(f)*.16+up*hh) for a,hh in ((-1,0),(1,0),(0,20+34*(i%2)))] for i,f in enumerate([j/14 for j in range(15)])]
   loft(f'Arch_leg_ridge_{sign}',rg,'Ceramic_Slate',C)
# Rear throat scoop: the hood's back curves down (quarter-ellipse per cross-section) into a closed rear wall that merges with the legs and rim; it carries a circular throat opening for the turbine.
SCX=2400;ZG=-90;RH=1400
def scp(t,p,k):
 s=math.sin(t);z0=260+(H-325)*s;D=max(z0-ZG,40);ex=SCX*s**2.2;nx,nz=-D*math.sin(p),ex*math.cos(p);nn=math.hypot(nx,nz) or 1
 return (-12900+2300*s-ex*math.sin(p)+nx/nn*k,4350*math.cos(t),ZG+D*math.cos(p)+nz/nn*k)
D0=260+(H-325)-ZG;pc=math.acos((1900-ZG)/D0);Pc=Vector(scp(math.pi/2,pc,0));EX=-Vector((-D0*math.sin(pc),0,SCX*math.cos(pc))).normalized();EY=Vector((0,1,0));EZ=EX.cross(EY);KS=.7;CT=Pc+EX*850
def sgrid(n,m,t0,t1,p0,p1,k0,k1,nt,npf):
 vs=[[[scp(t0+(t1-t0)*i/nt,p0+(p1-p0)*j/npf,k) for j in range(npf+1)] for i in range(nt+1)] for k in (k0,k1)];fl=[];ix=lambda l,i,j:(l*(nt+1)+i)*(npf+1)+j
 for i in range(nt):
  for j in range(npf):
   cc=sum((Vector(vs[l][a_][b_]) for l in (0,1) for a_ in (i,i+1) for b_ in (j,j+1)),Vector())/8;r_=cc-CT
   if (r_-EX*r_.dot(EX)).length<RH:continue
   fl+=[(ix(1,i,j),ix(1,i+1,j),ix(1,i+1,j+1),ix(1,i,j+1)),(ix(0,i,j),ix(0,i,j+1),ix(0,i+1,j+1),ix(0,i+1,j))]
   if i==0:fl.append((ix(0,i,j),ix(1,i,j),ix(1,i,j+1),ix(0,i,j+1)))
   if i==nt-1:fl.append((ix(0,i+1,j),ix(0,i+1,j+1),ix(1,i+1,j+1),ix(1,i+1,j)))
   if j==0:fl.append((ix(0,i,j),ix(0,i+1,j),ix(1,i+1,j),ix(1,i,j)))
   if j==npf-1:fl.append((ix(0,i,j+1),ix(1,i,j+1),ix(1,i+1,j+1),ix(0,i+1,j+1)))
 return mesh(n,[q for l in vs for r2 in l for q in r2],fl,m,C)
sgrid('Scoop_shell','Graphite_Structural_Alloy',.08,math.pi-.08,0,math.pi/2,-220,0,60,18)
for b in range(4):
 for ci in range(9):
  w_=(math.pi-.2)/9;ta_=.1+ci*w_;sgrid(f'Scoop_plate_{b}_{ci}','Ceramic_Ivory' if b%2==0 else 'Ceramic_Pearl',ta_,ta_+w_-.012,b*.39+.03,b*.39+.34,0,45,3,3)
 if b<3:sgrid(f'Scoop_seam_{b}','Teal_Inlay',.1,math.pi-.1,b*.39+.34,b*.39+.42,0,12,48,1)
# Adjustable fins sit at the outer rear of bore; the central mouth is visually and physically open.
PIV=[]
for i,y in enumerate([-2850,-1425,0,1425,2850]):
 h=(H-900)*math.sqrt(max(0,1-(y/4150)**2));pivot=bpy.data.objects.new(f'VanePivot_{i:02d}',None);V.objects.link(pivot);pivot.location=(-11250,y,340);pivot['hinge_axis_blender']='Y';pivot['suggested_limit_degrees']=35;pivot['purpose']='Driven by Breath_Control (drivers + baked Breath_Cycle action); rotation_euler.y positive = open (fin tips swing forward/down)'
 R=Vector((-11250,y,390));T=Vector((-9400,y,h));ax=(T-R).normalized();up=Vector((ax.z,0,-ax.x)).normalized();Y=Vector((0,1,0))
 sec=lambda u:[tuple(R+(T-R)*u+Y*a*(200-95*u)+up*b*(430-200*u)) for a,b in [(-.15,.5),(.15,.5),(.5,.28),(.5,-.1),(.2,-.5),(-.2,-.5),(-.5,-.1),(-.5,.28)]]
 lead=lambda u:[tuple(R+(T-R)*u+Y*a*(200-95*u)+up*(.5*(430-200*u)+o)) for a,o in [(-.13,-3),(.13,-3),(.13,12),(-.13,12)]]
 adopt(loft(f'Movable_intake_vane_{i:02d}',[sec(k/7) for k in range(8)],'Ceramic_Slate',V),pivot);adopt(loft(f'Vane_leading_teal_{i:02d}',[lead(k/4) for k in range(5)],'Teal_Inlay',V),pivot)
 adopt(cyl(f'Vane_hinge_barrel_{i:02d}',(-11250,y-170,340),(-11250,y+170,340),125,'Edge_Titanium',V),pivot)
 # Static clevis cheeks and plinth.
 gz=ground(-11250,y);top=max(560,gz+110)
 for sg in (-1,1):cube(f'Vane_clevis_{i}_{sg}',(-11250,y+sg*215,(gz-40+top)/2),(320,50,top-gz+40),'Edge_Titanium',C)
 cube(f'Vane_plinth_{i}',(-11250,y,gz+8),(460,540,70),'Graphite_Structural_Alloy',C)
 # Hydraulic ram in the plane y+170: static cylinder pivot at the floor aims at the attach pin on the fin; rod slides out.
 fa=.42;cp_=R+(T-R)*fa;Ax,Az=cp_.x+11250,cp_.z-340;gB=ground(-10750,y)+30;Bx,Bz=500.,gB-340
 L0=math.hypot(Ax-Bx,Az-Bz);rel=lambda th:((Ax*math.cos(th)+Az*math.sin(th))-Bx,(-Ax*math.sin(th)+Az*math.cos(th))-Bz)
 psi=lambda th:math.atan2(-rel(th)[1],rel(th)[0]);ext=lambda th:math.hypot(*rel(th))-L0
 Ls=[L0+ext(math.radians(BD['va'])*q/10) for q in range(11)];Lr=min(Ls)-25;CL=max(Ls)-Lr+60
 cp0=bpy.data.objects.new(f'Vane_RamCyl_{i:02d}',None);V.objects.link(cp0);cp0.location=(-10750,y+170,gB);cp0.rotation_euler=(0,psi(0),0)
 rp0=bpy.data.objects.new(f'Vane_RamRod_{i:02d}',None);V.objects.link(rp0);rp0.parent=cp0
 for ob_,pv_ in ((cyl(f'Vane_ram_cylinder_{i:02d}',(0,0,0),(CL,0,0),62,'Edge_Titanium',V),cp0),(cyl(f'Vane_ram_collar_{i:02d}',(0,0,0),(130,0,0),82,'Graphite_Structural_Alloy',V),cp0),(cyl(f'Vane_ram_base_eye_{i:02d}',(0,-75,0),(0,75,0),52,'Graphite_Structural_Alloy',V),cp0),
  (cyl(f'Vane_ram_rod_{i:02d}',(L0-Lr,0,0),(L0,0,0),30,'Ceramic_Pearl',V),rp0),(cyl(f'Vane_ram_head_eye_{i:02d}',(L0,-70,0),(L0,70,0),44,'Graphite_Structural_Alloy',V),rp0)):ob_.parent=pv_;ob_.matrix_parent_inverse=Matrix.Identity(4)
 cyl(f'Vane_ram_pin_{i:02d}',(cp_.x,y+90,cp_.z),(cp_.x,y+190,cp_.z),40,'Edge_Titanium',V);adopt(bpy.data.objects[f'Vane_ram_pin_{i:02d}'],pivot)
 cube(f'Vane_ram_footing_{i}',(-10750,y+170,gB-14),(300,230,56),'Graphite_Structural_Alloy',C)
 xs_='((%.3f)*cos(th)+(%.3f)*sin(th)-(%.3f))'%(Ax,Az,Bx);zs_='(-(%.3f)*sin(th)+(%.3f)*cos(th)-(%.3f))'%(Ax,Az,Bz);ex_={'th':(pivot,'rotation_euler[1]')}
 bake(pivot,[('rotation_euler',1,lambda p:f_vane(p,i))]);bake(cp0,[('rotation_euler',1,lambda p:psi(f_vane(p,i)))]);bake(rp0,[('location',0,lambda p:ext(f_vane(p,i)))])
 drv(pivot,'rotation_euler',1,E_VANE%i,('p','va','lag'));drv(cp0,'rotation_euler',1,f'atan2(-{zs_},{xs_})',(),ex_);drv(rp0,'location',0,f'sqrt({xs_}*{xs_}+{zs_}*{zs_})-{L0:.3f}',(),ex_)
 MOVABLE.extend([pivot,cp0,rp0]);PIV.append(pivot)
# Bore turbine seated recessed in the scoop's circular throat opening: duct collar, support spider, protective grille, nacelle and two counter-rotating rotors.
TW=lambda x,r,an:tuple(CT+EX*x+EY*r*KS*math.cos(an)+EZ*r*KS*math.sin(an))
revolve('Turbine_Collar_duct',[(-600,1840),(300,1840),(430,1960),(430,2120),(-600,2120)],56,TW,'Ceramic_Slate',C)
revolve('Turbine_Collar_flange_rear',[(-640,2120),(-640,2330),(-300,2330),(-300,2120)],56,TW,'Edge_Titanium',C);revolve('Turbine_Collar_flange_front',[(340,2120),(340,2260),(450,2260),(450,2120)],56,TW,'Edge_Titanium',C)
revolve('Turbine_Collar_teal',[(430,1965),(446,1965),(446,2115),(430,2115)],56,TW,'Teal_Inlay',C)
revolve('Turbine_Throat_ring',[(-940,2000),(-940,2500),(-500,2500),(-500,2000)],56,TW,'Ceramic_Pearl',C);revolve('Turbine_Throat_ring_teal',[(-946,1960),(-930,1960),(-930,2040),(-946,2040)],56,TW,'Teal_Inlay',C)
loft('Turbine_Nacelle',[circ(CT+EX*x,EX,r*KS,24) for x,r in ((-640,250),(-560,330),(-300,340),(240,340),(380,300),(480,200),(560,60))],'Graphite_Structural_Alloy',C)
for k in range(6):beam(f'Turbine_Spider_{k}',TW(-470,330,k*math.pi/3+.3),TW(-470,1850,k*math.pi/3+.3),110,50,'Edge_Titanium',C)
for k,r in enumerate((300,600,900,1200,1500,1800)):revolve(f'Turbine_Grille_ring_{k}',[(390,r-18),(415,r-18),(415,r+18),(390,r+18)],36,TW,'Edge_Titanium',C)
for k in range(24):beam(f'Turbine_Grille_bar_{k}',TW(402,380,k*math.pi/12),TW(402,1850,k*math.pi/12),26,26,'Edge_Titanium',C)
TURB=[]
for nm,xo,N,sg,mm in (('A',-330,20,1,'Ceramic_Pearl'),('B',-60,22,-1,'Ceramic_Slate')):
 pv=bpy.data.objects.new('Turbine_Rotor_'+nm,None);V.objects.link(pv);O=CT+EX*xo;pv.location=O;pv.rotation_mode='AXIS_ANGLE';pv.rotation_axis_angle=(0,*EX)
 pv['spin_axis_blender']=[round(q,4) for q in EX];pv['blades']=N;pv['purpose']='Continuous spin; rotation_axis_angle[0] radians, whole revolutions per breath cycle; B counter-rotates'
 bv=[];bf=[];tv=[];tf=[]
 for kb in range(N):
  an=2*math.pi*kb/N;rad=EY*math.cos(an)+EZ*math.sin(an);tg=-EY*math.sin(an)+EZ*math.cos(an)
  def sec(rho,sc=1.):
   f=(rho-380)/1170;cc=(330-110*f)*sc*KS;th=(70-45*f)*sc*KS;ph=math.radians(38-24*f);cd=(sg*math.sin(ph),math.cos(ph));nm_=(math.cos(ph),-sg*math.sin(ph))
   return [tuple(O+rad*rho*KS+EX*(al*cd[0]+tk*nm_[0])+tg*(al*cd[1]+tk*nm_[1])) for al,tk in ((-.5*cc,0),(-.2*cc,th/2),(.25*cc,.35*th),(.5*cc,0),(.25*cc,-.35*th),(-.2*cc,-th/2))]
  for V_,F_,rs,sc in ((bv,bf,(380,700,1050,1400,1500),1.),(tv,tf,(1490,1565),1.04)):
   o=len(V_);rg=[sec(r_,sc) for r_ in rs];V_+=[q for r_ in rg for q in r_];kk=6
   F_+=[(o+i*kk+j,o+i*kk+(j+1)%kk,o+(i+1)*kk+(j+1)%kk,o+(i+1)*kk+j) for i in range(len(rs)-1) for j in range(kk)]+[tuple(range(o,o+kk)),tuple(range(o+(len(rs)-1)*kk,o+len(rs)*kk))[::-1]]
 adopt(mesh(f'Turbine_Rotor_{nm}_blades',bv,bf,mm,V),pv);adopt(mesh(f'Turbine_Rotor_{nm}_tips',tv,tf,'Teal_Inlay',V),pv)
 adopt(loft(f'Turbine_Rotor_{nm}_hub',[circ(O+EX*x,EX,r*KS,32) for x,r in ((-90,360),(-70,415),(70,415),(90,360))],'Edge_Titanium',V),pv)
 bake(pv,[('rotation_axis_angle',0,lambda p,sg=sg:f_turb(p,sg))],3,True);drv(pv,'rotation_axis_angle',0,E_TURB%sg,('p','mr','cs'));MOVABLE.append(pv);TURB.append(pv)
# Clearance report at full vane amplitude: fin tips vs shell underside, fins vs turbine duct envelope, fin vs rest pose.
def _fin_pts(y,th):
 h=(H-900)*math.sqrt(max(0,1-(y/4150)**2));R_=Vector((-11250,0,390));T_=Vector((-9400,0,h));out=[]
 for q in range(41):
  pp=R_+(T_-R_)*(q/40)-Vector((-11250,0,340));out.append(Vector((-11250+pp.x*math.cos(th)+pp.z*math.sin(th),y,340-pp.x*math.sin(th)+pp.z*math.cos(th))))
 return out
_mc=1e9;_mt=1e9
for y_ in (-2850,-1425,0,1425,2850):
 for th_ in [math.radians(BD['va'])*q/8 for q in range(9)]:
  for pp in _fin_pts(y_,th_):
   _mc=min(_mc,260+(H-325)*math.sqrt(max(0,1-(y_/4350)**2))+SH-pp.z-110)
   r_=pp-CT;ax_=r_.dot(EX)
   _mt=min(_mt,math.hypot(max(ax_-450,-640-ax_,0),max((r_-EX*ax_).length-2330*KS,0)))
print('CLEARANCE vane(at %.0f deg) to shell underside (after 110 m fin half-chord): %.0f m;  vane to turbine duct outer wall (axial window): %.0f m'%(BD['va'],_mc,_mt),flush=True)
# Day heating: solar concentrator arrays (3 heliostats each) on the rim roofs. Panels sit on axis-angle pivots that aim at hot spots on the water at sun_available=1 and stow face-down at 0 (smooth crossfade).
SUNV=Euler((math.radians(25),math.radians(-28),math.radians(-35))).to_matrix()@Vector((0,0,1));HOT=[];HELIO=[]
for ai,deg_ in enumerate([15,50,85,115,235,260,285,350]):
 th=math.radians(deg_+2.5);c=SECTORS[sector(th)];P0=Vector(pt(th,.43,0));tg=Vector((-math.sin(th),math.cos(th),0)).normalized();hot=Vector((9900*.42*math.cos(th+math.pi+.35),5650*.42*math.sin(th+math.pi+.35),-128));HOT.append(hot)
 for k in range(3):
  Pp=P0+tg*(k-1)*480;base=ground(Pp.x,Pp.y);Pp.z=base+190;Td=(hot-Pp).normalized();nn=(SUNV+Td).normalized();ax_=Vector((0,0,1)).cross(nn);ax_=ax_.normalized() if ax_.length>1e-4 else Vector((1,0,0));th1=math.acos(max(-1,min(1,nn.z)))
  pv=bpy.data.objects.new(f'Heliostat_Pivot_{ai}_{k}',None);V.objects.link(pv);pv.location=Pp;pv.rotation_mode='AXIS_ANGLE';pv.rotation_axis_angle=(0,*ax_);rz_=math.atan2(tg.y,tg.x)
  adopt(cube(f'Heliostat_panel_{ai}_{k}',tuple(Pp),(380,260,10),'Mirror_Solar',V,rz_),pv);adopt(cube(f'Heliostat_frame_{ai}_{k}',tuple(Pp-Vector((0,0,8))),(404,284,8),'Edge_Titanium',V,rz_),pv)
  cyl(f'Heliostat_post_{ai}_{k}',(Pp.x,Pp.y,base-30),(Pp.x,Pp.y,Pp.z-14),38,'Edge_Titanium',c,22,8)
  pv['purpose']='Heliostat: rotation_axis_angle[0] = pi (stowed face-down, sun_available 0) .. %.4f rad (tracking the hot spot, sun_available 1); axis in rotation_axis_angle'%th1
  pv.rotation_axis_angle=(math.pi,*ax_);drv(pv,'rotation_axis_angle',0,'pi+(%.5f-pi)*(sun*sun*(3-2*sun))'%th1,('sun',));MOVABLE.append(pv);HELIO.append(pv)
  loft(f'Preview_Only_Beam_{ai}_{k}',[circ(Pp,Td,18,6),circ(hot,Td,60,6)],'Preview_Beam',ENV,False)
for hi,h in enumerate(HOT):cyl(f'Preview_Only_Hotspot_{hi}',(h.x,h.y,-133),(h.x,h.y,-128),340,'Preview_Hotspot',ENV,seg=24)
mdrv('Preview_Beam','Emission Strength','7*sun',('sun',));mdrv('Preview_Beam','Alpha','0.045*sun',('sun',));mdrv('Preview_Hotspot','Emission Strength','10*sun',('sun',));mdrv('Preview_Hotspot','Alpha','0.75*sun',('sun',))
# Sealed basin: floor about 800 m below the rim datum, night heat-exchanger rings (emission = (1-sun) x breath, inhale hotter), and the water surface in its own export collection.
FL=-800
def revolve2(n,prof,a0,a1,steps,tf,m,c):
 l=len(prof);vs=[tf(x,r,a0+(a1-a0)*k/steps) for k in range(steps+1) for x,r in prof];fs=[(k*l+i,k*l+(i+1)%l,(k+1)*l+(i+1)%l,(k+1)*l+i) for k in range(steps) for i in range(l)]+[tuple(range(l))[::-1],tuple(range(steps*l,steps*l+l))]
 return mesh(n,vs,fs,m,c)
for s_ in range(12):
 a0=2*math.pi*s_/12;a1=2*math.pi*(s_+1)/12;c=SECTORS[sector((a0+a1)/2)]
 prism(f'Basin_floor_{s_:02d}',[(0,0)]+[(9950*math.cos(a0+(a1-a0)*k/6),5700*math.sin(a0+(a1-a0)*k/6)) for k in range(7)],FL-70,FL,'Graphite_Structural_Alloy',c)
 for ri,rs_ in enumerate((.16,.30,.44,.58,.72,.86)):revolve2(f'Heat_ring_{ri}_{s_:02d}',[(FL,rs_-.022),(FL,rs_+.022),(FL+26,rs_+.022),(FL+26,rs_-.022)],a0,a1,5,lambda zz,rr,an:(9950*rr*math.cos(an),5700*rr*math.sin(an),zz),'Heat_Exchanger_Glow',c)
 am=(a0+a1)/2;beam(f'Heat_spoke_{s_:02d}',(9950*.1*math.cos(am),5700*.1*math.sin(am),FL+14),(9950*.9*math.cos(am),5700*.9*math.sin(am),FL+14),90,28,'Heat_Exchanger_Glow',c)
mdrv('Heat_Exchanger_Glow','Emission Strength','2.2*(1-sun)*(0.35+0.65*(0.5-0.5*cos(2*pi*p)))',('sun','p'))
WV=[(0,0,-135)]+[(9840*r_/8*math.cos(2*math.pi*j/96),5600*r_/8*math.sin(2*math.pi*j/96),-135) for r_ in range(1,9) for j in range(96)]
WF=[(0,1+j,1+(j+1)%96) for j in range(96)]+[(1+(r_-1)*96+j,1+(r_-1)*96+(j+1)%96,1+r_*96+(j+1)%96,1+r_*96+j) for r_ in range(1,8) for j in range(96)]
WO=mesh('Basin_Water_Surface',WV,WF,'Basin_Water',WT)
if WO.data.polygons[0].normal.z<0:WO.data.flip_normals()
# Service platforms over the basin: hex discs on single tall stalks with masts/antennas at varied heights; two small ones hang off the arch crown near the intake.
ISLANDS=[(-5700,-3500,800,0,1100),(-2400,3000,650,0,900),(4300,700,520,0,700),(1000,-3600,420,0,1500),(-7200,2600,340,0,1750),(6600,-2300,300,0,1000)]
HANG=[(-6900,-2100,250,0,3050),(-6900,2100,250,0,3050)]
hexp=lambda x,y,r,rot=.26:[(x+r*math.cos(rot+k*math.pi/3),y+r*math.sin(rot+k*math.pi/3)) for k in range(6)]
for i,(x,y,rx,ry,z) in enumerate(ISLANDS+HANG):
 hang=i>=len(ISLANDS);c=C if hang else SECTORS[sector(math.atan2(y,x))];hx=hexp(x,y,rx);nm=f'{"H" if hang else ""}{i}'
 if not hang:
  cyl(f'Island_stalk_{nm}',(x,y,-800),(x,y,z-60),rx*.16+80,'Graphite_Structural_Alloy',c,rx*.08+40)
  for k,zz in enumerate((.3,.62,.88)):cyl(f'Island_stalk_ring_{nm}_{k}',(x,y,zz*z-18),(x,y,zz*z+18),rx*.1+75,'Edge_Titanium',c)
 else:
  q=(x-430*(1 if x>-7000 else 1),y,z+20);beam(f'Island_arch_hanger_{nm}',(-7560,y,4070),q,150,170,'Graphite_Structural_Alloy',c);beam(f'Island_arch_hanger_b_{nm}',(-7560,y,4070),(x-rx*.5,y+rx*.5*(1 if y>0 else -1),z+20),90,100,'Edge_Titanium',c)
 loft(f'Maintenance_island_core_{nm}',[[(px_,py_,zz) for px_,py_ in hexp(x,y,rr)] for rr,zz in ((.3*rx,z-190),(.86*rx,z-55),(.9*rx,z))],'Graphite_Structural_Alloy',c)
 prism(f'Maintenance_island_deck_{nm}',hx,z,z+30,'Ceramic_Ivory',c);prism(f'Island_teal_rim_{nm}',hexp(x,y,rx*1.012),z+10,z+18,'Teal_Inlay',c)
 for k in range(6):beam(f'Island_rib_{nm}_{k}',(x+(hx[k][0]-x)*.35,y+(hx[k][1]-y)*.35,z-175),(x+(hx[k][0]-x)*.88,y+(hx[k][1]-y)*.88,z-40),30,40,'Edge_Titanium',c)
 cube(f'Island_gallery_{nm}',(x,y,z+80),(rx*.8,rx*.5,100),'Cavity_Dark',c,.2);cube(f'Island_gallery_roof_{nm}',(x,y,z+140),(rx*.96,rx*.62,22),'Ceramic_Pearl',c,.2)
 for k in range(3):cyl(f'Island_vent_{nm}_{k}',(x-rx*.3+k*rx*.28,y+rx*.18,z+151),(x-rx*.3+k*rx*.28,y+rx*.18,z+151+rx*.07+14),rx*.07+8,'Ceramic_Slate',c)
 mh=rx*.7+60+40*(i%3);cyl(f'Island_mast_{nm}',(x+rx*.3,y-rx*.2,z+151),(x+rx*.3,y-rx*.2,z+151+mh),9,'Edge_Titanium',c,4,8);cube(f'Island_mast_cap_{nm}',(x+rx*.3,y-rx*.2,z+151+mh),(16,16,16),'Teal_Inlay',c)
 for k in range(5):cube(f'Island_antenna_short_{nm}_{k}',(x-rx*.12+k*rx*.06,y+rx*.08,z+151+rx*.05+20*k%3*8),(8,8,rx*.12+30*(k%2)),'Edge_Titanium',c)
 for k in (0,2,4):
  px_,py_=hx[k];ux,uy=(px_-x),(py_-y);beam(f'Island_dock_arm_{nm}_{k}',(x+ux*.9,y+uy*.9,z+18),(x+ux*1.5,y+uy*1.5,z+18),max(24,rx*.05),max(24,rx*.04),'Edge_Titanium',c)
  cube(f'Island_dock_pad_{nm}_{k}',(x+ux*1.62,y+uy*1.62,z+14),(rx*.26,rx*.26,28),'Ceramic_Slate',c);cube(f'Island_dock_light_{nm}_{k}',(x+ux*1.62,y+uy*1.62,z+34),(rx*.12,rx*.12,10),'Teal_Inlay',c)
# Habitable-scale inset access gallery under the front intake rim (clear 30 m doorway).
for i,y in enumerate([-4150,-3700,3100]):
 arch_s=math.sqrt(1-(y/4350)**2);z=260+(H-325)*arch_s-220;x=-11400+2300*arch_s+1700;c=C;poly=[(x-650,y-170),(x+650,y-170),(x+900,y-50),(x+780,y+130),(x-650,y+130)]
 prism(f'Intake_access_deck_{i}',poly,z,z+28,'Graphite_Structural_Alloy',c)
 prism(f'Intake_access_deck_armor_{i}',poly,z+28,z+40,'Ceramic_Ivory',c)
 for sy in [-1,1]:beam(f'Intake_deck_brace_{i}_{sy}',(x-500,y+sy*210,z+220),(x+600,y+sy*210,z-20),35,60,'Edge_Titanium',c)
 beam(f'Intake_deck_crosstie_{i}',(x+600,y-230,z-20),(x+600,y+230,z-20),35,40,'Edge_Titanium',c)
 cube(f'Gallery_floor_{i}',(x-300,y,z+48),(290,150,16),'Ceramic_Slate',c)
 for sy in [-1,1]:cube(f'Gallery_side_{i}_{sy}',(x-350,y+sy*70,z+81),(260,12,60),'Graphite_Structural_Alloy',c)
 cube(f'Gallery_roof_{i}',(x-350,y,z+118),(290,160,14),'Ceramic_Ivory',c)
 for k in range(5):cube(f'Gallery_window_mullion_{i}_{k}',(x-460+k*45,y-75,z+83),(6,8,55),'Edge_Titanium',c)
 # Human-scale cues: 1.1 m handrail posts + rail along the deck front, 2.4 m door frames on the gallery, 30 m light posts at the deck corners.
 for k in range(28):cube(f'Rail_post_{i}_{k}',(x-640+k*48,y-168,z+41),(.3,.3,1.1),'Edge_Titanium',c)
 beam(f'Rail_top_{i}',(x-640,y-168,z+41.6),(x+640,y-168,z+41.6),.2,.2,'Edge_Titanium',c)
 for k in range(2):
  dx=x-420+k*120;[cube(f'Door_jamb_{i}_{k}_{sg}',(dx+sg*.6,y-76,z+57),(.15,.15,2.4),'Edge_Titanium',c) for sg in (-1,1)];cube(f'Door_lintel_{i}_{k}',(dx,y-76,z+58.3),(1.35,.15,.15),'Edge_Titanium',c)
 for sx in (-1,1):cube(f'Deck_light_post_{i}_{sx}',(x+sx*620,y-160,z+55),(5,5,30),'Edge_Titanium',c);cube(f'Deck_light_cap_{i}_{sx}',(x+sx*620,y-160,z+71),(9,9,6),'Teal_Inlay',c)
# Two service bridges at basin margins; deck is 72 m wide with paired 4 m high rails.
for i,(island,theta) in enumerate([(0,math.radians(220)),(1,math.radians(105))]):
 x,y,rx,ry,z=ISLANDS[island];p=pt(theta,0,420);q=(x,y,z+45);c=SECTORS[sector(theta)];beam(f'Access_bridge_deck_{i}',p,q,72,18,'Ceramic_Ivory',c)
 direction=(Vector(q)-Vector(p)).normalized();side=direction.cross(Vector((0,0,1))).normalized()
 for s in [-1,1]:beam(f'Access_bridge_rail_{i}_{s}',Vector(p)+side*34*s+Vector((0,0,13)),Vector(q)+side*34*s+Vector((0,0,13)),3,4,'Edge_Titanium',c)
 for k,e_ in enumerate((Vector(p),Vector(q))):
  for s in [-1,1]:cube(f'Bridge_light_post_{i}_{k}_{s}',e_+side*36*s+Vector((0,0,17)),(3,3,30),'Edge_Titanium',c);cube(f'Bridge_light_cap_{i}_{k}_{s}',e_+side*36*s+Vector((0,0,33)),(6,6,5),'Teal_Inlay',c)
# Create a very low-poly proxy from only the major foundation and arch silhouette.
PROX=[]
for ob in ARCH:
 if ob.name.startswith(('Foundation_','Intake_arch_dark_shell_','Arch_shoulder_buttress_','Dam_body_','Scoop_shell','Crown_floating_armor_','Maintenance_island_core_','Primary_dark_wing_','Outer_terrace_dark_','Raised_shoulder_','Intake_oblique_armor_','Maintenance_island_deck_')):
  cp=bpy.data.objects.new('Proxy_'+ob.name,ob.data.copy());PX.objects.link(cp);cp.matrix_world=ob.matrix_world.copy();PROX.append(cp)
PX.hide_render=True;PX.hide_viewport=True
# Preview only: thin land shelf, water and restrained segmented waterfall ribbons.
# Low, irregular plateau around the structure leaves basin hollow.
vs=[];fs=[];N=180
for ring,r in enumerate([1.01,1.15,1.38,2.4]):
 for j in range(N):
  t=2*math.pi*j/N;rr=r*(1+.008*math.sin(t*7));z=(-120 if ring==0 else -190 if ring==1 else -240 if ring==2 else -310)+0;vs.append((14000*rr*math.cos(t),9000*rr*math.sin(t),z))
for ring in range(3):
 for j in range(N):q=(j+1)%N;fs.append((ring*N+j,ring*N+q,(ring+1)*N+q,(ring+1)*N+j))
mesh('Preview_Only_Context_Land',vs,fs,'Ground_Moss',ENV)
# Basin water is presentation context and intentionally absent from architecture GLBs.
# Background ground is a ring with a hole over the basin so the sealed floor and rings stay visible through the water (the old preview basin water is replaced by Basin_Water_Surface).
GV=[(9890*math.cos(2*math.pi*j/128),5655*math.sin(2*math.pi*j/128),-310) for j in range(128)]+[(85000*math.cos(2*math.pi*j/128),85000*math.sin(2*math.pi*j/128),-310) for j in range(128)]
mesh('Preview_Only_Background_Ground',GV,[(j,128+j,128+(j+1)%128,(j+1)%128) for j in range(128)],'Ground_Rock',ENV)
# Water falls at the inner basin lip, primarily below the 500 m terraces.
for j,deg in enumerate([d_ for d_ in range(0,360,10) if not 300<=d_<=340]):
 t=math.radians(deg+.05);tm=math.radians(deg+2.5);z=(420+75*math.sin(tm*3)+45*math.sin(tm*7))*.55;wedge(f'Preview_Only_Condensed_Fall_{j:02d}',t-.007,t+.007,-.034,-.027,-140,z-12,'Preview_Foam',ENV,2)
# Clouds are preview-only volumetric dressing, excluded from every GLB.
# A few diffuse low banks preserve the architectural opening and floor visibility.
cloudmat=bpy.data.materials.new('Preview_Only_Cloud_Volume');cloudmat.use_nodes=True;nt=cloudmat.node_tree;nt.nodes.clear();out=nt.nodes.new('ShaderNodeOutputMaterial');vol=nt.nodes.new('ShaderNodeVolumePrincipled');vol.inputs['Color'].default_value=(.75,.82,.85,1);vol.inputs['Density'].default_value=.0016;nt.links.new(vol.outputs['Volume'],out.inputs['Volume'])
for i,(x,y,z,sx,sy,sz) in enumerate([(-6200,1700,160,2200,1500,450),(-4300,-900,120,2400,1200,320),(1800,300,100,2600,1200,350),(5500,-1700,160,1700,1300,400)]):
 bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2,radius=1,location=(x,y,z));ob=bpy.context.object;ob.name=f'Preview_Only_Cloud_Volume_{i:02d}';ob.scale=(sx,sy,sz);ob.data.materials.append(cloudmat)
 for cc in list(ob.users_collection):cc.objects.unlink(ob)
 ENV.objects.link(ob);ob.hide_render=True;ob.hide_viewport=True
# Preview-only steam: Principled-volume blobs boiling off the basin and pouring over the crest and down the back of the hood; off by default, enabled for the renders, never exported.
steammat=bpy.data.materials.new('Preview_Only_Steam_Volume');steammat.use_nodes=True;nt=steammat.node_tree;nt.nodes.clear();out=nt.nodes.new('ShaderNodeOutputMaterial');vol=nt.nodes.new('ShaderNodeVolumePrincipled');vol.inputs['Color'].default_value=(.93,.96,.98,1);tc=nt.nodes.new('ShaderNodeTexCoord');gr=nt.nodes.new('ShaderNodeTexGradient');gr.gradient_type='SPHERICAL';nz=nt.nodes.new('ShaderNodeTexNoise');nz.inputs['Scale'].default_value=3.0;nz.inputs['Detail'].default_value=6.0;mu=nt.nodes.new('ShaderNodeMath');mu.operation='MULTIPLY';m2=nt.nodes.new('ShaderNodeMath');m2.operation='MULTIPLY';m2.inputs[1].default_value=.0045;nt.links.new(tc.outputs['Object'],gr.inputs['Vector']);nt.links.new(tc.outputs['Object'],nz.inputs['Vector']);nt.links.new(gr.outputs['Fac'],mu.inputs[0]);nt.links.new(nz.outputs['Fac'],mu.inputs[1]);nt.links.new(mu.outputs[0],m2.inputs[0]);nt.links.new(m2.outputs[0],vol.inputs['Density']);nt.links.new(vol.outputs['Volume'],out.inputs['Volume']);STEAM=[]
for i,(x,y,z,sx,sy,sz) in enumerate([(-3500,-1800,250,2600,1800,500),(2000,1500,300,3000,2000,650),(-7000,200,700,1900,1500,800),(0,-3500,200,2400,1200,400),(-9600,0,5300,1700,3000,800),(-10900,300,4600,1300,2200,900),(-12600,-300,2800,1100,1800,1300),(-8600,1700,3600,1200,900,900)]):
 bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2,radius=1,location=(x,y,z));ob=bpy.context.object;ob.name=f'Preview_Only_Steam_{i:02d}';ob.scale=(sx,sy,sz);ob.data.materials.append(steammat)
 for cc in list(ob.users_collection):cc.objects.unlink(ob)
 ENV.objects.link(ob);ob.hide_viewport=True;ob.hide_render=True;STEAM.append(ob)
def steam(on):
 for o_ in STEAM:o_.hide_render=not on
# Presentation lighting.
S.world=bpy.data.worlds.new('World_Pale_Haze');S.world.use_nodes=True;wn=S.world.node_tree.nodes;wn.get('Background').inputs['Color'].default_value=(.49,.62,.72,1);wn.get('Background').inputs['Strength'].default_value=.55
ld=bpy.data.lights.new('Sun_Warm','SUN');lo=bpy.data.objects.new('Sun_Warm',ld);RIG.objects.link(lo);lo.rotation_euler=(math.radians(25),math.radians(-28),math.radians(-35));ld.energy=3.5;ld.angle=.11
ld=bpy.data.lights.new('Sky_Softbox','AREA');lo=bpy.data.objects.new('Sky_Softbox',ld);RIG.objects.link(lo);lo.location=(0,-6000,22000);ld.energy=1500000000;ld.shape='DISK';ld.size=22000

ld=bpy.data.lights.new('Bore_Fill','POINT');lo=bpy.data.objects.new('Bore_Fill',ld);RIG.objects.link(lo);lo.location=(-10300,0,3000);ld.energy=8e7;ld.shadow_soft_size=400
def camera(n,loc,target,lens=48,ortho=None):
 d=bpy.data.cameras.new(n);o=bpy.data.objects.new(n,d);RIG.objects.link(o);o.location=loc;o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler();d.lens=lens;d.clip_start=5;d.clip_end=300000
 if ortho:d.type='ORTHO';d.ortho_scale=ortho
 return o
CAM=camera('Camera_01_Wide_Actual_Blender',(27000,-33000,17300),(-500,0,1100),44)
CAM2=camera('Camera_02_Intake_Bore',(6000,-15500,7600),(-9400,0,2150),48)
CAM3=camera('Camera_03_Architecture_Top',(0,0,50000),(0,0,0),50,32000)
CAM4=camera('Camera_04_Maintenance_Gallery',(-6100,-5900,2600),(-9000,-4150,1450),42)
CAM5=camera('Camera_05_Turbine_Throat',(-7000,-1300,2500),tuple(CT+EX*100),38)
CAM6=camera('Camera_06_Arch_Gills',(-13200,-6800,7600),(-9900,-1000,4300),44)
CAM7=camera('Camera_07_Breath',(-2500,-7800,4600),(-10000,-300,2500),30)
CAM8=camera('Camera_08_Basin_Overview',(15500,-15500,10500),(-1500,200,-300),34)
# Breath timeline: 240 frames @24 fps = one cycle (frame 1 = phase 0); breath_phase keyed linear 0..1 with a cyclic F-modifier.
BC['breath_phase']=0.0;BC.keyframe_insert('["breath_phase"]',frame=1);BC['breath_phase']=1.0;BC.keyframe_insert('["breath_phase"]',frame=241);act=BC.animation_data.action;act.name='Breath_Phase_Loop'
for fc in act.layers[0].strips[0].channelbag(act.slots[0]).fcurves:fc.modifiers.new('CYCLES')
S.frame_start=1;S.frame_end=240;S.render.fps=24
def set_phase(p):
 f=1+240*p;S.frame_set(int(f),subframe=f-int(f));bpy.context.view_layer.update()
set_phase(0)
def xdrv(fc,expr):
 d=fc.driver;d.type='SCRIPTED';d.expression=expr;v=d.variables.new();v.name='sun';v.type='SINGLE_PROP';v.targets[0].id=BC;v.targets[0].data_path='["sun_available"]'
# Day/night crossfade drives the preview world and lights too (sun_available 0 = dim night world and sun).
xdrv(S.world.node_tree.nodes.get('Background').inputs['Strength'].driver_add('default_value'),'0.05+0.50*sun');xdrv(bpy.data.lights['Sun_Warm'].driver_add('energy'),'0.12+3.38*sun');xdrv(bpy.data.lights['Sky_Softbox'].driver_add('energy'),'1500000000*(0.03+0.97*sun)')
S.camera=CAM;S.render.engine='CYCLES';S.cycles.samples=20 if a.quick else 48;S.cycles.use_denoising=False
S.render.resolution_x=1440;S.render.resolution_y=1000;S.render.resolution_percentage=100;S.render.image_settings.file_format='PNG';S.view_settings.view_transform='AgX'
bpy.context.view_layer.update()
# Driver self-check (background-safe): drivers valid, poses change with phase, loop closes (phase 1.0 == phase 0).
_dr=[(ob.name,fc.data_path) for ob in MOVABLE if ob.animation_data for fc in ob.animation_data.drivers if not fc.driver.is_valid]
print('DRIVERS total',sum(len(ob.animation_data.drivers) for ob in MOVABLE if ob.animation_data),'invalid',_dr,flush=True)
def pose():return {ob.name:round({'AXIS_ANGLE':ob.rotation_axis_angle[0],'XYZ':ob.rotation_euler[1]}[ob.rotation_mode]+ob.location.x*0 if ob.parent is None else ob.location.x,3) for ob in MOVABLE}
for _p in (0,.25,.5,.64,.75):
 set_phase(_p);_q=pose();print('POSE',_p,'vane0/4 deg',[round(math.degrees(_q[k]),2) for k in ('VanePivot_00','VanePivot_04')],'gill00/15 deg',[round(math.degrees(_q[k]),2) for k in ('Gill_Pivot_00','Gill_Pivot_15')],'rotorA/B revs',[round(_q[k]/(2*math.pi),3) for k in ('Turbine_Rotor_A','Turbine_Rotor_B')],'ram rod x',_q['Vane_RamRod_02'],flush=True)
set_phase(0);_a=pose();S.frame_set(241);_b=pose()
print('LOOP frame1 vs frame241 max diff (turbine mod 2pi)',max(abs(_a[k]-_b[k]) if not k.startswith(('Turbine','Guide_Ring')) else abs(math.remainder(_a[k]-_b[k],2*math.pi)) for k in _a),flush=True)
set_phase(0);_g=[abs(ob.rotation_axis_angle[0]) for ob in GILLS];print('GILLS at phase 0 max angle rad',max(_g),flush=True)
bpy.ops.object.select_all(action='DESELECT')
for ob in ARCH:ob.select_set(True)
bpy.context.view_layer.objects.active=ARCH[0];bpy.ops.object.shade_smooth_by_angle(angle=math.radians(40))
# Tighten canonical bounds to 28 x18 km target without touching arch height.
# All primary vertices are real metres. Object transforms for vane pivots remain meaningful.
# Save current authoring state before exports.
# Streaming prep: tag every exported object with detail_class (structure|dressing|moving|water) and arch pieces with their chunk.
DRESS=('Intake_panel_seam','Intake_lane_seam','Gantry_','Turbine_Grille','Rail_','Door_','Deck_light','Bridge_light','Gallery_window','Vane_clevis','Vane_plinth','Vane_ram_','Lip_window','Rim_window','Rim_teal','Island_antenna','Island_vent','Island_dock_light','Island_mast','Island_rib','Island_teal_rim','Arch_leg_seam','Arch_leg_panel','Arch_leg_light','Lip_wall_light','Lip_pilaster','Sluice_jamb','Intake_rim_light','Crown_lip_light','Bore_truss','Bore_fin_edge','Dam_crest_teal','Dam_gate_teal','Dam_gate_dark','Exhaust_lamella','Scoop_seam','Gill_slit','Heliostat_post','Wing_vent','Heat_spoke','Gallery_side')
CHUNKS=['Hood_Shell','Nozzle_Guide','Throat_Compressor','Vane_Bank','Gills']
def chunk_of(n):
 if n.startswith('Gill_'):return 'Gills'
 if n.startswith(('Nozzle_segment','Guide_Ring_')):return 'Nozzle_Guide'
 if n.startswith('Turbine_'):return 'Throat_Compressor'
 if n.startswith(('Movable_intake_vane','VanePivot','Vane_')):return 'Vane_Bank'
 if n.startswith(('Intake_arch_dark_shell','Intake_oblique_armor','Intake_underside_rib','Intake_continuous','Intake_teal_inlay','Intake_lane_seam','Intake_panel_seam','Intake_rim_light','Crown_','Bore_','Gantry_','Scoop_','Arch_shoulder','Arch_leg')):return 'Hood_Shell'
 return None
def dclass(ob):
 if ob.type=='EMPTY':return 'moving'
 if ob.name.startswith(DRESS):return 'dressing'
 return 'moving' if ob.users_collection[0]==V else 'structure'
def tri(ob):return sum(len(p.vertices)-2 for p in ob.data.polygons) if ob.type=='MESH' else 0
for ob in list(ARCH)+list(MOVABLE):
 ob['export_category']='architecture';ob['units']='metres';ob['detail_class']=dclass(ob);ch=chunk_of(ob.name)
 if ch:ob['chunk']=ch
WO['export_category']='architecture_water';WO['units']='metres';WO['detail_class']='water'
TC={'structure':0,'dressing':0,'moving':0,'water':tri(WO)}
for ob in ARCH:TC[ob['detail_class']]+=tri(ob)
print('TRIANGLES by class',TC,'architecture total',sum(TC[k] for k in ('structure','dressing','moving')),flush=True)
for ob in ENV.objects:ob['export_category']='preview_only'
S['asset_title']='Weather Lung | Central climate machine';S['footprint_target_m']=[28000,18000];S['arch_height_parameter_m']=H;S['preview_vfx_excluded']=True;S['weather_simulation_integrated']=False
bpy.ops.object.select_all(action='DESELECT');bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Weather_Lung_Central_Station.blend'))

def export_glb(path,obs,anim=False):
 bpy.ops.object.select_all(action='DESELECT')
 for ob in obs:ob.hide_set(False);ob.select_set(True)
 bpy.context.view_layer.objects.active=next((o for o in obs if o.type=='MESH'),None)
 bpy.ops.export_scene.gltf(filepath=str(path),export_format='GLB',use_selection=True,export_apply=False,export_yup=True,export_extras=True,export_animations=anim,export_materials='EXPORT',**(dict(export_animation_mode='SCENE',export_frame_range=True,export_force_sampling=True) if anim else {}))

def stats(obs):
 vs=[];tri=0
 for ob in obs:
  if ob.type!='MESH':continue
  vs += [tuple(ob.matrix_world@v.co) for v in ob.data.vertices];tri+=sum(len(p.vertices)-2 for p in ob.data.polygons)
 return {'objects':len(obs),'vertices':len(vs),'triangles':tri,'bounds_min_m':[min(v[k] for v in vs) for k in range(3)],'bounds_max_m':[max(v[k] for v in vs) for k in range(3)]} if vs else {}
def batched_mesh(obs,name,origin):
 vs=[];fs=[];mids=[];used=[]
 for ob in obs:
  if ob.type!='MESH':continue
  off=len(vs);vs.extend(tuple(ob.matrix_world@v.co-origin) for v in ob.data.vertices)
  for f in ob.data.polygons:
   fs.append(tuple(off+k for k in f.vertices));ma=ob.data.materials[f.material_index]
   if ma not in used:used.append(ma)
   mids.append(used.index(ma))
 me=bpy.data.meshes.new(name);me.from_pydata(vs,[],fs);me.update()
 for ma in used:me.materials.append(ma)
 for f,mi in zip(me.polygons,mids):f.material_index=mi
 ob=bpy.data.objects.new(name,me);S.collection.objects.link(ob);bpy.context.view_layer.update();bpy.ops.object.select_all(action='DESELECT');ob.select_set(True);bpy.context.view_layer.objects.active=ob;bpy.ops.object.shade_smooth_by_angle(angle=math.radians(40));return ob
if not a.preview_only:
 BC['ripple_lag']=0.0;BC['sun_available']=0.0;set_phase(0)  # static exports (heliostats stowed) use the exact rest pose (all parts closed, zero lag)
 manifest={'units':'metres','blender_axes':{'X':'circumferential tangent','Y':'across ring','Z':'radial up/inward'},'gltf_godot_axes':{'X':'circumferential tangent','Y':'up','Z':'negative Blender Y'},'ring_radius_m':5000001.506,'sector_assembly_convention':'Each sector mesh local = original - origin_blender_m; place at origin_godot_m. Do not rescale.','architecture':stats(ARCH),'arch_height_parameter_m':H,'sectors':[],'preview_vfx_exported':False,'weather_simulation_integrated':False}
 batches=[]
 for i,c in SECTORS.items():
  obs=list(c.objects)
  if not obs:continue
  bb=stats(obs);origin=Vector([(bb['bounds_min_m'][k]+bb['bounds_max_m'][k])/2 for k in range(3)])
  cp=batched_mesh(obs,f'Weather_Lung_Sector_{i:02d}',origin)
  file=f'sectors/Weather_Lung_Sector_{i:02d}.glb';export_glb(OUT/'exports'/file,[cp])
  cp.location=origin;batches.append(cp)
  ent={'id':i,'file':file,'origin_blender_m':list(origin),'origin_godot_m':[origin.x,origin.z,-origin.y],'stats':bb,'batch_material_surfaces':len(cp.data.materials)}
  # Streaming split: per-class GLBs share this sector origin; arch-chunk pieces are delivered by exports/chunks instead.
  loc=[o for o in obs if o.get('chunk') is None]
  for cls in ('structure','dressing'):
   sel=[o for o in loc if o['detail_class']==cls]
   if sel:
    cc=batched_mesh(sel,f'Weather_Lung_Sector_{i:02d}_{cls.capitalize()}',origin);f2=f'sectors/Weather_Lung_Sector_{i:02d}_{cls.capitalize()}.glb';export_glb(OUT/'exports'/f2,[cc]);bpy.data.objects.remove(cc,do_unlink=True);ent[cls+'_file']=f2;ent[cls+'_triangles']=sum(tri(o) for o in sel)
  ent['triangles_by_detail_class_incl_arch_chunks']={cl:sum(tri(o) for o in obs if o['detail_class']==cl) for cl in ('structure','dressing')}
  manifest['sectors'].append(ent)
 bpy.context.view_layer.update()
 vaneobs=[o for o in ARCH if o.users_collection[0]==V]
 export_glb(OUT/'exports/Weather_Lung_Assembled.glb',batches+vaneobs+MOVABLE)
 manifest['assembled_static_mesh_nodes']=len(batches)
 for cp in batches:bpy.data.objects.remove(cp,do_unlink=True)
 # Vane bank exported around its own local origin; preserved hinge hierarchy.
 vo=Vector((-10500,0,340))
 for ob in MOVABLE:
  if not ob.parent:ob.location-=vo
 bpy.context.view_layer.update()
 export_glb(OUT/'exports/modules/Weather_Lung_Intake_Vanes.glb',vaneobs+MOVABLE)
 for ob in MOVABLE:
  if not ob.parent:ob.location+=vo
 bpy.context.view_layer.update()
 # Arch sub-chunks (one GLB each, local to the module origin): static pieces batched per class, moving pieces and pivots kept as nodes.
 manifest['arch_chunks']={}
 for chn in CHUNKS:
  stat=[o for o in ARCH if o.get('chunk')==chn and o.users_collection[0]!=V];mov=[o for o in ARCH if o.get('chunk')==chn and o.users_collection[0]==V];pivs=[o for o in MOVABLE if chunk_of(o.name)==chn];bl=[]
  for cls in ('structure','dressing'):
   sel=[o for o in stat if o['detail_class']==cls]
   if sel:bl.append(batched_mesh(sel,f'{chn}_{cls.capitalize()}',vo))
  for ob in pivs:
   if not ob.parent:ob.location-=vo
  bpy.context.view_layer.update();export_glb(OUT/'exports/chunks'/f'Weather_Lung_Chunk_{chn}.glb',bl+mov+pivs)
  for ob in pivs:
   if not ob.parent:ob.location+=vo
  bpy.context.view_layer.update()
  for cp in bl:bpy.data.objects.remove(cp,do_unlink=True)
  manifest['arch_chunks'][chn]={'file':f'chunks/Weather_Lung_Chunk_{chn}.glb','origin_blender_m':list(vo),'origin_godot_m':[vo.x,vo.z,-vo.y],'objects':len(stat)+len(mov),'pivots':[o.name for o in pivs if o.get('detail_class')=='moving'],'triangles_by_detail_class':{cl:sum(tri(o) for o in stat+mov if o['detail_class']==cl) for cl in ('structure','dressing','moving')},'visibility_km':{'structure':'0-30','dressing':'0-2 (fade 250 m)','moving':'0-8'}}
 export_glb(OUT/'exports/Weather_Lung_Water_Surface.glb',[WO])
 manifest['vane_module']={'file':'modules/Weather_Lung_Intake_Vanes.glb','origin_blender_m':list(vo),'origin_godot_m':[vo.x,vo.z,-vo.y],'local_hinge_axis_godot':'negative Z','suggested_angle_degrees':[0,35],'animation_included':False}
 PD={'VanePivot_':('local Y (Blender) = -Z in glTF/Godot','rotation_euler.y, 0 closed .. +35 deg open (positive = open)'),'Vane_RamCyl_':('local Y; aims at the fin attach pin','rotation_euler.y driven (IK of the ram)'),'Vane_RamRod_':('child of RamCyl, slides along its local +X','location.x extension in metres'),'Gill_Pivot_':('axis tangent to the arch at the plate front edge (see hinge_axis_blender extra)','rotation_axis_angle[0] radians, 0 closed .. +35 deg flared'),'Turbine_Rotor_':('bore axis tilted about Y (see spin_axis_blender extra), ~+X','rotation_axis_angle[0] radians, continuous; B counter-rotates'),'Guide_Ring_':('arch-plane normal (sheared 27 deg; axis stored in rotation_axis_angle)','rotation_axis_angle[0] radians, 1 revolution per breath cycle; B counter-rotates'),'Heliostat_Pivot_':('horizontal axis = Z x panel normal (stored in rotation_axis_angle)','rotation_axis_angle[0]: pi stowed face-down (sun_available 0) .. tracking angle (sun_available 1), smoothstep crossfade; driven by sun_available, not baked')}
 manifest['vane_module'].update({'pivots':len(MOVABLE),'pivot_list':[{'name':o.name,'parent':o.parent.name if o.parent else None,'axis':[v for k,v in PD.items() if o.name.startswith(k)][0][0],'channel':[v for k,v in PD.items() if o.name.startswith(k)][0][1],'location_blender_m':[round(q,2) for q in o.matrix_world.translation]} for o in MOVABLE],'animation_file':'modules/Weather_Lung_Intake_Vanes_Animated.glb' if a.export_anim else None,'note':'Module now holds vanes + rams + two turbine rotors + gill plates; static GLB is the closed rest pose (breath_phase 0).'})
 manifest['breath_cycle']={'control':'Breath_Control (Blender only, not exported)','props':{k:BC[k] for k in ('breath_phase','cycle_seconds','timeline_frames','vane_amplitude_deg','gill_amplitude_deg','turbine_max_rpm','ripple_lag')},'timeline':'240 frames @24fps per cycle, frame 1 = phase 0','inhale':'phase 0..0.5','exhale':'phase 0.5..1','turbine_revolutions_per_cycle':f_revs(),'actions':'Breath_Cycle_<pivot> per moving pivot (sampled keys, cyclic)'}
 manifest['triangles_before_detail_pass']=24872
 manifest['triangles_by_detail_class']=TC;manifest['triangles_architecture_total']=sum(TC[k] for k in ('structure','dressing','moving'))
 manifest['visibility_ranges_km']={'far_proxy':'>30','structure':'0-30','dressing':'0-2 (fade 250 m)','moving':'0-8','water':'0-30'}
 manifest['detail_class_note']='Every exported object carries custom prop detail_class (structure|dressing|moving|water) and arch pieces carry chunk (Hood_Shell|Nozzle_Guide|Throat_Compressor|Vane_Bank|Gills). Per-sector Structure/Dressing GLBs exclude arch-chunk pieces (delivered by exports/chunks/*); the combined sector GLBs and the assembled GLB contain everything. Rams are classed dressing even inside the moving module.'
 manifest['basin']={'floor_top_z_m':FL,'floor_underside_z_m':FL-70,'water_level_z_m':-135,'rim_datum_z_m':0,'dam_degrees':[310,340],'foundations_extend_to_z_m':-800,'note':'Foundations/inner wall/dam go down to the sealed floor at -800 m (previously -90 m), so architecture bounds_min z is now about -870.'}
 manifest['water_surface']={'file':'Weather_Lung_Water_Surface.glb','object':'Basin_Water_Surface','collection':'05_WATER_Export','detail_class':'water','triangles':TC['water'],'origin_blender_m':[0,0,0],'level_z_m':-135,'visibility':'0-30 km','material':'Basin_Water (transmission 0.93, IOR 1.33)'}
 BC['ripple_lag']=BD['lag'];BC['sun_available']=1.0
 if a.export_anim:
  S.frame_set(1);bpy.context.view_layer.update()
  for ob in MOVABLE:
   if not ob.parent:ob.location-=vo
  bpy.context.view_layer.update();export_glb(OUT/'exports/modules/Weather_Lung_Intake_Vanes_Animated.glb',vaneobs+MOVABLE,True)
  for ob in MOVABLE:
   if not ob.parent:ob.location+=vo
  bpy.context.view_layer.update()
 set_phase(0)
 PX.hide_viewport=False
 cp=batched_mesh(PROX,'Weather_Lung_Far_Proxy',Vector((0,0,0)))
 export_glb(OUT/'exports/Weather_Lung_Far_Proxy.glb',[cp]);bpy.data.objects.remove(cp,do_unlink=True);PX.hide_viewport=True
 manifest['far_proxy']=stats(PROX);(OUT/'docs/asset_manifest.json').write_text(json.dumps(manifest,indent=2))
# Put a concise embedded guide in Blender.
t=bpy.data.texts.new('START_HERE_Weather_Lung.txt');t.write('WEATHER LUNG\n28 x18 km central station, 4.8 km arch.\nZ up; units metres. Architecture and vanes export.\n90_PREVIEW_ONLY is render dressing, not an integrated weather system.\n12 sector-local GLBs have assembly origins in docs/asset_manifest.json.\nVanePivot objects are authorable hinge controls; controller not supplied.\nWhole footprint is tangent-flat; conform/segment on Ringworld for final placement.\nSee README.md.\n')
# Default viewport: material colors, camera framing.
for area in bpy.context.screen.areas:
 if area.type=='VIEW_3D':area.spaces.active.clip_end=300000;area.spaces.active.region_3d.view_distance=36000;area.spaces.active.shading.color_type='MATERIAL'
bpy.ops.object.select_all(action='DESELECT');bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Weather_Lung_Central_Station.blend'))
if a.render:
 ALLC=[('01_Wide_Actual_Blender',CAM),('02_Intake_Bore',CAM2),('03_Architecture_Top',CAM3),('04_Maintenance_Gallery',CAM4),('05_Turbine_Throat',CAM5),('06_Arch_Gills',CAM6),('07_Breath',CAM7),('08_Day_Solar',CAM8),('09_Night_Grid',CAM8)];only=[s for s in a.only.split(',') if s]
 for name,cam in ALLC:
  if (only and not any(name.startswith(o) for o in only)) or (a.quick and not only and name!='01_Wide_Actual_Blender'):continue
  BC['sun_available']=0.0 if name.startswith('09') else 1.0;set_phase(.42 if name.startswith('09') else .64)  # mid-breath poses; night still is mid-inhale (heaters hot)
  steam(not name.startswith(('03','06')));S.camera=cam
  ENV.hide_render=(name=='03_Architecture_Top');S.render.filepath=str(OUT/'previews'/f'{name}.png');bpy.ops.render.render(write_still=True)
 steam(False);BC['sun_available']=1.0
 if (not a.quick and not only) or 'strip' in only:
  # 6-frame breath strip from Camera_07 at reduced resolution/samples (no steam, for readability).
  S.camera=CAM7;ENV.hide_render=False;rx,ry,sm=S.render.resolution_x,S.render.resolution_y,S.cycles.samples;S.render.resolution_x,S.render.resolution_y,S.cycles.samples=720,500,16
  for k,ph in enumerate((0,.17,.33,.5,.67,.83)):set_phase(ph);S.render.filepath=str(OUT/'previews'/f'breath_strip_{k:02d}.png');bpy.ops.render.render(write_still=True)
  S.render.resolution_x,S.render.resolution_y,S.cycles.samples=rx,ry,sm
 set_phase(0);ENV.hide_render=False;S.camera=CAM;bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'Weather_Lung_Central_Station.blend'))
if a.anim_preview:
 # Looping preview video: 48 frames across one cycle (frames 1,6,..,236), Camera_07, 720x500, low samples, H.264.
 import glob,os
 S.camera=CAM7;ENV.hide_render=False;S.render.resolution_x,S.render.resolution_y=720,500;S.cycles.samples=8 if a.quick else 12;S.cycles.use_denoising=True
 S.render.image_settings.media_type='VIDEO';S.render.image_settings.file_format='FFMPEG';S.render.ffmpeg.format='MPEG4';S.render.ffmpeg.codec='H264';S.render.ffmpeg.constant_rate_factor='MEDIUM';S.render.fps=12;S.frame_start=1;S.frame_end=236;S.frame_step=5
 for f_ in glob.glob(str(OUT/'previews'/'breath_cycle*.mp4')):os.remove(f_)
 S.render.filepath=str(OUT/'previews'/'breath_cycle');bpy.ops.render.render(animation=True)
 for f_ in glob.glob(str(OUT/'previews'/'breath_cycle*.mp4')):os.replace(f_,str(OUT/'previews'/'breath_cycle.mp4'))
 S.render.image_settings.media_type='IMAGE';S.render.image_settings.file_format='PNG';S.render.fps=24;S.frame_start=1;S.frame_end=240;S.frame_step=1;set_phase(0)
print('WEATHER_LUNG_BUILD_COMPLETE',str(OUT),flush=True)
