import bpy, math, os, json, random, sys
from mathutils import Vector
ROOT=os.path.dirname(os.path.abspath(__file__))
HEIGHT=1800.0
CLEAR_RADIUS=75.0
FOOT_RADIUS=250.0
ARM_COUNT=8
STATIONS=[
# z, radius, radial half-depth, tangent half-width
(0,250,48,34),(38,249,46,33),(125,236,40,30),(270,211,34,27),
(450,187,29,24),(660,165,25,21),(900,145,21,18),(1140,126,18,16),
(1360,113,16,14),(1540,103,14,13),(1640,100,14,14),(1700,96,16,15),
(1750,88,12,11),(1780,80,4,6),(1800,76.5,1,.6)]
# reset
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
for c in list(bpy.data.collections):
    if c.name!='Collection': bpy.data.collections.remove(c)
scene=bpy.context.scene
scene.unit_settings.system='METRIC'; scene.unit_settings.length_unit='METERS'; scene.unit_settings.scale_length=1.0
base=bpy.data.collections.get('Collection'); base.name='00_SURFACE_SPIRE_MACHINE_EXPORT'
preview=bpy.data.collections.new('90_PRESENTATION_ONLY_DO_NOT_EXPORT'); scene.collection.children.link(preview)
beamcoll=bpy.data.collections.new('91_BEAM_PREVIEW_ONLY_DO_NOT_EXPORT'); scene.collection.children.link(beamcoll)
cameras=bpy.data.collections.new('92_CAMERAS_LIGHTS'); scene.collection.children.link(cameras)

def mat(name,c,metal=0,rough=.5,emission=None):
    m=bpy.data.materials.new(name); m.diffuse_color=(*c,1); m.use_nodes=True
    p=m.node_tree.nodes.get('Principled BSDF'); p.inputs['Base Color'].default_value=(*c,1); p.inputs['Metallic'].default_value=metal; p.inputs['Roughness'].default_value=rough
    if emission:
        p.inputs['Emission Color'].default_value=(*emission[0],1); p.inputs['Emission Strength'].default_value=emission[1]
    return m
shell=mat('Obsidian_Alloy',(.025,.041,.052),.52,.40)
facet=mat('Raised_Facet_Alloy',(.061,.086,.098),.52,.40)
edge=mat('Worn_Silver_Edges',(.125,.18,.20),.6,.37)
black=mat('Deep_Recess',(.006,.014,.019),.45,.45)
glow=mat('Reactor_Teal_Inlay',(.04,.45,.44),.15,.3,((.04,.85,.78),2.1))
white=mat('Reactor_White_Core',(.44,.8,.79),.1,.2,((.55,1,.95),2.4))
soil=mat('Desaturated_Regolith',(.11,.105,.09),.03,.88)
clay=mat('Neutral_Silhouette',(.42,.48,.52),.22,.48)
refmat=mat('Scale_Reference_Amber',(.9,.37,.08),.1,.6,((1,.2,.02),.3))
beamMat=mat('Beam_Preview_Emission',(.04,.5,.47),0,.4,((.05,.8,.73),1.8))

def mesh(name,verts,faces,mats,idx=None,coll=base):
    me=bpy.data.meshes.new(name+'_Mesh'); me.from_pydata(verts,[],faces); me.materials.clear()
    for m in mats: me.materials.append(m)
    me.update(); o=bpy.data.objects.new(name,me); coll.objects.link(o)
    if idx:
        for p,i in zip(me.polygons,idx): p.material_index=i
    return o

def section(d,w):
    return [(-d,-.55*w),(-.62*d,-w),(.3*d,-w),(.88*d,-.5*w),(d,-.15*w),(d-1.6,-.08*w),(d-1.6,.08*w),(d,.15*w),(.88*d,.5*w),(.3*d,w),(-.62*d,w),(-d,.55*w)]

def vworld(r,t,z,a): return (r*math.cos(a)-t*math.sin(a),r*math.sin(a)+t*math.cos(a),z)

def interp(z):
    for s0,s1 in zip(STATIONS[:-1],STATIONS[1:]):
        if s0[0]<=z<=s1[0]:
            f=(z-s0[0])/(s1[0]-s0[0]); return tuple(s0[i]+f*(s1[i]-s0[i]) for i in range(1,4))
    return STATIONS[-1][1:]

