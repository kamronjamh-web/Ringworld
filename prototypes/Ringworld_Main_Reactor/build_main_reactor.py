"""Build the Ringworld main reactor additions and refresh its assembled GLB.
Run: blender -b -t 8 -P build_main_reactor.py
Uses the supplied current .blend as editable baseline; original external assets are not required.
Optional first-build source: -- --source /path/to/original.blend
"""
import bpy,bmesh,math,os,sys,json
from mathutils import Vector,Matrix
from math import sin,cos,pi
ROOT=os.path.dirname(os.path.abspath(__file__));sys.path.insert(0,ROOT)
from mesh_helpers import *
args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
SOURCE=args[args.index('--source')+1] if '--source' in args else os.path.join(ROOT,'Ringworld_Main_Reactor.blend')
bpy.ops.wm.open_mainfile(filepath=SOURCE)
sc=bpy.context.scene
sc.unit_settings.system='METRIC';sc.unit_settings.scale_length=1
PREFIXES=['01','02','03','04','05','06','07']
def bounds(obs):
    bpy.context.view_layer.update()
    xyz=[o.matrix_world@Vector(v) for o in obs if o.type=='MESH' for v in o.bound_box]
    low=[min(p[i] for p in xyz) for i in range(3)];high=[max(p[i] for p in xyz) for i in range(3)]
    return {'min':low,'max':high,'dimensions':[high[i]-low[i] for i in range(3)]}
def coll(name):
    c=bpy.data.collections.get(name)
    if not c:c=bpy.data.collections.new(name);sc.collection.children.link(c)
    return c
base=[o for o in bpy.data.objects if o.type=='MESH' and any(c.name[:2] in PREFIXES for c in o.users_collection) and not o.name.startswith('Scale figure')]
fig=[o for o in bpy.data.objects if o.name.startswith('Scale figure')]
reference=coll('92_HUMAN_SCALE_REFERENCE_ONLY')
for o in fig:
    for c in list(o.users_collection):c.objects.unlink(o)
    reference.objects.link(o)
if not sc.get('MAIN_REACTOR_BASELINE_SCALED'):
    original=bounds(base);sc['ORIGINAL_ARCHITECTURE_BOUNDS_JSON']=json.dumps(original)
    # Baking actual geometry makes the delivered export a metre-scale asset at scale 1.
    for o in base:
        o.location*=2;o.scale*=2
    bpy.ops.object.select_all(action='DESELECT')
    for o in base:o.select_set(True)
    bpy.context.view_layer.objects.active=base[0]
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    for o in bpy.data.collections['80_CAVE_SDF_REFERENCE_ONLY'].objects:
        o.location*=2;o.scale*=2
    for o in bpy.data.collections['90_LIGHTS_AND_CAMERAS'].objects:
        o.location*=2
        if o.type=='LIGHT':
            o.data.energy*=4
            if hasattr(o.data,'size'):o.data.size*=2
            if hasattr(o.data,'shadow_soft_size'):o.data.shadow_soft_size*=2
        elif o.type=='CAMERA':o.data.clip_end=50000
        elif o.type=='MESH':o.scale*=2
    # Preserve a precisely 1.8 m person, placed on the doubled arrival platform.
    if fig:
        old=bounds(fig);floor=old['min'][2];height=old['dimensions'][2];pivot=Vector((5,-1370,floor));target=Vector((10,-2740,161.86))
        for o in fig:
            world=o.matrix_world.copy()
            for v in o.data.vertices:v.co=target+(world@v.co-pivot)*(1.8/height)
            o.matrix_world=Matrix.Identity(4)
    sc['MAIN_REACTOR_BASELINE_SCALED']=2.0
# Ensure physical polygon clearance, including the chord of each aperture polygon.
# The old radius 75 ring doubled to 150 has a slightly smaller apothem.
for o in base:
    if o.name.startswith(('Crown | aperture','Crown | recessed aperture','Exhaust |')):
        for v in o.data.vertices:
            r=(v.co.x*v.co.x+v.co.y*v.co.y)**.5
            if 149.9<=r<=152.1:
                v.co.x*=152.5/r;v.co.y*=152.5/r
