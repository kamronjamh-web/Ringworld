"""Alien-megastructure revision. Original dimensional brief retained; no industrial trusses or cage cylinder.
Run against the supplied source .blend. This regenerates the authoritative architecture while retaining the reference cavern and preview rig.
"""
import bpy, bmesh, math, random, json, os
from mathutils import Vector
from mathutils import noise
from math import sin, cos, pi
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Ringworld_Tripod_Reactor_Blockout.blend'))
C={c.name[:2]:c for c in bpy.data.collections if c.name[:2].isdigit()}
# Keep the explicit 1.8 m explorer; remove the first version's human industrial language.
fig=[o for o in C['06'].objects if o.name.startswith('Scale figure')]
for k in ['01','02','03','04','05','06','07']:
    for o in list(C[k].objects):
        if o not in fig:bpy.data.objects.remove(o,do_unlink=True)
CUR=C['01']
exec(open(os.path.join(ROOT,'geometry_helpers.py')).read())
# A dark, coherent alien material vocabulary; broad shapes carry the design.
metal.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.025,.041,.052,1)
metal.diffuse_color=(.025,.041,.052,1);metal.name='ALIEN | dark graphite ceramic-alloy'
metal.node_tree.nodes.get('Principled BSDF').inputs['Metallic'].default_value=.52
metal.node_tree.nodes.get('Principled BSDF').inputs['Roughness'].default_value=.40
edge.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.125,.18,.20,1);edge.diffuse_color=(.125,.18,.20,1);edge.name='ALIEN | subtle satin inlay'
concrete.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.13,.15,.145,1);concrete.diffuse_color=(.13,.15,.145,1);concrete.name='ALIEN | monolithic pale ceramic'
teal.node_tree.nodes.get('Principled BSDF').inputs['Emission Strength'].default_value=2.1;teal.name='ALIEN | recessed teal energy trace'
white.node_tree.nodes.get('Principled BSDF').inputs['Emission Strength'].default_value=2.4;white.name='ALIEN | containment seam white'

def profile_body(name,profile,n=8,ma=metal,phase=0):
    v=[];f=[]
    for r,z in profile:
        for k in range(n):
            a=phase+k*2*pi/n;v.append((r*cos(a),r*sin(a),z))
    for j in range(len(profile)-1):
        for k in range(n):f.append((j*n+k,j*n+(k+1)%n,(j+1)*n+(k+1)%n,(j+1)*n+k))
    f.append(tuple(range(n-1,-1,-1)));f.append(tuple((len(profile)-1)*n+k for k in range(n)))
    return mesh(name,v,f,ma)

def tapered_petal(name,a,sections,ma=metal):
    # section = outer radius, inner radius, angular halfwidth, height
    v=[];f=[]
    for ro,ri,w,z in sections:
        for r,aa in [(ro,a-w),(ro,a+w),(ri,a-w),(ri,a+w)]:v.append((r*cos(aa),r*sin(aa),z))
    for j in range(len(sections)-1):
        k=j*4;q=k+4;f.extend([(k,q,q+1,k+1),(k+2,k+3,q+3,q+2),(k,k+2,q+2,q),(k+1,q+1,q+3,k+3)])
    f.extend([(0,1,3,2),(len(v)-4,len(v)-2,len(v)-1,len(v)-3)])
    return mesh(name,v,f,ma)

def blade(name,a,sections,ma=metal):
    # Six-sided cross section produces uninterrupted sweeping bevel planes.
    v=[];f=[]
    for r,z,w,h in sections:
        for t,dz in [(-w*.75,h/2), (w*.75,h/2),(w,h*.22),(w*.65,-h/2),(-w*.65,-h/2),(-w,h*.22)]:v.append(radial(a,r,z+dz,t))
    for j in range(len(sections)-1):
        for k in range(6):f.append((j*6+k,j*6+(k+1)%6,(j+1)*6+(k+1)%6,(j+1)*6+k))
    f.extend([tuple(range(5,-1,-1)),tuple((len(sections)-1)*6+k for k in range(6))])
    return mesh(name,v,f,ma)