def loft_arm(index):
    a=index*math.tau/8
    verts=[vworld(r+x,y,z,a) for z,r,d,w in STATIONS for x,y in section(d,w)]
    N=12; faces=[]; ids=[]
    faces.append(tuple(reversed(range(N)))); ids.append(0)
    for j in range(len(STATIONS)-1):
        for k in range(N):
            faces.append((j*N+k,j*N+(k+1)%N,(j+1)*N+(k+1)%N,(j+1)*N+k))
            ids.append(3 if k==5 else (1 if k in [1,2,8,9] else (2 if k in [4,6] else 0)))
    faces.append(tuple((len(STATIONS)-1)*N+k for k in range(N))); ids.append(0)
    o=mesh(f'Arm_{index+1:02d}_Faceted_Monolith',verts,faces,[shell,facet,edge,black],ids)
    o['arm_number']=index+1; o['foot_center_radius_m']=FOOT_RADIUS; o['azimuth_degrees']=index*45
    o['role']='Primary tapered alien arm; clear beam envelope preserved'
    # Thin energy inlay and proud outer-facet seams. Separate pieces remain editable.
    strip_segments=[(42,113),(280,434),(672,884),(1152,1344),(1552,1664),(1692,1758)]
    sv=[]; sf=[]
    for z0,z1 in strip_segments:
        rows=[]
        zs=sorted(set([z0,z1]+[s[0] for s in STATIONS if z0<s[0]<z1]))
        for z in zs:
            r,d,w=interp(z); wid=max(.65,w*.055)
            rows.append(len(sv)); sv.extend([vworld(r+d-1.53,-wid,z,a),vworld(r+d-1.53,wid,z,a)])
        for q in range(len(rows)-1): sf.append((rows[q],rows[q]+1,rows[q+1]+1,rows[q+1]))
    mesh(f'Arm_{index+1:02d}_Recessed_Teal_Channels',sv,sf,[glow])
    # Large shallow faceted shields at the bottom, repeating language of cavern monoliths.
    for p,(z0,z1,shift) in enumerate([(34,305,-1),(350,685,1),(735,1110,-1),(1160,1460,1)]):
        vv=[]; ff=[]
        for z,f in [(z0,.22),(z0+(z1-z0)*.12,.85),(z1-(z1-z0)*.14,.72),(z1,.08)]:
            r,d,w=interp(z)
            for tang in [.32,.82]:
                tt=shift*tang*w*f
                rr=r+d*(.91 if tang==.32 else .56)+.30
                vv.append(vworld(rr,tt,z,a))
        for k in range(3): ff.append((2*k,2*k+1,2*k+3,2*k+2))
        mesh(f'Arm_{index+1:02d}_Inset_Facet_{p+1:02d}',vv,ff,[facet])
    return o

arms=[loft_arm(i) for i in range(8)]

def annulus(name,profile,segments=64,mats=None,coll=base):
    # profile ordered around closed radial/Z cross-section
    vv=[]; ff=[]; ids=[]
    for r,z in profile:
        vv.extend([(r*math.cos(j*math.tau/segments),r*math.sin(j*math.tau/segments),z) for j in range(segments)])
    for k in range(len(profile)):
        for j in range(segments):
            ff.append((k*segments+j,k*segments+(j+1)%segments,((k+1)%len(profile))*segments+(j+1)%segments,((k+1)%len(profile))*segments+j)); ids.append(k%(len(mats or [shell])))
    return mesh(name,vv,ff,mats or [shell],ids,coll)

# All polygon chords stay outside the reserved R75m cylinder.
ri=CLEAR_RADIUS/math.cos(math.pi/64)+.04
crown=annulus('Crown_Open_Annular_Focus',[(ri,1738),(87,1738),(89,1742),(85,1748),(ri,1748)],64,[black,shell,facet,edge,black])
crown['clear_beam_diameter_m']=150.0
annulus('Crown_Recessed_Teal_Halo',[(77,1747),(78,1747),(78,1747.8),(77,1747.8)],64,[glow])
# Ground throat: simple alien rim linked exactly to cavern's 150m clear throat.
throat=annulus('Ground_Throat_150m_Clear',[(ri,0),(88,0),(94,7),(91,14),(ri,14)],64,[black,shell,facet,edge,black])
annulus('Ground_Throat_Teal_Inset',[(88.5,13.4),(89,13.4),(89,13.9),(88.5,13.9)],64,[glow])
# Eight low broad anchoring shoes, no cross-braces and no central platform.
for i in range(8):
    a=i*math.tau/8; vv=[]
    outlines=[(0,[(194,-29),(213,-43),(279,-43),(308,-18),(308,18),(279,43),(213,43),(194,29)]),
              (10,[(200,-25),(217,-38),(277,-38),(299,-15),(299,15),(277,38),(217,38),(200,25)])]
    for z,ol in outlines: vv.extend(vworld(r,t,z,a) for r,t in ol)
    ff=[tuple(reversed(range(8))),tuple(range(8,16))]
    ff.extend((j,(j+1)%8,(j+1)%8+8,j+8) for j in range(8))
    mesh(f'Foot_{i+1:02d}_Ground_Anchor',vv,ff,[shell,facet,edge],[0,1]+[2 if j in [1,5] else 0 for j in range(8)])

