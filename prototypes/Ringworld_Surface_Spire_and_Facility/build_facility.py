"""Add an editable, hollow facility to the approved surface spire.
Run: /usr/bin/blender -b --python build_facility.py
The approved source .blend and source GLB are read-only inputs and are never saved over.
All dimensions are metres. The authoring scene is Z-up; glTF is exported Y-up.
"""
import bpy, bmesh, math, os, json, hashlib
from mathutils import Vector
ROOT = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(ROOT, 'Surface_Beam_Spire.blend')
SOURCE_GLB = os.path.join(ROOT, 'Surface_Beam_Spire_Godot.glb')
def file_hash(p):
    with open(p, 'rb') as f: return hashlib.sha256(f.read()).hexdigest()
source_hashes = {os.path.basename(p): file_hash(p) for p in [SOURCE, SOURCE_GLB]}
bpy.ops.wm.open_mainfile(filepath=SOURCE)
scene = bpy.context.scene
base = bpy.data.collections['00_SURFACE_SPIRE_MACHINE_EXPORT']
def new_collection(name):
    c = bpy.data.collections.new(name); scene.collection.children.link(c); return c
arch = new_collection('10_FACILITY_ARCHITECTURE_EXPORT')
roof = new_collection('11_FACILITY_REMOVABLE_ROOF_EXPORT')
fixtures = new_collection('12_FACILITY_INTERIOR_FIXTURES_EXPORT')
# The gallery is raised above the preserved 14 m shaft rim; the main level remains at Z0.
WING_ANGLES = [22.5, 112.5, 202.5]
ENTRY_ANGLE = 292.5
RAMP_A0, RAMP_A1 = 292.5, 360.0
LOW_ACCESS_ANGLE, HIGH_ACCESS_ANGLE = 289.5, 363.0
shell = bpy.data.materials['Obsidian_Alloy']
facet = bpy.data.materials['Raised_Facet_Alloy']
edge = bpy.data.materials['Worn_Silver_Edges']
black = bpy.data.materials['Deep_Recess']
glow = bpy.data.materials['Reactor_Teal_Inlay']
white = bpy.data.materials.get('Reactor_White_Core', glow)
def material(name, color, metal=.4, rough=.45, emission=None):
    m = bpy.data.materials.new(name); m.diffuse_color = (*color, 1); m.use_nodes = True
    p = m.node_tree.nodes.get('Principled BSDF'); p.inputs['Base Color'].default_value=(*color,1)
    p.inputs['Metallic'].default_value=metal; p.inputs['Roughness'].default_value=rough
    if emission:
        p.inputs['Emission Color'].default_value=(*emission[0],1); p.inputs['Emission Strength'].default_value=emission[1]
    return m
floor_mat=material('Facility_Floor_Basalt_Alloy',(.061,.075,.078),.32,.62)
inner_mat=material('Facility_Interior_Ceramic',(.15,.19,.195),.32,.48)
soft_glow=material('Facility_Soft_Teal_Guide',(.055,.30,.29),.12,.38,((.08,.65,.59),1.2))
screen_mat=material('Facility_Control_Surface',(.017,.085,.09),.22,.31,((.025,.28,.25),.45))

def world(r,t,z,ang):
    a=math.radians(ang); return (r*math.cos(a)-t*math.sin(a),r*math.sin(a)+t*math.cos(a),z)
def polar(r,a,z=0): return world(r,0,z,a)
def mesh(name, verts, faces, mats, ids=None, collection=arch, role='architecture'):
    me=bpy.data.meshes.new(name+'_Mesh'); me.from_pydata(verts,[],faces); me.update()
    for m in mats: me.materials.append(m)
    if ids is not None:
        for p, i in zip(me.polygons,ids): p.material_index=i
    # Explicit closed surfaces have coherent outward normals, including the interior faces.
    bm=bmesh.new(); bm.from_mesh(me); bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces)); bm.to_mesh(me); bm.free(); me.update()
    ob=bpy.data.objects.new(name,me); collection.objects.link(ob)
    ob['asset_scope']='surface_base_facility'; ob['role']=role
    return ob

