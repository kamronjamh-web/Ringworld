"""Add the approved 4 km outer-arm ring to the frozen main spire without editing it.
blender -b -t 4 --python scripts/build_outer_ring.py -- [--source /path/Main_Surface_Spire.blend]
"""
import bpy,bmesh,math,os,json,hashlib,sys
from mathutils import Vector
ROOT=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE='/workspace/shared/main_spire_asset/Main_Surface_Spire.blend'
args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
if '--source' in args:SOURCE=args[args.index('--source')+1]
source_hash=hashlib.sha256(open(SOURCE,'rb').read()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=SOURCE);s=bpy.context.scene
s.unit_settings.system='METRIC';s.unit_settings.scale_length=1;s.unit_settings.length_unit='METERS'
root=bpy.data.objects['MAIN_SPIRE_ASSEMBLY'];root.name='MAIN_SPIRE_OUTER_RING_ASSEMBLY'
source_names=[o.name for c in bpy.data.collections if c.name.startswith(('00_','10_','11_','12_','20_','21_','22_','23_','24_','25_')) for o in c.objects if o.type=='MESH']
def coll(name):
    c=bpy.data.collections.new(name);s.collection.children.link(c);return c
arms=coll('31_OUTER_STAGGERED_ARMS_EXPORT');feet=coll('32_OUTER_ARM_FOUNDATIONS_EXPORT');ties=coll('33_SIX_STAGE_OUTER_CONNECTOR_RINGS_EXPORT');entry=coll('34_OUTER_ARM_ENTRANCE_HALL_EXPORT');roof=coll('35_OUTER_ENTRANCE_REMOVABLE_ROOF_EXPORT');crown=coll('36_OUTER_4000M_OPEN_CROWN_EXPORT')
shell=bpy.data.materials['Obsidian_Alloy'];facet=bpy.data.materials['Raised_Facet_Alloy'];edge=bpy.data.materials['Worn_Silver_Edges'];black=bpy.data.materials['Deep_Recess'];glow=bpy.data.materials['Reactor_Teal_Inlay'];armor=bpy.data.materials['Main_Armor_Graphite'];floor=bpy.data.materials['Facility_Floor_Basalt_Alloy'];interior=bpy.data.materials['Facility_Interior_Ceramic']
def mesh(name,vs,fs,mats,col,ids=None):
    me=bpy.data.meshes.new(name+'_Mesh');me.from_pydata(vs,[],fs)
    for m in mats:me.materials.append(m)
    if ids:
        for p,i in zip(me.polygons,ids):p.material_index=i
    bm=bmesh.new();bm.from_mesh(me);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(me);bm.free();me.update();o=bpy.data.objects.new(name,me);col.objects.link(o);o['outer_ring_revision_addition']=True;return o
def world(r,t,z,a):
    a=math.radians(a);return (r*math.cos(a)-t*math.sin(a),r*math.sin(a)+t*math.cos(a),z)
def loft(name,sections,a,mats,col):
    n=len(sections[0][1]);v=[world(r,t,z,a) for z,ol in sections for r,t in ol];f=[tuple(reversed(range(n)))];ids=[0]
    for k in range(len(sections)-1):
        for j in range(n):f.append((k*n+j,k*n+(j+1)%n,(k+1)*n+(j+1)%n,(k+1)*n+j));ids.append(1 if len(mats)>1 and j in [1,2,6,7] else 0)
    f.append(tuple(range((len(sections)-1)*n,len(sections)*n)));ids.append(0);return mesh(name,v,f,mats,col,ids)
def box(name,r0,r1,t0,t1,z0,z1,a=0,mat=shell,col=entry):
    v=[world(r,t,z,a) for z in [z0,z1] for r,t in [(r0,t0),(r1,t0),(r1,t1),(r0,t1)]]
    return mesh(name,v,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],[mat],col)
def arc(name,profile,a0,a1,mat,col,step=5.625):
    n=max(1,math.ceil(abs(a1-a0)/step));full=abs(a1-a0)>=359.999;row=n if full else n+1
    aa=[a0+(a1-a0)*j/n for j in range(row)];v=[world(r,0,z,a) for r,z in profile for a in aa];f=[]
    for k in range(len(profile)):
        for j in range(n):f.append((k*row+j,k*row+(j+1)%row,((k+1)%len(profile))*row+(j+1)%row,((k+1)%len(profile))*row+j))
    if not full:f.extend([tuple(k*row for k in reversed(range(len(profile)))),tuple(k*row+n for k in range(len(profile)))])
    return mesh(name,v,f,[mat],col)