# Reference ground annulus has a true open hole under the beam.
annulus('Presentation_Ground_Not_Exported',[(ri,-8),(4000,-8),(4000,-.08),(ri,-.08)],128,[soil],preview)
# Ground radial impact/approach stones, intentionally abstract, entirely presentation-only.
random.seed(14)
for j in range(75):
    a=random.random()*math.tau; r=random.uniform(350,1500); s=random.uniform(2,13); h=random.uniform(.4,3.5)
    verts=[(r*math.cos(a)+s*math.cos(k*math.tau/5),r*math.sin(a)+s*math.sin(k*math.tau/5),-1) for k in range(5)]
    verts += [(r*math.cos(a)+s*.62*math.cos(k*math.tau/5+.1),r*math.sin(a)+s*.62*math.sin(k*math.tau/5+.1),h) for k in range(5)]
    faces=[(5,6,7,8,9)]+[(k,(k+1)%5,(k+1)%5+5,k+5) for k in range(5)]
    mesh('Presentation_Rock_%03d'%j,verts,faces,[soil],coll=preview)
# Separate visible beam preview; disabled in neutral renders and absent in GLB.
annulus('Beam_Envelope_Preview',[(62,-45),(62,2200),(61.8,2200),(61.8,-45)],64,[beamMat],beamcoll)
beamcoll.hide_render=True; beamcoll.hide_viewport=True
# Human references: 1.8m tall at local ground. 2m cube nearby for exact easy verification.
def box(name,center,size,material,coll=preview):
    x,y,z=center; dx,dy,dz=[v/2 for v in size]
    vv=[(x+a*dx,y+b*dy,z+c*dz) for a,b,c in [(-1,-1,-1),(1,-1,-1),(1,1,-1),(-1,1,-1),(-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1)]]
    return mesh(name,vv,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],[material],coll=coll)
def human(x,y):
    box('Human_1p8m_Torso',(x,y,1.16),(.42,.23,.62),refmat)
    box('Human_1p8m_Head',(x,y,1.66),(.24,.23,.28),refmat)
    for sign in [-1,1]:
        box('Human_1p8m_Leg',(x+sign*.12,y,.43),(.16,.20,.86),refmat)
        box('Human_1p8m_Arm',(x+sign*.29,y,1.13),(.13,.17,.62),refmat)
human(316,-45); human(320,-46)
box('Scale_Cube_2m',(324,-42,1),(2,2,2),refmat)
# A 20m reference slab makes human scale local detail comprehensible.
box('Presentation_Scale_Platform',(320,-45,-.15),(22,12,.3),facet)

# Cameras and lights.
def camera(name,pos,target,ortho=None,lens=50):
    d=bpy.data.cameras.new(name); o=bpy.data.objects.new(name,d); cameras.objects.link(o); o.location=pos
    o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler(); d.lens=lens; d.clip_start=.1; d.clip_end=15000
    if ortho: d.type='ORTHO'; d.ortho_scale=ortho
    return o
hero=camera('Camera_Hero',(1050,-1780,280),(0,0,850),None,34)
neutral=camera('Camera_Neutral',(2750,-4400,1900),(0,0,900),2260)
top=camera('Camera_Top',(0,0,3200),(0,0,0),760)
basecam=camera('Camera_Base_Scale',(430,-220,65),(295,-15,23),None,38)
crowncam=camera('Camera_Crown_Opening',(380,-590,2250),(0,0,1730),560)

def area(name,pos,power,size,color,target):
    d=bpy.data.lights.new(name,'AREA'); d.energy=power; d.shape='DISK'; d.size=size; d.color=color
    o=bpy.data.objects.new(name,d); cameras.objects.link(o); o.location=pos; o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler()