# The heart is suspended inside separated containment petals, with deliberately large voids.
CUR=C['01']
profile_body('Heart | suspended faceted spindle',[(2,-298),(38,-227),(73,-62),(68,88),(36,211),(2,299)],n=9,ma=metal,phase=pi/6)
for k in range(6):
    a=2*pi*k/6+pi/6
    sections=[(76,60,.025,-251),(158,122,.18,-161),(198,144,.235,-40),(184,130,.22,129),(135,108,.13,235),(79,66,.028,285)]
    tapered_petal(f'Containment | levitating petal {k+1}',a,sections,metal)
    # Thin interrupted inset line on one broad face, not a luminous outline.
    tapered_petal(f'Containment | energy inlay {k+1}',a+.13,[(160,158,.009,-146),(199,196,.009,-40),(185,182,.009,121)],teal)
    tapered_petal(f'Containment | ceramic ridge {k+1}',a-.14,[(157,153,.022,-137),(200,196,.022,-40),(185,181,.022,111)],edge)
    ring(f'Containment | lower separated arc {k+1}',188,166,-177,17,metal,n=8,start=a-.22,end=a+.22)
    ring(f'Containment | mid floating arc {k+1}',244,216,51,24,metal,n=8,start=a-.25,end=a+.25)
    ring(f'Containment | mid arc inner seam {k+1}',218,216,52,2.4,white,n=8,start=a-.21,end=a+.21)
    ring(f'Containment | upper separated arc {k+1}',174,147,219,14,edge,n=8,start=a-.20,end=a+.20)
# Central field slivers are visibly separated from the solid spindle.
for k in range(3):
    a=2*pi*k/3
    tapered_petal(f'Heart | field slit {k+1}',a,[(44,42,.021,-208),(83,80,.021,-38),(76,73,.021,95),(40,38,.021,196)],white)
ring('Crown | aperture / clear 150m diameter',112,75,294,12,metal,n=60)
ring('Crown | recessed aperture seam',77,75,296,2,teal,n=60)

CUR=C['02'];angles=[pi/2,7*pi/6,11*pi/6]
for k,a in enumerate(angles):
    name=f'B{k+1}'
    sec=[(188,49,19,44),(340,78,29,68),(610,165,43,105),(930,283,63,136),(1260,305,93,183)]
    blade(name+' | sweeping suspension monolith',a,sec,metal)
    blade(name+' | recessed upper channel',a,[(r,z+h/2+.5,w*.20,2) for r,z,w,h in sec],black)
    # A short recessed energy segment, not a stripe across the entire kilometer.
    blade(name+' | embedded field channel',a,[(400,122,3,1),(615,220,3,1),(782,281,3,1)],teal)
    # Forked stone-like root leaves a single designed aperture in the outer structure.
    blade(name+' | lower separated root',a,[(640,60,21,26),(940,110,38,53),(1260,228,73,72)],edge)

# Anchors are asymmetrical faceted stelae, not elevator cages or conventional piers.
CUR=C['05']
for k,a in enumerate(angles):
    o=profile_body(f'Anchor {k+1} | embedded alien stele',[(35,-360),(108,-160),(105,194),(60,413),(9,530)],n=5,ma=metal,phase=pi/2)
    o.location=radial(a,1245,0);o.rotation_euler.z=a
    # Thin vertical recess links all three terrace levels and reserves a future traversal/lift path.
    blade(f'Anchor {k+1} | lift interface recess',a,[(1123,-190,7,5),(1123,342,7,5)],black) if False else None
    rbox(f'Anchor {k+1} | vertical transfer recess',a,1139,81,(3,13,512),black)
    rbox(f'Anchor {k+1} | transfer trace',a,1137,95,(.8,1.2,435),teal,t=4)
    for j,z in enumerate([-180,80,330]):
        blade(f'Anchor {k+1} | levitating landing {j+1}',a,[(1080,z,47,3),(1155,z,73,9),(1265,z,68,9)],concrete)
        rbox(f'Anchor {k+1} | threshold seam {j+1}',a,1135,z+6,(2,30,.8),white)