for o in base:
    if o.name == 'Crown | aperture / clear 150m diameter':o.name='Crown | aperture / clear 300m diameter'
    if o.name == 'Exhaust | roof interface / 150m opening':o.name='Exhaust | roof interface / 300m opening'
# Remove only generated main-reactor additions; retain the editable inherited architecture.
for c in list(bpy.data.collections):
    if c.name.startswith(('08_','09_','10_','11_','12_')):
        for o in list(c.objects):bpy.data.objects.remove(o,do_unlink=True)
        bpy.data.collections.remove(c)
for o in list(bpy.data.objects):
    if o.name.startswith(('MAIN_PREVIEW','MAIN_CAMERA')):bpy.data.objects.remove(o,do_unlink=True)
M=lambda prefix:next(m for m in bpy.data.materials if m.name.startswith(prefix))
metal=M('ALIEN | dark graphite');edge=M('ALIEN | subtle satin');ceramic=M('ALIEN | monolithic');teal=M('ALIEN | recessed teal');white=M('ALIEN | containment seam');black=M('M04 | Recess / graphite')
# Preserve the original palette, with readable ceramic faces and restrained emission.
for m,color,rough in [(metal,(.028,.043,.052,1),.42),(edge,(.13,.19,.21,1),.38),(ceramic,(.19,.21,.205,1),.68)]:
    m.diffuse_color=color;p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=color;p.inputs['Roughness'].default_value=rough
for m,strength in [(teal,2.1),(white,2.4)]:m.node_tree.nodes.get('Principled BSDF').inputs['Emission Strength'].default_value=strength
C=coll('08_SECONDARY_CONTAINMENT_HALOS')
# Three entirely new, widely separated containment belts. Arcs leave broad radial gaps.
for level,(r,z,w,h,phase) in enumerate([(680,-455,62,36,0),(645,365,54,32,pi/12),(545,745,48,30,0)]):
    for k in range(6):
        a=phase+k*pi/3
        ring(f'HALO {level+1}.{k+1} | outer separated armor',r,r-w,z,h,metal,C,n=12,start=a-.39,end=a+.39)
        ring(f'HALO {level+1}.{k+1} | ceramic crest',r-8,r-w+8,z+h/2+3,6,edge,C,n=12,start=a-.35,end=a+.35)
        ring(f'HALO {level+1}.{k+1} | recessed inner energy channel',r-w+3,r-w+1,z+2,5,teal,C,n=10,start=a-.29,end=a+.20)
        # Inset rectangular notches, real shadow gaps between small plates.
        for j in [-1,0,1]:
            aa=a+j*.21
            ring(f'HALO {level+1}.{k+1} | articulated cap {j+2}',r+5,r-4,z+3,14,ceramic,C,n=2,start=aa-.032,end=aa+.032)
    # A separate underbelly/overhead radial field fork visibly connects this new tier.
    if level!=1:
        for k in range(3):
            a=pi/2+k*2*pi/3
            blade(f'HALO {level+1} | radial coupling fork {k+1}',a,[(305,z+30,13,20),(430,z-16,19,25),(r-w,z,24,20)],edge,C)
C=coll('09_NESTED_CORE_FACETS')
# Nested, offset shell segments create a dense inner machine without filling the core's voids.
for k in range(9):
    a=k*2*pi/9+pi/18
    petal(f'CORE L2 | nested faceted sheath {k+1}',a,[(124,114,.022,-505),(185,167,.10,-345),(214,189,.125,-70),(202,180,.115,195),(156,139,.07,400),(121,110,.02,510)],edge,C)
    petal(f'CORE L2 | separated channel liner {k+1}',a+.09,[(188,185,.008,-330),(217,214,.008,-70),(205,202,.008,190),(159,156,.008,385)],black,C)
    petal(f'CORE L2 | intermittent plasma slit {k+1}',a+.09,[(218,216,.004,-195),(218,216,.004,-70),(206,204,.004,135)],white,C)