def box(name, r0,r1,t0,t1,z0,z1,ang=0,mat=shell,collection=arch):
    v=[world(r,t,z,ang) for z in (z0,z1) for r,t in [(r0,t0),(r1,t0),(r1,t1),(r0,t1)]]
    return mesh(name,v,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],[mat],collection=collection)

def prism(name, outline,z0,z1,ang=0,mats=None,collection=arch):
    n=len(outline);v=[world(r,t,z,ang) for z in (z0,z1) for r,t in outline]
    f=[tuple(reversed(range(n))),tuple(range(n,2*n))]+[(j,(j+1)%n,(j+1)%n+n,j+n) for j in range(n)]
    return mesh(name,v,f,mats or [shell],collection=collection)

def loft(name, sections, ang=0,mats=None,collection=arch):
    n=len(sections[0][1]);v=[world(r,t,z,ang) for z,outline in sections for r,t in outline]
    f=[tuple(reversed(range(n)))]; ids=[0]
    for k in range(len(sections)-1):
        for j in range(n):
            f.append((k*n+j,k*n+(j+1)%n,(k+1)*n+(j+1)%n,(k+1)*n+j));ids.append(1 if len(mats or [])>1 and j%3==0 else 0)
    f.append(tuple(range((len(sections)-1)*n,len(sections)*n)));ids.append(1 if len(mats or [])>1 else 0)
    return mesh(name,v,f,mats or [shell],ids,collection)

def arc(name, profile, a0=0,a1=360,mat=shell,collection=arch,step=3.75):
    # Sweep a closed R/Z profile, with true wall thickness and capped ends.
    n=max(1,math.ceil(abs(a1-a0)/step)); full=abs(abs(a1-a0)-360)<1e-6
    row=n if full else n+1
    aa=[a0+(a1-a0)*j/n for j in range(row)]
    v=[polar(r,a,z) for r,z in profile for a in aa]; f=[]
    for k in range(len(profile)):
        for j in range(n):
            jj=(j+1)%row;kk=(k+1)%len(profile)
            f.append((k*row+j,k*row+jj,kk*row+jj,kk*row+j))
    if not full:
        f += [tuple(k*row for k in reversed(range(len(profile)))), tuple(k*row+n for k in range(len(profile)))]
    return mesh(name,v,f,[mat],collection=collection)

def arc_slab(name,ri,ro,z0,z1,a0=0,a1=360,mat=floor_mat,collection=arch,step=3.75):
    return arc(name,[(ri,z0),(ro,z0),(ro,z1),(ri,z1)],a0,a1,mat,collection,step)

def remaining_arcs(gaps):
    # Returns the walls remaining after real full-height portal gaps are removed.
    merged=[]
    cuts=[]
    for center, half in gaps:
        lo=(center-half)%360;hi=(center+half)%360
        cuts.extend([(lo,hi)] if lo<=hi else [(0,hi),(lo,360)])
    for lo,hi in sorted(cuts):
        if merged and lo<=merged[-1][1]:merged[-1][1]=max(merged[-1][1],hi)
        else:merged.append([lo,hi])
    last=0;out=[]
    for lo,hi in merged:
        if lo>last:out.append((last,lo))
        last=hi
    if last<360:out.append((last,360))
    return out

def faceted_pier(name,r0,r1,tcenter,width,z0,z1,angle,collection=arch):
    outline=[(r0,tcenter-width*.35),(r0+.25,tcenter-width*.5),(r1-.25,tcenter-width*.5),(r1,tcenter-width*.28),(r1,tcenter+width*.28),(r1-.25,tcenter+width*.5),(r0+.25,tcenter+width*.5),(r0,tcenter+width*.35)]
    return loft(name,[(z0,outline),(z1-.45,outline),(z1,[(r0+(r-r0)*.86,tcenter+(t-tcenter)*.7) for r,t in outline])],angle,[facet,edge],collection)