ST=[(0,730,80,60),(100,725,78,59),(300,700,74,57),(550,650,68,54),(1050,570,58,48),(1550,505,50,42),(2050,450,44,37),(2550,400,40,33),(3050,360,36,30),(3300,330,32,28),(3500,292,26,25),(3600,270,23,23),(3700,240,20,20),(3800,209,16,17),(3900,179,10,12),(3960,162,5,7),(3980,157,3.7,4.5),(4000,153.5,1.5,.75)]
def interp(z):
    for lo,hi in zip(ST[:-1],ST[1:]):
        if lo[0]<=z<=hi[0]:
            t=(z-lo[0])/(hi[0]-lo[0]);return tuple(lo[i]+(hi[i]-lo[i])*t for i in [1,2,3])
    return ST[-1][1:]
def section(z):
    r,d,w=interp(z);return [(r-d,-w*.48),(r-d*.65,-w),(r+d*.38,-w),(r+d*.9,-w*.52),(r+d,-w*.15),(r+d,w*.15),(r+d*.9,w*.52),(r+d*.38,w),(r-d*.65,w),(r-d,w*.48)]
def clip(poly,sign,bound=12):
    out=[]
    for p,q in zip(poly,poly[1:]+poly[:1]):
        pi=sign*p[1]>=bound;qi=sign*q[1]>=bound
        if pi:out.append(p)
        if pi!=qi:
            v=(sign*bound-p[1])/(q[1]-p[1]);out.append((p[0]+(q[0]-p[0])*v,sign*bound))
    return out
ANGLES=[22.5+45*i for i in range(8)];ENTRY=292.5;outer_names=[]
for i,a in enumerate(ANGLES,1):
    zvals=[x[0] for x in ST]
    if a==ENTRY:zvals=[24]+[z for z in zvals if z>24]
    o=loft(f'Outer_Arm_{i:02d}_Main_Monolith',[(z,section(z)) for z in zvals],a,[shell,armor,edge],arms);o['outer_arm_number']=i;o['azimuth_degrees']=a;o['foot_center_radius_m']=730.;o['height_m']=4000.;o['role']='New full-height outer sweeping arm, staggered 22.5 degrees from inner arms';outer_names.append(o.name)
    if a==ENTRY:
        for sign in [-1,1]:loft(f'Outer_Arm_{i:02d}_Entrance_Solid_Jamb_{sign:+d}',[(z,clip(section(z),sign)) for z in [0,24]],a,[shell,armor],entry)
    # Separate physically raised side ribs and narrow channels preserve the alien monolith style.
    for sign in [-1,1]:
        secs=[];channels=[]
        for z in [32,100,300,550,1050,1550,2050,2550,3050,3300,3500,3600,3700,3800,3900]:
            r,d,w=interp(z);rr=r+d*.57;tt=sign*w*.79;scale=max(.3,w/60)
            secs.append((z,[(rr-3*scale,tt-3*scale),(rr+7*scale,tt-3*scale),(rr+10*scale,tt),(rr+7*scale,tt+3*scale),(rr-3*scale,tt+3*scale)]))
            channels.append((z,[(rr+9*scale,tt-.65*scale),(rr+10*scale,tt-.65*scale),(rr+10*scale,tt+.65*scale),(rr+9*scale,tt+.65*scale)]))
        loft(f'Outer_Arm_{i:02d}_Raised_Energy_Rib_{sign:+d}',secs,a,[edge,facet],arms)
        loft(f'Outer_Arm_{i:02d}_Teal_Feed_{sign:+d}',channels,a,[glow],arms)
    # Six large side plates echo the preserved inner arms; none close the entry opening.
    for j,(z0,z1) in enumerate([(120,480),(620,970),(1120,1460),(1630,1960),(2130,2460),(2630,2960)],1):
        for sign in [-1,1]:
            secs=[]
            for z,f in [(z0,.2),(z0+35,1),(z1-45,.85),(z1,.12)]:
                r,d,w=interp(z);rr=r+d*.82;tt=sign*w*.47;ww=w*.23*f
                secs.append((z,[(rr-1,tt-ww),(rr+7,tt-ww*.75),(rr+11,tt),(rr+7,tt+ww*.75),(rr-1,tt+ww)]))
            loft(f'Outer_Arm_{i:02d}_Layered_Plate_{j:02d}_{sign:+d}',secs,a,[armor,edge],arms)
    # Every foot has a proper foundation. The entrance foot is physically split at its center.
    outline=[(622,-49),(644,-78),(798,-78),(852,-30),(852,30),(798,78),(644,78),(622,49)]
    top=[(632,-43),(650,-68),(790,-68),(838,-25),(838,25),(790,68),(650,68),(632,43)]
    if a==ENTRY:
        for sign in [-1,1]:loft(f'Outer_Foot_{i:02d}_Split_Foundation_{sign:+d}',[(0,clip(outline,sign)),(18,clip(top,sign))],a,[shell,facet],feet)
    else:loft(f'Outer_Foot_{i:02d}_Foundation',[(0,outline),(18,top)],a,[shell,facet],feet)
    for sign in [-1,1]:
        tt=sign*73;ol=[(660,tt-7),(824,tt-7),(858,tt-2),(858,tt+2),(824,tt+7),(660,tt+7)]
        loft(f'Outer_Foot_{i:02d}_Raised_Anchor_Rail_{sign:+d}',[(0,ol),(26,[(r-4,t*.98) for r,t in ol])],a,[armor,edge],feet)