for k in range(6):
    a=k*pi/3+pi/6
    # Ribbed dorsal plates attach to outer containment petals, leaving the original vertical slits visible.
    for j,(z,r) in enumerate([(-295,321),(-150,372),(15,391),(180,370),(345,311)]):
        petal(f'CORE L3 | dorsal panel {k+1}.{j+1}',a-.08,[(r+8,r,.095,z-42),(r+13,r+4,.105,z),(r+8,r,.095,z+42)],metal,C)
        petal(f'CORE L3 | inset seam {k+1}.{j+1}',a-.08,[(r+13.5,r+13,.065,z-2),(r+13.5,r+13,.065,z+2)],black,C)
C=coll('10_POWER_DISTRIBUTION_RIBS')
angles=[pi/2,7*pi/6,11*pi/6]
for k,a in enumerate(angles):
    # Paired power ribs under the original sweeping support create a forked, higher-order silhouette.
    for side in [-1,1]:
        aa=a+side*.075
        sec=[(590,-80,15,31),(980,-60,24,48),(1450,60,39,62),(1940,265,46,72),(2430,435,53,83)]
        blade(f'POWER B{k+1}.{side:+d} | suspended distribution rib',aa,sec,metal,C)
        blade(f'POWER B{k+1}.{side:+d} | recessed bus channel',aa,[(r,z+h/2+1,w*.20,3) for r,z,w,h in sec],black,C)
        blade(f'POWER B{k+1}.{side:+d} | live bus segment',aa,[(1000,-30,2,2),(1450,93,2.5,2),(1810,244,2.5,2)],teal,C)
    # Additional armor stacks on the broad support, framing an inset recess at a true lower height.
    for j,(r,z,w) in enumerate([(1290,465,68),(1570,560,82),(1840,670,97),(2130,735,120)]):
        for side in [-1,1]:
            blade(f'POWER B{k+1} | cheek plate {j+1}.{side:+d}',a+side*.035,[(r-72,z,13,15),(r+72,z+30,17,15)],edge,C)
        blade(f'POWER B{k+1} | recessed panel well {j+1}',a,[(r-64,z-2,13,3),(r+64,z+28,15,3)],black,C)