# MAIN CIRCULATION: 12 m nominal clear width, 10 m clear ceiling. All doorways are omitted wall geometry.
arc_slab('Ring_Continuous_Level_0_Floor',148,164,-1,0)
arc('Ring_Faceted_Removable_Ceiling_Hull',[(147.5,10),(164.5,10),(167,14),(163,20),(149,20),(145,14)],mat=shell,collection=roof)
# Recessed guide channels are cut into the actual wall profile.
inner_profile=[(148,0),(150,0),(150,.70),(149.76,.92),(149.76,1.18),(150,1.40),(150,10),(148,10)]
outer_profile=[(162,0),(164,0),(164,10),(162,10),(162,1.40),(162.24,1.18),(162.24,.92),(162,.70)]
outer_gaps=[(a,2.15) for a in WING_ANGLES]+[(ENTRY_ANGLE,2.55)]
inner_gaps=[(LOW_ACCESS_ANGLE,2.35)]
for side, profile,gaps,lr in [('Inner',inner_profile,inner_gaps,149.80),('Outer',outer_profile,outer_gaps,162.20)]:
    for j,(a0,a1) in enumerate(remaining_arcs(gaps)):
        arc(f'Ring_{side}_Wall_{j+1:02d}_With_Recess',profile,a0,a1,inner_mat)
        arc_slab(f'Ring_{side}_Recessed_Teal_Guide_{j+1:02d}',lr-.015,lr+.015,.99,1.10,a0+.08,a1-.08,soft_glow,fixtures)
# Faceted vertical seams remain outside the 12 m walking band.
for k in range(48):
    a=k*7.5
    if min(abs((a-c+180)%360-180) for c,h in outer_gaps)>4:
        faceted_pier(f'Ring_Outer_Pier_{k+1:02d}',162.04,164.6,0,.65,0,10,a)
    if abs((a-LOW_ACCESS_ANGLE+180)%360-180)>4:
        faceted_pier(f'Ring_Inner_Pier_{k+1:02d}',147.4,149.96,0,.65,0,10,a)
    # Low, wide floor seam plates provide human-scale module rhythm.
    box(f'Ring_Floor_Seam_{k+1:02d}',150.2,161.8,-.035,.035,.003,.011,a,black,fixtures)
    arc_slab(f'Ring_Ceiling_Light_{k+1:02d}',152.0,152.24,9.99,10.005,a+.9,a+5.9,soft_glow,roof)
    # Roof ribs unify the top silhouette with the eight primary arms.
    if k%3==0:
        box(f'Ring_Roof_Seam_{k//3+1:02d}',150,162,-.28,.28,20.0,20.3,a,facet,roof)