# Stage rings intersect all eight outer arms at their true section radius. Eight radial
# links overlap the preserved inner collector sectors and the new outer track.
source_stages=[550,1050,1550,2050,2550,3050];contact_sets=[]
INNER_ST=[(0,500,96),(76,498,92),(250,472,80),(540,422,68),(900,374,58),(1320,330,50),(1800,290,42),(2280,252,36),(2720,226,32),(3080,206,28),(3280,200,28)]
def inner_interp(z):
    for p,q in zip(INNER_ST[:-1],INNER_ST[1:]):
        if p[0]<=z<=q[0]:
            f=(z-p[0])/(q[0]-p[0]);return p[1]+(q[1]-p[1])*f,p[2]+(q[2]-p[2])*f
for j,z in enumerate(source_stages,1):
    r,d,w=interp(z);ri=r-d*.40;ro=r+d+18
    ring=arc(f'Outer_Connector_Ring_Stage_{j:02d}',[(ri,z-20),(ro-15,z-28),(ro,z-10),(ro,z+13),(ro-17,z+31),(ri,z+20)],0,360,armor,ties,step=5.625)
    ring['stage_number']=j;ring['role']='Continuous structural annulus intersects all eight outer arm bodies'
    # Restrained segmented channels identify the ring without turning it into a luminous disc.
    for k,a in enumerate(ANGLES,1):
        arc(f'Outer_Ring_Stage_{j:02d}_Recess_{k:02d}',[(ro+.1,z-3),(ro+1,z-3),(ro+1,z+5),(ro+.1,z+5)],a+7,a+38,black,ties)
        arc(f'Outer_Ring_Stage_{j:02d}_Teal_{k:02d}',[(ro+1.05,z),(ro+1.6,z),(ro+1.6,z+1.5),(ro+1.05,z+1.5)],a+8,a+37,glow,ties)
        ir,id=inner_interp(z);start=ir+id+4;end=ri+18
        ol=[(start,-13),(end,-13),(end+6,-6),(end+6,6),(end,13),(start,13)]
        link=loft(f'Outer_Ring_Stage_{j:02d}_Radial_Tie_{k:02d}',[(z-16,ol),(z+16,ol)],a,[shell,edge],ties);link['stage_number']=j
        box(f'Outer_Ring_Stage_{j:02d}_Radial_Tie_Inset_{k:02d}',start+3,end,-1,1,z+16.05,z+16.8,a,glow,ties)
        contact_sets.append({'stage':j,'outer_arm':f'Outer_Arm_{k:02d}_Main_Monolith','outer_ring':ring.name,'radial_tie':link.name,'inner_collector':f'Accelerator_Stage_{j:02d}_Collector_Sector_{k:02d}','inner_arm':f'Arm_{k:02d}_Faceted_Monolith','inner_clamp':f'Accelerator_Stage_{j:02d}_Arm_Feed_Clamp_{k:02d}'})