C=coll('11_SIX_REACTOR_POWER_MODULES')
# Six substantial 815 m-tall reactor pods, each with its own contained heart.
# These are visual power-generation modules, not a physically quantified capacity claim.
for k in range(6):
    a=k*pi/3
    before=set(C.objects)
    profile(f'POWER MODULE {k+1} | contained faceted heart',[(8,-500),(89,-388),(126,-165),(118,25),(67,180),(6,220)],metal,C,n=9,phase=pi/6)
    for j in range(6):
        aa=j*pi/3+pi/6
        petal(f'POWER MODULE {k+1} | heavy containment petal {j+1}',aa,[(104,82,.05,-505),(206,163,.25,-360),(240,186,.28,-125),(218,174,.26,80),(156,121,.15,209)],metal,C)
        petal(f'POWER MODULE {k+1} | inner core aperture {j+1}',aa+.28,[(112,107,.028,-330),(138,132,.036,-120),(128,122,.035,20),(83,78,.023,150)],white,C)
        petal(f'POWER MODULE {k+1} | recessed outer channel {j+1}',aa+.13,[(210,207,.011,-330),(243,240,.011,-125),(221,218,.011,75)],teal,C)
        petal(f'POWER MODULE {k+1} | armored dorsal ridge {j+1}',aa-.13,[(211,204,.026,-330),(249,239,.029,-125),(225,216,.026,75)],edge,C)
    for tier,(z,ro,ri) in enumerate([(-335,241,193),(-60,268,221),(165,219,160)]):
        for j in range(3):
            aa=j*2*pi/3
            ring(f'POWER MODULE {k+1} | split containment collar {tier+1}.{j+1}',ro,ri,z,38,metal,C,n=8,start=aa-.91,end=aa+.91)
            ring(f'POWER MODULE {k+1} | collar inset {tier+1}.{j+1}',ro-8,ro-13,z+20,3,edge,C,n=8,start=aa-.82,end=aa+.82)
    ring(f'POWER MODULE {k+1} | crown collector',186,100,225,30,edge,C,n=36)
    ring(f'POWER MODULE {k+1} | crown energy aperture',107,102,241,3,teal,C,n=36)
    profile(f'POWER MODULE {k+1} | lower flux keel',[(8,-575),(76,-465),(67,-352)],edge,C,n=6)
    # Architecture references on the side communicate that each pod is a separate plant.
    for side in [-1,1]:
        ring(f'POWER MODULE {k+1} | service balcony {side:+d}',293,252,-175,10,ceramic,C,n=10,start=(0 if side>0 else pi)-.58,end=(0 if side>0 else pi)+.58)
    # Transform the module as a complete authored assembly around the central reactor.
    for obj in set(C.objects)-before:
        obj.location=radial(a,1320);obj.rotation_euler.z=a
        obj['power_module']=k+1
    # Heavy, forked trunk merges each plant into the central collection manifold.
    blade(f'POWER TRUNK {k+1} | armored transfer trunk',a,[(550,-100,54,76),(790,-140,72,98),(1050,-185,93,118),(1145,-170,101,126)],metal,C)
    blade(f'POWER TRUNK {k+1} | recessed bus bed',a,[(575,-60,21,6),(790,-87,27,7),(1050,-122,32,8),(1120,-103,33,8)],black,C)
    blade(f'POWER TRUNK {k+1} | inset primary energy conduit',a,[(595,-56,3,3),(790,-82,4,3),(1050,-116,4,3),(1100,-105,4,3)],teal,C)
    for side in [-1,1]:
        blade(f'POWER TRUNK {k+1} | separated reinforcing fork {side:+d}',a+side*.055,[(585,-175,19,32),(805,-210,25,39),(1095,-225,32,46)],edge,C)
    # Distinct collector jaws show all six feeds converging on the central heart.
    petal(f'MASTER CORE | heavy collector jaw {k+1}',a,[(476,425,.12,-325),(515,454,.16,-155),(504,448,.15,80),(453,411,.11,240)],metal,C)
    petal(f'MASTER CORE | collector inlay {k+1}',a+.105,[(516,512,.012,-155),(505,501,.012,70)],teal,C)
    ring(f'MASTER CORE | collector manifold segment {k+1}',599,510,-130,94,metal,C,n=10,start=a-.40,end=a+.40)
    ring(f'MASTER CORE | manifold armored cap {k+1}',589,528,-79,8,edge,C,n=10,start=a-.36,end=a+.36)
ring('MASTER CORE | upper structural collector',492,424,283,52,metal,C,n=72)
ring('MASTER CORE | upper collector inset',481,474,310,3,edge,C,n=72)
# Share repeated module mesh datablocks while keeping six independently placed named assemblies.
module_templates={}
for obj in C.objects:
    if obj.name.startswith('POWER MODULE '):
        key=obj.name.split(' | ',1)[1]
        if key in module_templates:obj.data=module_templates[key]
        else:module_templates[key]=obj.data
        obj['module_geometry']='Shared sixfold prototype; make single-user before individual mesh edits.'
C=coll('12_EXHAUST_CROWN_ARTICULATION')
for tier,(z,ro) in enumerate([(1010,340),(1510,315),(1945,286)]):
    for k in range(3):
        a=pi/6+k*2*pi/3
        ring(f'EXHAUST {tier+1}.{k+1} | secondary flanged collar',ro,ro-38,z,25,metal,C,n=12,start=a-.61,end=a+.61)
        ring(f'EXHAUST {tier+1}.{k+1} | recessed collar seam',ro-35,ro-37,z+2,4,teal,C,n=12,start=a-.49,end=a+.38)
        petal(f'EXHAUST {tier+1}.{k+1} | floating vertical vane',a,[(ro-15,ro-26,.08,z-86),(ro+12,ro-5,.115,z),(ro-15,ro-26,.08,z+86)],edge,C)