# THREE FACETED WING CHAMBERS, each 30 m wide x 32 m deep x 12 m high inside.
wing_labels=['Command','Resonance','Maintenance']
for n,(a,label) in enumerate(zip(WING_ANGLES,wing_labels),1):
    p=f'Wing_{n:02d}_{label}'
    outline=[(169,-15),(171,-17),(203,-17),(205,-14),(205,14),(203,17),(171,17),(169,15)]
    prism(p+'_Foundation_Floor',outline,-1,0,a,[floor_mat])
    # Connection neck: no end caps in the openings. A real 8 x 8 m doorway opens into the room.
    box(p+'_Connection_Floor',160,172,-5.2,5.2,-1,0,a,floor_mat)
    for s in [-1,1]:
        box(p+f'_Neck_Side_{s}',163,169,5.2*s if s>0 else -6.6,6.6*s if s>0 else -5.2,0,10,a,inner_mat)
        box(p+f'_Inner_Door_Jamb_{s}',169,171,4*s if s>0 else -17,17*s if s>0 else -4,0,12,a,inner_mat)
        box(p+f'_Side_Wall_{s}',171,203,15*s if s>0 else -17,17*s if s>0 else -15,0,12,a,inner_mat)
        box(p+f'_Side_Base_Sill_{s}',171,203,14.86*s if s>0 else -15.15,15.15*s if s>0 else -14.86,0,.55,a,facet)
        # Inset frames: wall backing, small proud surround, recessed luminous inner strips.
        for j,r in enumerate([175,183,191,199]):
            faceted_pier(p+f'_Wall_Rib_{s}_{j}',r-.45,r+.45,15.0*s,.55,0,12,a)
            box(p+f'_Wall_Channel_Back_{s}_{j}',r-.12,r+.12,14.97*s if s>0 else -15.025,15.025*s if s>0 else -14.97,2.0,8.6,a,black,fixtures)
            box(p+f'_Wall_Channel_Teal_{s}_{j}',r-.055,r+.055,14.94*s if s>0 else -14.96,14.96*s if s>0 else -14.94,2.2,8.3,a,glow,fixtures)
    box(p+'_Door_Lintel',169,171,-4,4,8,12,a,facet)
    box(p+'_Back_Wall',203,205,-17,17,0,12,a,inner_mat)
    # Eight-corner hull is entirely removable, including the neck ceiling.
    box(p+'_Neck_Ceiling',161,171,-6.6,6.6,10,12,a,shell,roof)
    roof_lower=[(168,-17),(171,-19),(203,-19),(207,-15),(207,15),(203,19),(171,19),(168,17)]
    roof_upper=[(173,-11),(176,-13),(198,-13),(201,-10),(201,10),(198,13),(176,13),(173,11)]
    loft(p+'_Removable_Faceted_Ceiling_Hull',[(12,roof_lower),(16,roof_lower),(22,roof_upper)],a,[shell,facet],roof)
    # Ceiling strips are on the ceiling plane, never below clear headroom.
    for t in [-9,9]: box(p+f'_Ceiling_Recessed_Teal_{t}',174,201,t-.15,t+.15,11.99,12.005,a,soft_glow,roof)
    for t in [-11.0,11.0]:
        box(p+f'_Floor_Inset_{t}',173,201,t-.05,t+.05,.005,.015,a,edge,fixtures)
    # Low alien consoles with sloping control decks, kept to the side/back of each spacious chamber.
    console_positions=[(199,-8),(199,0),(199,8)] if n==1 else ([(187,-12),(195,-12),(187,12),(195,12)] if n==2 else [(195,-10),(200,-10),(195,10),(200,10)])
    for j,(r,t) in enumerate(console_positions,1):
        body=[(r-1.1,t-1.8),(r+.8,t-1.8),(r+1.4,t-1.15),(r+1.4,t+1.15),(r+.8,t+1.8),(r-1.1,t+1.8)]
        top=[(r-.9,t-1.5),(r+.45,t-1.5),(r+.9,t-.95),(r+.9,t+.95),(r+.45,t+1.5),(r-.9,t+1.5)]
        loft(p+f'_Console_{j:02d}_Faceted_Body',[(0,body),(.92,body),(1.35,top)],a,[black,facet],fixtures)
        box(p+f'_Console_{j:02d}_Interface',r-.75,r+.35,t-1.25,t+1.25,1.352,1.365,a,screen_mat,fixtures)
        for q in range(3):box(p+f'_Console_{j:02d}_Readout_{q}',r-.65+q*.27,r-.57+q*.27,t-1.05,t+.65,1.367,1.377,a,soft_glow,fixtures)
    # Rear vertical power pylons anchor each chamber without occupying its central route.
    for s in [-1,1]:
        faceted_pier(p+f'_Rear_Power_Pier_{s}',200.5,203.05,12*s,2.1,0,10.8,a,fixtures)
        box(p+f'_Rear_Power_Light_{s}',200.45,200.48,12*s-.12,12*s+.12,1.0,9.4,a,glow,fixtures)
    # Doorframe light reveals clearly readable actual openings.
    for s in [-1,1]:box(p+f'_Door_Reveal_{s}',168.94,169.02,4*s-.09,4*s+.09,.5,7.6,a,glow,fixtures)