# The eight new arms physically meet this open focus ring just below their pointed tips.
cap=arc('Outer_Crown_Open_4000m_Focus_Ring',[(150.8,3984),(164,3984),(168,3988),(164,3994),(150.8,3994)],0,360,armor,crown,step=2.8125)
cap['clear_beam_radius_m']=150.;cap['role']='Open annular crown physically intersecting all eight outer tips'
arc('Outer_Crown_Recessed_Teal_Lip',[(164.1,3991),(164.8,3991),(164.8,3992),(164.1,3992)],0,360,glow,crown,step=2.8125)
# Continuous floor from the existing exterior apron, through the outer arm, to the new exterior.
# The corridor has true sidewalls, ceiling, and a 20 x 20 m nominal structural frame; the audit measures trim-adjusted free clearance.
box('Outer_Entrance_Under_Apron_Join_Floor',444,460,-12,12,-2,-.01,ENTRY,floor,entry)
box('Outer_Entrance_Continuous_Walkable_Floor',460,882,-12,12,-2,0,ENTRY,floor,entry)
for sign in [-1,1]:
    t0,t1=(10,12) if sign>0 else (-12,-10)
    box(f'Outer_Entrance_Hall_Wall_{sign:+d}',444,866,t0,t1,0,20,ENTRY,interior,entry)
    t=sign*9.95;box(f'Outer_Entrance_Recessed_Guide_{sign:+d}',456,865,t-.025,t+.025,1.65,1.85,ENTRY,glow,entry)
    for j,r in enumerate(range(470,856,24),1):
        tt=sign*10.12;box(f'Outer_Entrance_Internal_Rib_{sign:+d}_{j:02d}',r,r+1.2,tt-.12,tt+.12,0,20,ENTRY,facet,entry)
# Roof is a separate removable collection. The outer arm remains structurally above Z24.
roof_outline=[(440,-12),(866,-12),(872,-8),(872,8),(866,12),(440,12)]
loft('Outer_Entrance_Removable_Hall_Ceiling',[(20,roof_outline),(24,roof_outline),(28,[(r,t*.76) for r,t in roof_outline])],ENTRY,[shell,facet],roof)
for j,r in enumerate(range(470,855,32),1):box(f'Outer_Entrance_Ceiling_Inset_{j:02d}',r,r+17,-.18,.18,19.99,20.01,ENTRY,glow,roof)
# Large exterior portal reinforces that the passage actually penetrates an outer arm.
for sign in [-1,1]:
    t0,t1=(10,20) if sign>0 else (-20,-10)
    box(f'Outer_Entrance_External_Portal_Jamb_{sign:+d}',850,868,t0,t1,0,30,ENTRY,armor,entry)
    box(f'Outer_Entrance_External_Portal_Reveal_{sign:+d}',868.1,868.7,sign*10-.2,sign*10+.2,1,18.5,ENTRY,glow,entry)
box('Outer_Entrance_External_Portal_Header',850,868,-10,10,20,30,ENTRY,armor,roof)
# Named export hierarchy. Old architecture and materials remain unchanged.
new_collections=[arms,feet,ties,entry,roof,crown]
for c in new_collections:
    group=bpy.data.objects.new(c.name,None);s.collection.objects.link(group);group.parent=root
    for o in c.objects:o.parent=group
export_cols=[c for c in bpy.data.collections if c.name.startswith(('00_','10_','11_','12_','20_','21_','22_','23_','24_','25_','31_','32_','33_','34_','35_','36_'))]
obs=list(dict.fromkeys(o for c in export_cols for o in c.objects if o.type=='MESH'))
root['inner_arm_count']=8;root['outer_arm_count']=8;root['total_arm_count']=16;root['height_m']=4000.;root['outer_arm_stagger_degrees']=22.5
s['height_m']=4000.;s['inner_spire_height_m']=3600.;s['outer_arm_count']=8;s['total_arm_count']=16;s['outer_foot_radius_m']=730.;s['outer_entrance_angle_degrees']=ENTRY;s['beam_clear_radius_m']=150.
s['asset_title']='MAIN SPIRE | 4 km staggered outer-arm revision';s['export_note']='One assembled GLB with 8 original inner arms and 8 new outer arms; presentation excluded.'
s['revision_source_sha256']=source_hash
# Presentation cameras are extended for the taller and wider assembly; no geometry enters GLB.
rig=bpy.data.collections['92_CAMERAS_LIGHTS_DO_NOT_EXPORT']
def camera(name,p,t,ortho=None,lens=45):
    d=bpy.data.cameras.new(name);o=bpy.data.objects.new(name,d);rig.objects.link(o);o.location=p;o.rotation_euler=(Vector(t)-o.location).to_track_quat('-Z','Y').to_euler();d.clip_start=.1;d.clip_end=30000;d.lens=lens
    if ortho:d.type='ORTHO';d.ortho_scale=ortho
    return o