# Explicit, inspectable registration marker. Empty only; never exported.
guides=coll('00_SCENE_GUIDES')
for o in list(guides.objects):bpy.data.objects.remove(o,do_unlink=True)
for name,loc,size in [('REGISTRATION | chamber center / align to spire XY',(0,0,0),180),('REGISTRATION | roof aperture center',(0,0,2060),150)]:
    o=bpy.data.objects.new(name,None);guides.objects.link(o);o.location=loc;o.empty_display_type='CIRCLE';o.empty_display_size=size
# Improve preview lighting while retaining the reference cave and original dramatic entrance view.
rig=bpy.data.collections['90_LIGHTS_AND_CAMERAS']
def area(name,location,target,power,size,color):
    d=bpy.data.lights.new(name,'AREA');d.energy=power;d.shape='DISK';d.size=size;d.color=color;o=bpy.data.objects.new(name,d);rig.objects.link(o);o.location=location;o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler()
area('MAIN_PREVIEW | broad cool key',(-1700,-2500,1850),(0,0,200),140000000,1800,(.55,.76,1))
area('MAIN_PREVIEW | soft neutral fill',(2100,-1700,1100),(0,0,150),95000000,1600,(.85,.92,1))
area('MAIN_PREVIEW | pale overhead rim',(0,1700,2250),(0,0,450),190000000,1800,(.73,.90,1))
area('MAIN_PREVIEW | core low fill',(-500,-1350,-650),(0,0,-50),22000000,650,(.31,.75,.74))
def camera(name,location,target,lens=35,ortho=None):
    d=bpy.data.cameras.new(name);d.lens=lens;d.clip_end=50000
    if ortho:d.type='ORTHO';d.ortho_scale=ortho
    o=bpy.data.objects.new(name,d);rig.objects.link(o);o.location=location;o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler();return o
cut=camera('MAIN_CAMERA | architecture cutaway',(4500,-6100,4300),(0,0,525),ortho=6600)
hero=camera('MAIN_CAMERA | central installation',(-290,-2780,370),(0,150,310),lens=22)
sc.render.engine='CYCLES';sc.cycles.device='CPU';sc.cycles.use_denoising=False;sc.cycles.samples=64;sc.cycles.max_bounces=4;sc.cycles.volume_bounces=0
sc.render.resolution_x=1800;sc.render.resolution_y=1200;sc.render.resolution_percentage=100
sc.view_settings.view_transform='AgX';sc.view_settings.exposure=.8
world=sc.world.node_tree.nodes.get('Background');world.inputs[0].default_value=(.052,.071,.087,1);world.inputs[1].default_value=.5
bpy.data.objects['Preview atmosphere | non-export'].hide_render=True
sc.camera=hero
sc['BLOCKOUT_STATUS']='Main-reactor authoring asset. 2x baseline architecture plus new multi-tier containment, core layers, power distribution, six reactor power modules and crown details. No collision, gameplay or LOD claim.'
sc['DESIGN']='Main installation: three sweeping buttresses, suspended nested heart, separated containment belts and six substantial reactor modules feeding a master collector. Graphite, ceramic, muted teal/white.'
sc['MAIN_DIMENSIONS']='Reference chamber 6000 m wide x 4000 m high; manufactured architecture 5007.997 x 5555.428 x 2798 m; exhaust clear diameter 300 m above Z=600 m; entry terrace Z=160 m.'
sc['PRIMARY_EXPORT']='Main_Reactor_Architecture.glb; 827 named architecture instances, 587 shared mesh resources, 44626 instantiated triangles. No terrain, reference explorer, cameras or preview lights.'
sc['CHAMBER_REFERENCE_METRES']='6000 wide x 4000 high; nominal roof/floor +/-2000; irregular cave reference only.'
sc['REGISTRATION']='Local chamber center at (0,0,0). Place around Godot (0,-4000,0), align local X/Z to main spire. No intervening shaft supplied.'
sc['CAPACITY_ART_DIRECTION']='Six substantial reactor power modules converge on one master core. Fictional visual capacity; no engineering output rating.'
sc['EXHAUST_CLEARANCE']='At least radius 150 metres above crown (Blender Z >= 600). Core itself occupies axis below crown.'
# Save source with a useful hero view; exports contain architecture only.
arch=[o for o in bpy.data.objects if o.type=='MESH' and any(c.name[:2] in PREFIXES+['08','09','10','11','12'] for c in o.users_collection)]
for o in arch:
    o['asset']='Ringworld Main Reactor';o['units']='metres'
    o['export_group']='Manufactured architecture'