area('Softbox_Key',(1300,-2200,2400),90000000,1900,(.72,.85,1),(0,0,900))
area('Softbox_Rim',(-1700,1200,2100),65000000,1500,(.19,.62,.7),(0,0,900))
area('Softbox_Warm_Fill',(-1600,-1000,900),30000000,1800,(1,.7,.43),(0,0,850))
sun_data=bpy.data.lights.new('Sun_Distant','SUN'); sun_data.energy=1.4; sun_data.angle=.18
sun=bpy.data.objects.new('Sun_Distant',sun_data); cameras.objects.link(sun); sun.rotation_euler=(.45,-.4,-.7)
scene.world.use_nodes=True; world=scene.world.node_tree.nodes.get('Background'); world.inputs[0].default_value=(.095,.12,.14,1); world.inputs[1].default_value=.6
scene.render.engine='CYCLES'; scene.cycles.samples=24; scene.cycles.use_denoising=False
scene.render.resolution_x=1400; scene.render.resolution_y=1800; scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'; scene.render.film_transparent=False
scene.view_settings.view_transform='AgX'; scene.view_settings.look='AgX - Medium High Contrast'; scene.view_settings.exposure=0
scene.render.image_settings.color_mode='RGB'
# Neutral preview is intentionally bright and emissive-free.
scene.camera=neutral
scene['asset_title']='SURFACE BEAM SPIRE | Eight-arm annular alien focus'
scene['height_m']=1800; scene['foot_center_radius_m']=250; scene['arm_count']=8; scene['beam_clear_radius_m']=75
scene['axis_note']='Blender Z-up; ground center at origin. glTF exports Y-up automatically.'
scene['export_note']='Only 00_SURFACE_SPIRE_MACHINE_EXPORT. Beam/ground/people/cameras are presentation only.'
# Export machine only before rendering. Native dimensions remain unscaled.
bpy.ops.object.select_all(action='DESELECT')
for o in base.objects: o.select_set(True)
bpy.context.view_layer.objects.active=arms[0]
bpy.ops.export_scene.gltf(filepath=os.path.join(ROOT,'Surface_Beam_Spire_Godot.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True)
# Store the editable scene with hero camera and beam disabled.
scene.camera=hero
for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D':
        area.spaces.active.clip_end=20000
        area.spaces.active.region_3d.view_distance=2400
        area.spaces.active.region_3d.view_location=(0,0,900)
        area.spaces.active.region_3d.view_perspective='CAMERA'
bpy.data.texts.load(__file__)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Surface_Beam_Spire.blend'))
report={'height_m':1800,'arms':8,'foot_center_radius_m':250,'foot_azimuth_degrees':[i*45 for i in range(8)],'reserved_vertical_beam_radius_m':75,'crown_inner_vertex_radius_m':ri,'machine_objects':len(base.objects),'machine_vertices':sum(len(o.data.vertices) for o in base.objects),'machine_polygons':sum(len(o.data.polygons) for o in base.objects),'exported_glb':'Surface_Beam_Spire_Godot.glb','beam_preview_exported':False}
with open(os.path.join(ROOT,'dimensions_and_checks.json'),'w') as f: json.dump(report,f,indent=2)
print('GEOMETRY_EXPORTED',json.dumps(report),flush=True)
# Render modes selectable via trailing args.
args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else ['neutral','hero','top','crown','base']
for mode in args:
    beamcoll.hide_render=True
    scene.render.resolution_x=1400; scene.render.resolution_y=1800
    scene.cycles.samples=24
    if mode=='neutral':
        scene.camera=neutral; scene.view_layers[0].material_override=clay; scene.render.resolution_x=1080; scene.render.resolution_y=1500; scene.cycles.samples=64
    elif mode=='top':
        scene.camera=top; scene.view_layers[0].material_override=None; scene.render.resolution_x=1400; scene.render.resolution_y=1400
    elif mode=='crown':
        scene.camera=crowncam; scene.view_layers[0].material_override=None; scene.render.resolution_x=1500; scene.render.resolution_y=1300
    elif mode=='base':
        scene.camera=basecam; scene.view_layers[0].material_override=None; scene.render.resolution_x=1600; scene.render.resolution_y=1000; scene.cycles.samples=32
    else:
        scene.camera=hero; scene.view_layers[0].material_override=None; scene.cycles.samples=32
        if mode=='beam': beamcoll.hide_render=False
    scene.render.filepath=os.path.join(ROOT,mode+'.png'); bpy.ops.render.render(write_still=True)
    print('RENDER_DONE',mode,flush=True)