# OUTER ENTRY: 8 x 8 m unobstructed opening, long 12 m-clear throat, level exterior apron.
a=ENTRY_ANGLE
box('Entry_Throat_Level_0_Floor',160,222,-8,8,-1,0,a,floor_mat)
for s in [-1,1]:
    box(f'Entry_Throat_Side_Wall_{s}',163,220,6*s if s>0 else -8,8*s if s>0 else -6,0,10,a,inner_mat)
    box(f'Entry_Portal_Jamb_{s}',220,222,4*s if s>0 else -8,8*s if s>0 else -4,0,12,a,facet)
    box(f'Entry_Guidance_{s}',164,219,5.965*s if s>0 else -5.995,5.995*s if s>0 else -5.965,.94,1.08,a,soft_glow,fixtures)
    box(f'Entry_Portal_Teal_Reveal_{s}',222.01,222.04,4*s-.12,4*s+.12,.3,7.6,a,glow,fixtures)
    for j,r in enumerate([171,183,195,207]):faceted_pier(f'Entry_Throat_Faceted_Rib_{s}_{j}',r-.45,r+.45,6.0*s,.7,0,10,a)
    # External portal shoulders / low fin silhouettes.
    outline=[(214,8*s),(222,8*s),(224,10*s),(221,13*s),(216,13*s),(212,11*s)]
    loft(f'Entry_Outer_Faceted_Shoulder_{s}',[(0,outline),(14,outline),(24,[(r-2,t*.8) for r,t in outline])],a,[shell,facet])
box('Entry_Portal_Header',220,222,-4,4,8,12,a,facet)
entrylower=[(162,-8),(164,-10),(218,-10),(223,-7),(223,7),(218,10),(164,10),(162,8)]
entryupper=[(165,-4),(167,-6),(215,-6),(219,-4),(219,4),(215,6),(167,6),(165,4)]
loft('Entry_Removable_Throat_Ceiling_Hull',[(10,entrylower),(14,entrylower),(20,entryupper)],a,[shell,facet],roof)
for t in [-3.5,3.5]:box(f'Entry_Ceiling_Light_{t}',165,218,t-.10,t+.10,9.99,10.005,a,soft_glow,roof)
# Level apron, no step at either end. Tapers to the 8 m opening at the inner edge.
v=[world(r,t,z,a) for r,t,z in [(222,-5,-1),(230,-7,-1),(230,7,-1),(222,5,-1),(222,-5,0),(230,-7,0),(230,7,0),(222,5,0)]]
mesh('Entry_Level_Exterior_Approach_Apron',v,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],[floor_mat])

# RAISED SHAFT OBSERVATION GALLERY: floor Z16 is 2 m above the original 14 m lip.
arc_slab('Gallery_Raised_Observation_Floor',99.8,124,14.7,16)
# Load-bearing faceted support piers beneath the raised gallery; these do not intrude on its walking level.
for k in range(16):
    a=k*22.5
    for label,r0,r1 in [('Inner',101.2,104.6),('Outer',119.8,123.2)]:
        faceted_pier(f'Gallery_Understructure_{label}_Support_{k+1:02d}',r0,r1,0,4.8,-.2,14.7,a)

arc('Gallery_Inner_Faceted_Parapet',[(99.8,16),(101,16),(101,17.1),(100.65,17.32),(99.8,17.32)],mat=facet)
arc_slab('Gallery_Inner_Teal_Handrail',100.25,100.75,17.32,17.39,mat=soft_glow,collection=fixtures)
# Open framed windows occupy the entire inner elevation above its safe low parapet.
for k in range(32):
    a=k*11.25
    faceted_pier(f'Gallery_Inner_Window_Pier_{k+1:02d}',99.8,101.3,0,1.05,16,26,a)
    box(f'Gallery_Inner_Window_Reveal_{k+1:02d}',101.305,101.32,-.06,.06,18,24.7,a,soft_glow,fixtures)
arc_slab('Gallery_Inner_Upper_Spandrel',99.8,101.3,24.8,26,mat=inner_mat)
for j,(a0,a1) in enumerate(remaining_arcs([(HIGH_ACCESS_ANGLE,2.65)])):
    arc_slab(f'Gallery_Outer_Wall_{j+1:02d}',122,124,16,26,a0,a1,inner_mat)
    arc_slab(f'Gallery_Outer_Lower_Recess_{j+1:02d}',121.94,122.01,17,17.25,a0,a1,black,fixtures)
    arc_slab(f'Gallery_Outer_Teal_Guide_{j+1:02d}',121.91,121.94,17.08,17.18,a0,a1,soft_glow,fixtures)