for textblock in list(bpy.data.texts):bpy.data.texts.remove(textblock)
for filename in ['build_main_reactor.py','mesh_helpers.py']:
    bpy.data.texts.load(os.path.join(ROOT,filename))
readme=bpy.data.texts.new('README - MAIN REACTOR')
readme.write('RINGWORLD MAIN REACTOR\n\nOne assembled GLB, editable named objects.\nTrue 2x inherited architecture; reference explorer remains 1.8 m.\nNew collections 08-12 add secondary containment, nested core facets, power ribs, six reactor power modules and exhaust detailing.\n\nMetres; chamber-center origin. Approximate chamber reference 6000 m wide x 4000 m high.\nExhaust reserves 300 m diameter above crown Z=600 m.\nCave, preview rig and explorer are excluded from the GLB.\nNo collision, gameplay, LODs, surface POI or connecting shaft.\nSee IMPORT_NOTES.md and VALIDATION.md beside this file.\n')
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Ringworld_Main_Reactor.blend'))
bpy.ops.object.select_all(action='DESELECT')
for o in arch:o.select_set(True)
bpy.context.view_layer.objects.active=arch[0]
bpy.ops.export_scene.gltf(filepath=os.path.join(ROOT,'Main_Reactor_Architecture.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True,export_materials='EXPORT')
original=json.loads(sc['ORIGINAL_ARCHITECTURE_BOUNDS_JSON']);new=bounds(arch);scaled=bounds(base)
stats={'blender_version':bpy.app.version_string,'unit_metres':1,'origin':'local chamber center','baseline_bounds':original,'scaled_baseline_bounds':scaled,'new_architecture_bounds':new,'axis_size_ratios':[new['dimensions'][i]/original['dimensions'][i] for i in range(3)],'architecture_mesh_objects':len(arch),'unique_architecture_mesh_datablocks':len({o.data.as_pointer() for o in arch}),'power_module_count':6,'power_module_height_including_keel_metres':815,'power_module_max_containment_diameter_metres':536,'inherited_architecture_mesh_objects':len(base),'new_mesh_objects':len(arch)-len(base),'base_vertices':sum(len(o.data.vertices) for o in arch),'base_faces':sum(len(o.data.polygons) for o in arch),'reference_person_height':bounds(fig)['dimensions'][2],'main_causeway_width_metres':24,'secondary_causeway_width_metres':18,'beam_clearance_required_radius_metres':150,'beam_clearance_z_range_metres':[600,2078]}
with open(os.path.join(ROOT,'build_stats.json'),'w') as f:json.dump(stats,f,indent=2)
print('MAIN_REACTOR_EXPORT_READY',json.dumps(stats),flush=True)
if '--no-render' in args:sys.exit(0)
# First review render is the full assembled architecture with cave hidden.
sc.render.resolution_y=1400
bpy.data.collections['80_CAVE_SDF_REFERENCE_ONLY'].hide_render=True;reference.hide_render=True
sc.camera=cut;sc.cycles.samples=64;sc.render.filepath=os.path.join(ROOT,'preview_02_architecture_cutaway.png');bpy.ops.render.render(write_still=True)
print('MAIN_REACTOR_CUTAWAY_READY',flush=True)
if '--cutaway-only' in args:sys.exit(0)
# The hero retains the separate cave reference to communicate installation scale.
bpy.data.collections['80_CAVE_SDF_REFERENCE_ONLY'].hide_render=False;reference.hide_render=False
sc.camera=hero;sc.cycles.samples=96;sc.render.resolution_x=1800;sc.render.resolution_y=1200
sc.render.filepath=os.path.join(ROOT,'preview_01_entrance_reveal.png');bpy.ops.render.render(write_still=True)
print('MAIN_REACTOR_HERO_READY',flush=True)