hero=camera('Camera_Outer_Ring_Hero',(6200,-9900,4400),(0,0,1980),4690)
# Cutaway looks down the entrance axis, with the old facility clearly at the inner end.
entrance_cam=camera('Camera_Outer_Entrance_Cutaway',world(1000,-350,950,ENTRY),world(590,0,8,ENTRY),None,48)
camera('Camera_Outer_Entrance_Exterior',world(1020,-75,44,ENTRY),world(790,0,20,ENTRY),None,36)
# Presentation-only interior illumination keeps the actual passage legible in close views.
for i,r in enumerate(range(480,850,40),1):
    ld=bpy.data.lights.new(f'Outer_Entrance_Preview_Light_{i:02d}','AREA');ld.energy=2600;ld.shape='DISK';ld.size=12;ld.color=(.6,.85,.86);lo=bpy.data.objects.new(ld.name,ld);rig.objects.link(lo);lo.location=world(r,0,19.3,ENTRY)
s.camera=hero;s.render.engine='CYCLES';s.cycles.use_denoising=False;s.cycles.samples=48;s.cycles.use_adaptive_sampling=True;s.cycles.adaptive_threshold=.055;s.render.resolution_x=1440;s.render.resolution_y=1920;s.render.resolution_percentage=100
for a in bpy.context.screen.areas:
    if a.type=='VIEW_3D':a.spaces.active.clip_end=30000;a.spaces.active.region_3d.view_distance=6000;a.spaces.active.region_3d.view_location=(0,0,1950);a.spaces.active.region_3d.view_perspective='CAMERA'
# Keep a reproducible route centerline. The old route file remains separately unchanged.
source_routes=os.path.join(os.path.dirname(SOURCE),'validation','route_samples.json')
routes=json.load(open(source_routes))
new_route={'name':'outer_arm_entrance_to_existing_facility','points':[list(world(r,0,0,ENTRY)) for r in range(880,443,-2)]}
new_route['points'] += [list(world(r,0,0,ENTRY)) for r in range(444,311,-2)]
routes.append(new_route);json.dump(routes,open(os.path.join(ROOT,'validation','route_samples.json'),'w'),indent=2)
manifest={'source':SOURCE,'source_sha256':source_hash,'preserved_mesh_names':source_names,'height_m':4000,'inner_arms':8,'outer_arms':8,'outer_angles_deg':ANGLES,'outer_foot_radius_m':730,'stagger_deg':22.5,'beam_radius_m':150,'stage_heights_m':source_stages,'outer_arm_stations':ST,'contact_sets':contact_sets,'crown_ring':cap.name,'entrance':{'outer_arm_number':7,'angle_deg':ENTRY,'nominal_frame_width_m':20,'nominal_frame_height_m':20,'trim_adjusted_min_width_m':19.6,'trim_adjusted_min_height_m':19.99,'floor_z_m':0,'floor_radial_span_m':[444,882],'original_facility_entry_retained':True},'source_unchanged':True,'mesh_count':len(obs),'new_mesh_count':len(obs)-len(source_names)}
json.dump(manifest,open(os.path.join(ROOT,'validation','revision_manifest.json'),'w'),indent=2)
# Preserve the source embedded authoring text and add this incremental revision builder.
bpy.data.texts.load(__file__)
bpy.ops.object.select_all(action='DESELECT')
for o in obs+[root]+list(root.children):o.hide_set(False);o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.gltf(filepath=os.path.join(ROOT,'Main_Spire_Outer_Ring_Godot.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True)
bpy.ops.object.select_all(action='DESELECT');bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Main_Spire_Outer_Ring.blend'))
assert hashlib.sha256(open(SOURCE,'rb').read()).hexdigest()==source_hash
print('OUTER_RING_BUILT',json.dumps({k:manifest[k] for k in ['height_m','inner_arms','outer_arms','outer_foot_radius_m','mesh_count','new_mesh_count']}),flush=True)