arc('Gallery_Removable_Faceted_Ceiling_Hull',[(99.8,26),(124.3,26),(126,28),(122,30),(103,30),(98.7,28)],mat=shell,collection=roof)
for k in range(32):
    a=k*11.25
    box(f'Gallery_Floor_Seam_{k+1:02d}',102,121.5,-.04,.04,16.003,16.012,a,black,fixtures)
    arc_slab(f'Gallery_Ceiling_Inlay_{k+1:02d}',111,111.3,25.99,26.005,a+1.7,a+9.55,soft_glow,roof)
# Sparse viewing consoles leave the 20 m broad gallery circulation almost entirely free.
for k,a in enumerate([45,135,225],1):
    faceted_pier(f'Gallery_Observation_Console_{k}',102.0,103.6,0,3,16,17.25,a,fixtures)
    box(f'Gallery_Observation_Console_Screen_{k}',102.1,103.4,-1.1,1.1,17.255,17.27,a,screen_mat,fixtures)

# 1:10 RAMP: 12 m width, R130–142, 67.5-degree sweep, Z0 to Z16.
# Every segment has planar top surfaces and a 10 m unobstructed ceiling following the slope.
RAMP_SEGMENTS=36
for k in range(RAMP_SEGMENTS):
    a0=RAMP_A0+(RAMP_A1-RAMP_A0)*k/RAMP_SEGMENTS
    a1=RAMP_A0+(RAMP_A1-RAMP_A0)*(k+1)/RAMP_SEGMENTS
    z0=16*k/RAMP_SEGMENTS;z1=16*(k+1)/RAMP_SEGMENTS
    def ramp_prism(name,ri,ro,offset0,offset1,mat,coll):
        verts=[polar(r,a,z+off) for off in [offset0,offset1] for r,a,z in [(ri,a0,z0),(ro,a0,z0),(ro,a1,z1),(ri,a1,z1)]]
        return mesh(name,verts,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],[mat],collection=coll)
    ramp_prism(f'Ramp_Walkable_Floor_Segment_{k+1:02d}',130,142,-1,0,floor_mat,arch)
    ramp_prism(f'Ramp_Inner_Parapet_Segment_{k+1:02d}',129.1,130,0,1.1,facet,arch)
    ramp_prism(f'Ramp_Outer_Parapet_Segment_{k+1:02d}',142,142.9,0,1.1,facet,arch)
    ramp_prism(f'Ramp_Inner_Guidance_Segment_{k+1:02d}',129.5,129.8,1.10,1.17,soft_glow,fixtures)
    ramp_prism(f'Ramp_Outer_Guidance_Segment_{k+1:02d}',142.2,142.5,1.10,1.17,soft_glow,fixtures)
    ramp_prism(f'Ramp_Removable_Ceiling_Segment_{k+1:02d}',129.1,142.9,10,12,shell,roof)
    if k%4==0:
        faceted_pier(f'Ramp_Inner_Frame_{k//4+1:02d}',129.1,130,0,.75,z0,z0+10,a0)
        faceted_pier(f'Ramp_Outer_Frame_{k//4+1:02d}',142,142.9,0,.75,z0,z0+10,a0)
# Ramp load-bearing support spines, all below the sloped walking surface.
for k in [8,16,24,32]:
    a=RAMP_A0+(RAMP_A1-RAMP_A0)*k/RAMP_SEGMENTS
    z=16*k/RAMP_SEGMENTS
    faceted_pier(f'Ramp_Understructure_Support_{k:02d}',134,138,0,3.5,-.2,z-1.15,a)