# Vast pared-back terraces, smooth surfaces with no human railings or industrial bracing.
CUR=C['03']
for i,z in enumerate([-180,80,330]):
    for k,a in enumerate(angles):
        ring(f'L{i+1} | wall terrace {k+1}',1252,1170,z,8,concrete,n=24,start=a-.57,end=a+.57)
        ring(f'L{i+1} | wall terrace underside {k+1}',1247,1175,z-6,8,metal,n=24,start=a-.55,end=a+.55)
        for j in [-1,0,1]:
            aa=a+j*.33
            ring(f'L{i+1} | arrival glyph {k+1}-{j}',1183,1182,z+4.1,.15,teal,n=4,start=aa-.014,end=aa+.014)
for i,(z,ro,ri) in enumerate([(-140,216,197),(80,264,241),(225,201,181)]):
    for k in range(3):
        a=angles[k]
        ring(f'L{i+1} | floating core terrace {k+1}',ro,ri,z,4,concrete,n=12,start=a-.93,end=a+.93)
        ring(f'L{i+1} | terrace underside {k+1}',ro-1,ri+1,z-3,4,metal,n=12,start=a-.9,end=a+.9)

blade('ENTRY | core docking tongue',-pi/2,[(218,80,9,5),(273,80,9,5)],concrete)
ring('ENTRY | core vestibule gap closure',264,241,80,4,concrete,n=8,start=-pi/2-.17,end=-pi/2+.17)

# Continuous primary route, thin hovering ribbons and one visibly damaged secondary route.
CUR=C['04']
def ribbon(name,a,r1,r2,z,width=12,damaged=False):
    if not damaged:
        blade(name+' | continuous levitating causeway',a,[(r1,z,width/2,2.3),(r2,z,width/2,2.3)],concrete)
        blade(name+' | dark tapered underside',a,[(r1,z-3,width*.23,2),(r2,z-3,width*.23,2)],metal)
    else:
        for p,q in [(r1,609),(681,r2)]:blade(name+' | fractured route',a,[(p,z,width/2,2.3),(q,z,width/2,2.3)],concrete)
        o=blade(name+' | dislocated fragment',a,[(628,z-22,5,2),(659,z-41,5,2)],metal)
    for j in range(7):
        r=r1+(r2-r1)*(j+.5)/7;rbox(name+f' | inset wayfinding mark {j}',a,r,z+1.22,(1.6,1.2,.05),teal)
ribbon('ENTRY',-pi/2,254,1410,80,12)
ribbon('B2 lower damaged',angles[1]+.14,210,1170,-140,9,True)
ribbon('B1 upper',angles[0]-.12,193,1170,225,9)

CUR=C['06']
# An alien landing tongue embedded in exposed rock. Only the explorer supplies a human reference.
blade('Arrival | faceted landing tongue',-pi/2,[(1333,77,19,5),(1385,76,41,11),(1430,69,48,20)],concrete)
for x in [-28,28]:
    cube('Arrival | recessed guide',(x,-1386,81.55),(.25,40,.08),black)
    cube('Arrival | short luminous marker',(x,-1370,81.61),(.15,3,.06),teal)
# Position the unchanged 1.8m explorer on the new deck.
figure_delta=80.93-(bpy.data.objects['Scale figure | boot left'].location.z-.09)
for o in fig:o.location.z+=figure_delta

# The 150m throat is carried through split, sculptural monoliths with levitating collars.
CUR=C['07']
for k in range(3):
    a=2*pi*k/3+pi/6
    tapered_petal(f'Exhaust | split monolith {k+1}',a,[(96,78,.15,302),(148,81,.18,498),(127,81,.15,814),(86,76,.08,1025)],metal)
    tapered_petal(f'Exhaust | recessed inner channel {k+1}',a+.09,[(81,79,.010,360),(83,81,.010,500),(83,81,.010,940)],teal)
for j,z in enumerate([421,678,995]):
    for k in range(3):
        a=2*pi*k/3+pi/6
        ring(f'Exhaust | aligned floating collar {j}-{k}',132-j*7,75,z,12,edge,n=14,start=a-.73,end=a+.73)
ring('Exhaust | roof interface / 150m opening',115,75,1030,18,metal,n=60)