# Flat, generous turn landings at both ends and real radial doorways to the ring/gallery.
arc_slab('Ramp_Lower_Flat_Turn_Landing',130,142,-1,0,286.5,292.5)
arc_slab('Ramp_Upper_Flat_Turn_Landing',130,142,15,16,360,366)
arc_slab('Ramp_Lower_Landing_Inner_Parapet',129.1,130,0,1.1,286.5,292.5,facet)
arc_slab('Ramp_Upper_Landing_Outer_Parapet',142,142.9,16,17.1,360,366,facet)
# No parapet is placed over either access opening.
arc_slab('Ramp_Lower_Landing_Removable_Ceiling',129.1,142.9,10,12,286.5,292.5,shell,roof)
arc_slab('Ramp_Upper_Landing_Removable_Ceiling',129.1,142.9,26,28,360,366,shell,roof)
box('Ramp_Lower_End_Wall',129.1,142.9,-.8,0,0,10,286.5,inner_mat)
box('Ramp_Upper_End_Wall',129.1,142.9,0,.8,16,26,366,inner_mat)
for label,ang,r0,r1,z in [('Lower',LOW_ACCESS_ANGLE,138,151,0),('Upper',HIGH_ACCESS_ANGLE,120,134,16)]:
    box(f'Ramp_{label}_Access_Floor',r0,r1,-5,5,z-1,z,ang,floor_mat)
    # Access side walls stop at the landing edge, leaving its full turning area open.
    wr0,wr1=(142,150) if label=='Lower' else (123,130)
    for s in [-1,1]:
        box(f'Ramp_{label}_Access_Side_{s}',wr0,wr1,5*s if s>0 else -6,6*s if s>0 else -5,z,z+10,ang,inner_mat)
        box(f'Ramp_{label}_Access_Guidance_{s}',wr0,wr1,4.94*s if s>0 else -4.98,4.98*s if s>0 else -4.94,z+.95,z+1.07,ang,soft_glow,fixtures)
    box(f'Ramp_{label}_Access_Removable_Ceiling',r0,r1,-6,6,z+10,z+12,ang,shell,roof)

# Metadata and route data for independent audit. No presentation objects enter either GLB.
scene['facility_main_floor_z_m']=0.0
scene['facility_gallery_floor_z_m']=16.0
scene['facility_ramp_slope']='1:10.014 nominal centerline; 16 m rise over 160.22 m arc'
scene['facility_roof_collection']=roof.name
scene['facility_clear_bore_radius_m']=75.0
scene['facility_note']='Actual wall/floor/ceiling meshes; open doors. Ring 12x10 m clear; rooms 30x32x12 m; gallery raised over 14 m source rim. No collision/gameplay added.'