# Preview settings: no geometry edits to the concept-only cave.
sc=bpy.context.scene
sc['BLOCKOUT_STATUS']='Alien megastructure revision. Original geometry, static editable blockout. No production collision/LOD/gameplay claim.'
sc['DESIGN']='Three sweeping monolithic supports; faceted suspended spindle and disconnected containment petals; split exhaust monoliths. No human industrial trusses or cage shaft.'
sc.cycles.use_denoising=False;sc.cycles.samples=48;sc.render.resolution_percentage=70;sc.cycles.max_bounces=4;sc.cycles.volume_bounces=0
# Keep camera inside clear rock mouth for an unobstructed entrance reveal.
hero=bpy.data.objects['CAM_01 | entrance reveal'];hero.location=(0,-1425,126);hero.rotation_euler=(Vector((0,60,80))-hero.location).to_track_quat('-Z','Y').to_euler();hero.data.lens=24
sc.camera=hero
for o in bpy.data.objects:
    if o.name.startswith('Entrance / raw rock jamb'):o.location.x=270 if o.location.x>0 else -270
# Reset explicit lighting strengths so regeneration is repeatable.
energies={'Key | cool chamber bounce':10000000,'Rim | warm geological backlight':17600000,'Fill | entrance softbox':4000000,'Crown | exhaust spill':560000,'Void | deep subtle teal':1760000,'Arrival | cave-mouth warm wash':168000,'Arrival | path light':76000}
for o in bpy.data.objects:
    if o.type=='LIGHT':
        if o.name in energies:o.data.energy=energies[o.name]
        elif o.name.startswith('Anchor '):o.data.energy=2800000
sc.world.node_tree.nodes.get('Background').inputs[0].default_value=(.055,.085,.105,1);sc.world.node_tree.nodes.get('Background').inputs[1].default_value=.18
sc.view_settings.exposure=.4
for t in list(bpy.data.texts):
    if t.name.endswith('.py'):bpy.data.texts.remove(t)
bpy.data.texts.load(__file__)
readme=bpy.data.texts.get('README / START HERE');readme.clear();readme.write('RINGWORLD / ALIEN TRIPOD REACTOR\n\nAuthoritative alien-megastructure revision.\nThree sweeping monolithic supports, a suspended faceted core inside separated containment petals, and a sculptural split exhaust throat.\n\n1 unit = 1 meter. Chamber center = (0,0,0). Intended chamber envelope = 3000m x 2000m.\nCore approx 600m high. Exhaust clear diameter = 150m.\nCollections 01–07 are manufactured architecture. 80 is concept-only SDF reference geology; 90 is Blender preview-only lights and haze.\n\nNo game terrain, collision, LODs, traversal implementation or surface POI is included.\nSee IMPORT_NOTES.md for dimensions and import notes.\n')
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Ringworld_Tripod_Reactor_Blockout.blend'))
bpy.ops.object.select_all(action='DESELECT');obs=[]
for k in ['01','02','03','04','05','06','07']:
    for o in C[k].objects:
        if o.type=='MESH':o.select_set(True);obs.append(o)
bpy.ops.export_scene.gltf(filepath=os.path.join(ROOT,'Reactor_Architecture_Godot.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True,export_materials='EXPORT')
with open(os.path.join(ROOT,'build_stats.json'),'w') as f:json.dump({'revision':'alien_megastructure','blender_version':bpy.app.version_string,'architecture_objects':len(obs),'base_vertices':sum(len(o.data.vertices) for o in obs),'base_faces':sum(len(o.data.polygons) for o in obs),'scene_objects':len(bpy.data.objects),'units':'meters','origin':'chamber center'},f,indent=2)
print('ALIEN_EXPORTS_READY',flush=True)
# First preview is a fast structural review without distracting rock/volumetric noise.
bpy.data.collections['80_CAVE_SDF_REFERENCE_ONLY'].hide_render=True;bpy.data.objects['Preview atmosphere | non-export'].hide_render=True
sc.camera=bpy.data.objects['CAM_02 | layout cutaway'];sc.world.node_tree.nodes.get('Background').inputs[0].default_value=(.065,.085,.10,1);sc.world.node_tree.nodes.get('Background').inputs[1].default_value=.48
sc.cycles.samples=64;sc.render.resolution_percentage=75;sc.render.filepath=os.path.join(ROOT,'preview_02_architecture_cutaway.png');bpy.ops.render.render(write_still=True)
print('ALIEN_CUTAWAY_READY',flush=True)