routes={
 'entry_to_ring':[world(r,0,z,ENTRY_ANGLE) for r,z in [(232,0),(230,0),(226,0),(222,0),(218,0),(205,0),(180,0),(164,0),(156,0)]],
 'main_ring_clockwise':[polar(156,ENTRY_ANGLE+i*3.75,0) for i in range(97)],
 'room_routes':{label:[world(r,0,0,a) for r in [156,162,167,170,176,187,196]] for a,label in zip(WING_ANGLES,wing_labels)},
 'ring_to_ramp_lower':[polar(r,LOW_ACCESS_ANGLE,0) for r in [156,151,146,142,138,136]],
 'ramp_lower_landing':[polar(136,a,0) for a in [286.8,289.5,292.5]],
 'ramp_ascent':[polar(136,RAMP_A0+(RAMP_A1-RAMP_A0)*j/72,16*j/72) for j in range(73)],
 'ramp_upper_to_gallery':[polar(136,a,16) for a in [360,361.5,363]]+[polar(r,HIGH_ACCESS_ANGLE,16) for r in [132,128,124,120,112,106]],
 'gallery_circuit':[polar(112,HIGH_ACCESS_ANGLE+i*3.75,16) for i in range(97)],
 'gallery_viewpoint':polar(106,275.625,17.7),
}
manifest={
 'source_files_sha256':source_hashes,
 'units':'metres; Blender Z-up; GLB Y-up',
 'export_collections':[arch.name,roof.name,fixtures.name],
 'removable_roof_collection':roof.name,
 'entry_angle_degrees':ENTRY_ANGLE,
 'entry_clear_opening_m':{'width':8,'height':8,'radial_position':221},
 'main_ring_m':{'inside_radius':150,'outside_radius':162,'floor_z':0,'clear_height':10},
 'rooms':[{'name':label,'angle_degrees':a,'inside_radial_bounds':[171,203],'inside_tangent_bounds':[-15,15],'clear_height':12,'door_width':8,'door_height':8} for a,label in zip(WING_ANGLES,wing_labels)],
 'gallery_m':{'floor_z':16,'floor_inner_radius':99.8,'floor_outer_radius':124,'inside_outer_wall_radius':122,'parapet_top_z':17.39,'clear_ceiling_z':26,'roof_max_z':30},
 'ramp_m':{'inside_radius':130,'outside_radius':142,'start_angle':RAMP_A0,'end_angle':RAMP_A1,'rise':16,'centerline_run':136*math.radians(67.5),'clear_height':10,'lower_access_angle':LOW_ACCESS_ANGLE,'upper_access_angle':HIGH_ACCESS_ANGLE},
 'routes_blender_xyz':routes,
 'eye_height_above_route_m':1.7,
 'notes':['Source spire geometry untouched.','No beam, ground, lights, cameras, human references, or gameplay/collision exported.','Gallery floor is above the original 14 m shaft rim.','Main ring loop uses angular samples rather than a chord across its center.','Route points refer to floor level; add 1.7 m for eye height.'],
}
facility_objects=list(arch.objects)+list(roof.objects)+list(fixtures.objects)
manifest['facility_object_count']=len(facility_objects)
manifest['facility_vertices']=sum(len(o.data.vertices) for o in facility_objects)
manifest['facility_polygons']=sum(len(o.data.polygons) for o in facility_objects)
for filename,objects in [('Surface_Base_Facility_Godot.glb',facility_objects),('Surface_Spire_and_Facility_Godot.glb',list(base.objects)+facility_objects)]:
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:o.hide_set(False);o.select_set(True)
    bpy.context.view_layer.objects.active=objects[0]
    bpy.ops.export_scene.gltf(filepath=os.path.join(ROOT,filename),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True)
    print('EXPORTED',filename,flush=True)
bpy.ops.object.select_all(action='DESELECT')
scene['export_note']='00 original spire; 10/11/12 facility. Presentation 90/91/92 excluded from all GLB. 11 removes all ceilings and roof hulls for cutaway.'
# Preserve the original presentation setup; parent will add inspection cameras and renders.
if os.path.isfile(__file__):bpy.data.texts.load(__file__)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Surface_Spire_with_Facility.blend'))
for filename,h in source_hashes.items():assert file_hash(os.path.join(ROOT,filename))==h,'Original file changed! '+filename
# Densify each piecewise route for capsule/floor checks (maximum 1.75 m spacing).
route_samples=[]
def add_route(name,points):
    dense=[]
    for p,q in zip(points[:-1],points[1:]):
        distance=math.sqrt(sum((q[i]-p[i])**2 for i in range(3)))
        count=max(1,math.ceil(distance/1.75))
        dense.extend([[p[i]+(q[i]-p[i])*j/count for i in range(3)] for j in range(count)])
    dense.append(list(points[-1])); route_samples.append({'name':name,'points':dense})
for name,pts in routes.items():
    if name=='room_routes':
        for room,roompts in pts.items():add_route('ring_to_'+room,roompts)
    elif name!='gallery_viewpoint':add_route(name,pts)
# Off-mesh exterior ground points are kept only in the narrative manifest, not floor audit.
route_samples[0]['points']=[p for p in route_samples[0]['points'] if math.hypot(p[0],p[1])<=230.001]
with open(os.path.join(ROOT,'facility_route_samples.json'),'w') as f:json.dump(route_samples,f,indent=2)
with open(os.path.join(ROOT,'facility_geometry_manifest.json'),'w') as f:json.dump(manifest,f,indent=2)
print('FACILITY_GEOMETRY_FROZEN',json.dumps({k:manifest[k] for k in ['facility_object_count','facility_vertices','facility_polygons','source_files_sha256']}),flush=True)
