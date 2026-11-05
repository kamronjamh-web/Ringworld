import bpy, bmesh, math, os, json
from mathutils import Vector
from math import sin,cos,pi
OUT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
for d in bpy.data.collections:
    if d.name!='Collection': bpy.data.collections.remove(d)
scene=bpy.context.scene
scene.unit_settings.system='METRIC'; scene.unit_settings.scale_length=1; scene.unit_settings.length_unit='METERS'
COLS={}
for name in ['01_Tower_Half_Positive_X','02_Tower_Half_Negative_X','03_Split_Emitter_Spheres','04_Crown_Containment','05_Base_Power_Intakes','90_Render_Context_NOT_EXPORTED','91_Cameras_Lights_NOT_EXPORTED']:
    col=bpy.data.collections.new(name);scene.collection.children.link(col);COLS[name]=col
active=COLS['01_Tower_Half_Positive_X']; ASSET=[]
def material(name,color,metal=0,rough=.4,emission=None):
    m=bpy.data.materials.new(name);m.diffuse_color=(*color,1);m.use_nodes=True
    bs=m.node_tree.nodes.get('Principled BSDF');bs.inputs['Base Color'].default_value=(*color,1);bs.inputs['Metallic'].default_value=metal;bs.inputs['Roughness'].default_value=rough
    if emission: bs.inputs['Emission Color'].default_value=(*color,1);bs.inputs['Emission Strength'].default_value=emission
    return m
ivory=material('Pale ceramic titanium',(0.66,.61,.47),.43,.32)
light=material('Warm edge alloy',(.82,.75,.58),.55,.28)
dark=material('Recessed graphite steel',(.035,.065,.078),.7,.32)
metal=material('Gunmetal mechanisms',(.12,.18,.19),.8,.28)
bronze=material('Champagne structural trim',(.4,.29,.14),.75,.28)
cyan=material('Cyan energized conduits',(.008,.65,1),.2,.26,4.0)
black=material('Intake black cavity',(.008,.014,.019),.2,.7)
contextmat=material('Optional wall context',(.19,.25,.28),.1,.8)
def register(ob,mat=None,asset=True):
    for col in list(ob.users_collection):col.objects.unlink(ob)
    active.objects.link(ob)
    if mat:ob.data.materials.append(mat)
    if asset:ASSET.append(ob)
    return ob
def mesh(name,verts,faces,mat,bevel=0,smooth=False):
    m=bpy.data.meshes.new(name+'_mesh');m.from_pydata(verts,[],faces);m.update();bm=bmesh.new();bm.from_mesh(m);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));
    if bm.calc_volume(signed=True)<0:bmesh.ops.reverse_faces(bm,faces=list(bm.faces))
    bm.to_mesh(m);bm.free();ob=bpy.data.objects.new(name,m);active.objects.link(ob);m.materials.append(mat);ASSET.append(ob)
    if smooth:
        for p in m.polygons:p.use_smooth=True
    if bevel:
        mod=ob.modifiers.new('Machined edge bevel','BEVEL');mod.width=bevel;mod.segments=2
        mod=ob.modifiers.new('Weighted corner normals','WEIGHTED_NORMAL')
    return ob
def box(name,center,size,mat,bevel=.2):
    x,y,z=center;a,b,c=[v/2 for v in size]
    return mesh(name,[(x+dx*a,y+dy*b,z+dz*c) for dx,dy,dz in [(-1,-1,-1),(1,-1,-1),(1,1,-1),(-1,1,-1),(-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1)]],[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],mat,bevel)
def tube(name,points,radius,mat,sides=8,closed=False):
    verts=[];n=len(points);previous_u=None
    plane_normal=None
    if closed and n>=3:
        plane_normal=(Vector(points[1])-Vector(points[0])).cross(Vector(points[2])-Vector(points[1])).normalized()
    for i,p in enumerate(points):
        p=Vector(p);prev=Vector(points[(i-1)%n] if (closed or i) else points[0]);nxt=Vector(points[(i+1)%n] if (closed or i<n-1) else points[-1]);tan=(nxt-prev).normalized()
        if plane_normal is not None: u=plane_normal
        elif previous_u is not None: u=(previous_u-tan*previous_u.dot(tan)).normalized()
        else:
            ref=min([Vector((1,0,0)),Vector((0,1,0)),Vector((0,0,1))],key=lambda q:abs(q.dot(tan)))
            u=tan.cross(ref).normalized()
        previous_u=u;v=tan.cross(u).normalized()
        for j in range(sides):verts.append(tuple(p+radius*(cos(j*2*pi/sides)*u+sin(j*2*pi/sides)*v)))
    faces=[]
    for i in range(n if closed else n-1):
        for j in range(sides):faces.append((i*sides+j,i*sides+(j+1)%sides,((i+1)%n)*sides+(j+1)%sides,((i+1)%n)*sides+j))
    if not closed:faces.extend([tuple(range(sides-1,-1,-1)),tuple((n-1)*sides+j for j in range(sides))])
    return mesh(name,verts,faces,mat,smooth=True)
def beam(name,a,b,width,depth,mat):
    a,b=Vector(a),Vector(b);ob=box(name,(0,0,0),(width,depth,(b-a).length),mat,.17);ob.location=(a+b)/2;ob.rotation_euler=(b-a).to_track_quat('Z','Y').to_euler();return ob
# Sculpted, continuous main blade shells. All heights are meters above wall top.
levels=[(0,24,40,39),(1,24,40,36.8),(3,24.1,40,33),(6,24.2,39.9,29.5),(10,24.3,39.8,27),(16,24.4,39.8,24.7),(25,24.5,39.8,23.2),(40,24.7,39.7,22),(72,25,39.6,21),(145,25.2,39.2,20.4),(250,25.5,38.7,20),(450,25.7,38.2,18),(780,25.8,37.8,17),(1150,25.7,37.5,17),(1450,26,37.7,16),(1530,28,37,14),(1580,32,36,9),(1600,34,35,3)]
def shape_ring(sign,lo,hi,dep):
    # bevelled/octagonal cross section
    c=min(2,(hi-lo)*.3,dep*.3)
    return [(sign*x,y) for x,y in [(lo,-dep+c),(lo+c,-dep),(hi-c,-dep),(hi,-dep+c),(hi,dep-c),(hi-c,dep),(lo+c,dep),(lo,dep-c)]]
for sign in [1,-1]:
    active=COLS['01_Tower_Half_Positive_X' if sign==1 else '02_Tower_Half_Negative_X']
    side='POS' if sign==1 else 'NEG'
    verts=[(x,y,z) for z,lo,hi,d in levels for x,y in shape_ring(sign,lo,hi,d)]
    faces=[tuple(range(7,-1,-1))]
    for k in range(len(levels)-1):
        for j in range(8):faces.append((k*8+j,k*8+(j+1)%8,(k+1)*8+(j+1)%8,(k+1)*8+j))
    faces.append(tuple((len(levels)-1)*8+j for j in range(8)))
    if sign<0:faces=[tuple(reversed(f)) for f in faces]
    core=mesh(side+'_tapered_structural_blade',verts,faces,ivory,.28)
    core['half_space']='X > 0' if sign==1 else 'X < 0'
    # Dark inset flutes and thin live rails follow curvature; no crossing of the shield plane.
    for yy in [-1,1]:
        pts=[(sign*(lo+3.0),yy*(d+.45),z+1) for z,lo,hi,d in levels[:-1] if z>=72]
        tube(side+f'_inner_flute_{yy}',pts,1.35,dark,8)
        tube(side+f'_cyan_bus_{yy}',[(x-sign*.15,y+yy*1.1,z) for x,y,z in pts],.35,cyan,8)
        # Strong outer split ridges lift off the main shell, inset under the bounding limits.
        tube(side+f'_spine_trim_{yy}',[(sign*(hi-1.8),yy*(d-2.5),z+1) for z,lo,hi,d in levels[:-1]],.55,bronze,6)
    # Front and rear individually articulated armor panels with intentional dark joints.
    for k in range(20):
        z=125+k*70
        dep=18
        for (za,loa,hia,da),(zb,lob,hib,db) in zip(levels,levels[1:]):
            if za<=z<=zb:dep=da+(db-da)*(z-za)/(zb-za);break
        for yy in [-1,1]:
            # slim 5-sided swept plate, not a stack of boxes
            x0,x1=27,37.8
            v2=[(sign*x0,yy*(dep+.2),z-22),(sign*x1,yy*(dep+.2),z-29),(sign*(x1-.4),yy*(dep+.2),z+25),(sign*(x0+2),yy*(dep+.2),z+31),(sign*x0,yy*(dep+.2),z+18)]
            vv=v2+[(x,y+yy*.6,zz) for x,y,zz in v2]
            ff=[(0,1,2,3,4),(9,8,7,6,5)]+[(j,(j+1)%5,(j+1)%5+5,j+5) for j in range(5)]
            mesh(side+f'_armor_{k+1:02d}_{yy}',vv,ff,light if k%4==0 else ivory,.12)
            # angular diagonal relieved strap every fourth bay
            if k%4==1:
                beam(side+f'_swept_rib_{k}_{yy}',(sign*27,yy*(dep+1.3),z-20),(sign*37,yy*(dep+1.3),z+22),1.4,1.0,bronze)
    for k in range(9):
        zz=185+k*145
        # Swept blade with exposed dark backing and luminous inner channel.
        for yy in [-1,1]:
            dd=20 if zz<450 else 17.5
            pts=[(sign*25.8,yy*(dd+1),zz-31),(sign*22.8,yy*(dd+2),zz-5),(sign*24.2,yy*(dd+2),zz+42),(sign*28.0,yy*(dd+1),zz+66)]
            tube(side+f'_exposed_buttress_{k}_{yy}',pts,1.1,metal,6)
            tube(side+f'_buttress_glint_{k}_{yy}',[(x-sign*.65,y+yy*.5,z) for x,y,z in pts],.20,cyan,6)
            beam(side+f'_rib_anchor_{k}_{yy}',(sign*29,yy*(dd+.4),zz-18),(sign*37.5,yy*(dd+.4),zz+18),.9,.7,bronze)
    # Slender inner spire diverges from main blade, creating forked crown silhouette.
    for yy in [-1,1]:
        ringz=[(1410,25,yy*14,3.8),(1495,24,yy*13,3.3),(1555,26,yy*9,2.6),(1584,28,yy*7,.35)]
        vv=[]
        for z,x,y,r in ringz:vv += [(sign*(x+dx*r),y+dy*r,z) for dx,dy in [(-1,-1),(1,-1),(1,1),(-1,1)]]
        ff=[(3,2,1,0),(12,13,14,15)]+[(k*4+j,k*4+(j+1)%4,(k+1)*4+(j+1)%4,(k+1)*4+j) for k in range(3) for j in range(4)]
        mesh(side+f'_inner_crown_prong_{yy}',vv,ff,light,.15)
    # Integrated swept foot fins remain entirely within 80 x 80m envelope.
    for yy in [-1,1]:
        path=[(sign*28,yy*37,6),(sign*28,yy*35,35),(sign*28,yy*28,80),(sign*28,yy*22,145),(sign*28,yy*19,245)]
        tube(side+f'_foot_cable_recess_{yy}',path,2.0,dark,8)
        tube(side+f'_foot_energized_feed_{yy}',[(x-sign*1.2,y,z) for x,y,z in path],.55,cyan,8)
# Regular emitter module prototypes, kept genuinely bisected with 0.9m open seam.
active=COLS['03_Split_Emitter_Spheres']; SPHERE_Z=[145+70*i for i in range(20)]
proto_start=len(ASSET); r=22.5; gap=.9; theta_max=math.acos(1.4/r)
for sign in [1,-1]:
    side='POS' if sign==1 else 'NEG';N=32;T=14
    vv=[]
    # Pole is a single vertex to avoid degenerate UV sphere triangles.
    vv.append((sign*r,0,0))
    for t in range(1,T+1):
        th=theta_max*t/T
        for j in range(N):
            ph=2*pi*j/N;vv.append((sign*r*cos(th),(r/sin(theta_max))*sin(th)*cos(ph),(r/sin(theta_max))*sin(th)*sin(ph)))
    ff=[]
    for j in range(N):ff.append((0,1+j,1+(j+1)%N))
    for t in range(T-1):
        for j in range(N):a=1+t*N+j;b=1+t*N+(j+1)%N;c=1+(t+1)*N+(j+1)%N;d=1+(t+1)*N+j;ff.append((a,b,c,d))
    ff.append(tuple(1+(T-1)*N+j for j in range(N-1,-1,-1)))
    if sign<0:ff=[tuple(reversed(f)) for f in ff]
    vv=[(x if abs(x)>22.45 else x*.975,y*.973,z*.973) for x,y,z in vv]
    ob=mesh('Emitter_01_'+side+'_hemisphere_R22_5',vv,ff,dark,smooth=True)
    ob.data.materials.append(light);ob.data.materials.append(dark)
    for p in ob.data.polygons:
        if p.index==len(ff)-1:p.material_index=2;p.use_smooth=False
        elif False:p.material_index=1
    ob['nominal_diameter_m']=45;ob['split_clearance_m']=gap
    # Circular seam crowns are inset onto the cut face; radius is slightly below envelope.
    for x,rr,thick,mat,label in [(.7,22.22,.25,cyan,'luminous_split_rim'),(.85,20.7,.36,bronze,'inner_split_rim'),(.7,17.8,.24,metal,'inner_containment')]:
        tube('Emitter_01_'+side+'_'+label,[(sign*x,rr*cos(2*pi*j/64),rr*sin(2*pi*j/64)) for j in range(64)],thick,mat,6,True)
    # Forty thick armor panels per hemisphere; real recessed gaps reveal graphite liner.
    tv=[];tf=[];material_indices=[]
    bands=[.07,.40,.74,1.07,1.32,theta_max]
    for bi in range(len(bands)-1):
        ta=bands[bi]+(.010 if bi else 0);tb=bands[bi+1]-(.010 if bi<len(bands)-2 else 0)
        for j in range(8):
            pa=j*2*pi/8-pi/8+.012;pb=j*2*pi/8+pi/8-.012
            base=len(tv);nt=3;np=4
            for inset in [0,.60]:
                for ti in range(nt+1):
                    th=ta+(tb-ta)*ti/nt
                    for pj in range(np+1):
                        ph=pa+(pb-pa)*pj/np;rad=r-inset
                        tv.append((sign*rad*cos(th),(rad/sin(theta_max))*sin(th)*cos(ph),(rad/sin(theta_max))*sin(th)*sin(ph)))
            grid=(nt+1)*(np+1)
            for off in [0,grid]:
                for ti in range(nt):
                    for pj in range(np):
                        q=base+off+ti*(np+1)+pj;tf.append((q,q+1,q+np+2,q+np+1));material_indices.append((bi+j)%5==0)
            for ti in range(nt):
                for pj in [0,np]:
                    q=base+ti*(np+1)+pj;tf.append((q,q+np+1,q+np+1+grid,q+grid));material_indices.append(False)
            for ti in [0,nt]:
                for pj in range(np):
                    q=base+ti*(np+1)+pj;tf.append((q,q+1,q+1+grid,q+grid));material_indices.append(False)
    armor=mesh('Emitter_01_'+side+'_segmented_shell_armor',tv,tf,ivory,smooth=True)
    armor.data.materials.append(light)
    for p,mi in zip(armor.data.polygons,material_indices):p.material_index=int(mi)
    # Mechanical equatorial armor ribs lie wholly inside the 45m spherical envelope.
    for theta in [.40,.74,1.07,1.32]:
        rr=(r-.13)/sin(theta_max)*sin(theta);xx=sign*(r-.13)*cos(theta)
        tube('Emitter_01_'+side+f'_latitude_frame_{theta}',[(xx,rr*cos(2*pi*j/48),rr*sin(2*pi*j/48)) for j in range(48)],.19,bronze,5,True)
    # Mechanical trunnions link each sphere half only to its own tower half.
    beam('Emitter_01_'+side+'_mount_trunnion',(sign*20,0,0),(sign*27,0,0),5.5,5.5,metal)
    for yy in [-1,1]:
        beam('Emitter_01_'+side+f'_fork_{yy}',(sign*26,yy*19,-10),(sign*20,yy*13,-4),1.7,2,bronze)
        beam('Emitter_01_'+side+f'_upper_fork_{yy}',(sign*26,yy*19,10),(sign*20,yy*13,4),1.7,2,bronze)
    for zz in [-1,1]:
        beam('Emitter_01_'+side+f'_forward_gimbal_{zz}',(sign*26,-19,zz*12),(sign*16,-15,zz*7),2.1,2.5,metal)
        tube('Emitter_01_'+side+f'_gimbal_live_contact_{zz}',[(sign*25,-20.5,zz*11),(sign*19,-17,zz*8)],.22,cyan,6)
    # Small gimbal bearing at exposed forward quadrant.
    tube('Emitter_01_'+side+'_bearing',[(sign*18.5,-14.5+2.2*cos(2*pi*j/24),2.2*sin(2*pi*j/24)) for j in range(24)],.4,metal,6,True)
prototypes=list(ASSET[proto_start:])
for ob in prototypes:ob.location.z=SPHERE_Z[0]
for i,z in enumerate(SPHERE_Z[1:],2):
    for orig in prototypes:
        ob=orig.copy();ob.data=orig.data;ob.name=orig.name.replace('Emitter_01_',f'Emitter_{i:02d}_');active.objects.link(ob);ob.location.z=z;ASSET.append(ob)
# Crown: independent split arcs and extra high-field emitter geometry, no force-field mesh.
active=COLS['04_Crown_Containment'];zc=SPHERE_Z[-1]
for sign in [1,-1]:
    side='POS' if sign==1 else 'NEG'
    # Two inclined rings, each interrupted at the shield plane. Max X/Y remains below 40.
    for tilt,label in [(0,'front'),(.62,'oblique')]:
        rr=29.3
        points=[]
        for i in range(49):
            a=-pi/2+.06+(pi-.12)*i/48
            x=sign*rr*cos(a);q=rr*sin(a)
            points.append((x,q*sin(tilt),zc+q*cos(tilt)))
        tube('Crown_'+side+'_'+label+'_containment_arc',points,1.65,bronze,10)
        tube('Crown_'+side+'_'+label+'_energized_trace',[(x,y-1.38,z) for x,y,z in points],.28,cyan,7)
    for yy in [-1,1]:
        beam('Crown_'+side+f'_radial_clamp_{yy}',(sign*23,yy*13,zc-8),(sign*32,yy*17,zc-24),3,3,metal)
        beam('Crown_'+side+f'_upper_suspension_{yy}',(sign*22,yy*12,zc+17),(sign*29,yy*15,zc+45),2.5,2.5,bronze)
    # Crown tip vanes framing the elevated field origin; they stop well below spire tip.
    for yy in [-1,1]:
        vv=[(sign*2.0,yy*3,zc+22.6),(sign*5.0,yy*6,zc+27),(sign*5.5,yy*5,zc+48),(sign*1.8,yy*2.2,zc+56)]
        vv=vv+[(x+sign*1.3,y,z) for x,y,z in vv]
        mesh('Crown_'+side+f'_field_focus_vane_{yy}',vv,[(0,1,2,3),(7,6,5,4),(0,4,5,1),(1,5,6,2),(2,6,7,3),(3,7,4,0)],light,.15)
        tube('Crown_'+side+f'_focus_trace_{yy}',[(sign*1.7,yy*2.2,zc+26),(sign*1.7,yy*2.2,zc+52)],.2,cyan,6)
    for j in range(5):
        a=-1.15+j*.575
        x=sign*29.3*cos(a);z=zc+29.3*sin(a)
        box('Crown_'+side+f'_gimbal_lock_{j}',(x,-2.2,z),(3.0,5,3),metal,.3)
# Recessed base docking ports, one per separated tower half; opening faces -Y.
active=COLS['05_Base_Power_Intakes']
for sign in [1,-1]:
    side='POS' if sign==1 else 'NEG'
    # independent plinths, exactly at wall top with no exported wall or cross-plane tie
    foot=[(4,-29),(10,-40),(31,-40),(40,-28),(40,28),(31,40),(10,40),(4,29)]
    fv=[(sign*x,y,z) for z in [0,3] for x,y in foot]
    mesh('Base_'+side+'_sculpted_wall_saddle',fv,[tuple(range(7,-1,-1)),tuple(range(8,16))]+[(j,(j+1)%8,(j+1)%8+8,j+8) for j in range(8)],dark,.25)
    # Layered concave buttresses grow out of wall-top saddles and merge into the blades.
    sweep=[(0,39.5,16),(2,35.0,20),(5,31.0,22),(10,27.5,22),(20,24.8,20.5),(40,22.8,18),(80,21.3,16.7),(140,20.5,16),(210,20.0,15.8),(290,19.4,15.7)]
    for yy in [-1,1]:
        for rib,xc in enumerate([24.0,30.0,36.5]):
            sv=[];rows=sweep[:(10-rib)]
            for zi,(z,yo,yi) in enumerate(rows):
                shrink=1-zi/(len(rows)-1)*.65
                half=(2.6 if rib==0 else 2.0)*shrink
                center=xc+(2.0 if rib==0 else 0)*zi/(len(rows)-1)
                sv.extend([(sign*(center-half),yy*yi,z),(sign*(center+half),yy*yi,z),(sign*(center+half),yy*yo,z),(sign*(center-half),yy*yo,z)])
            sf=[(3,2,1,0),tuple((len(rows)-1)*4+j for j in range(4))]+[(k*4+j,k*4+(j+1)%4,(k+1)*4+(j+1)%4,(k+1)*4+j) for k in range(len(rows)-1) for j in range(4)]
            mesh('Base_'+side+f'_swept_wall_buttress_{yy}_{rib}',sv,sf,light if rib==0 else ivory,.35)
            spine=[]
            for zi,(z,yo,yi) in enumerate(rows):
                center=xc+(2.0 if rib==0 else 0)*zi/(len(rows)-1)
                spine.append((sign*center,yy*min(yo+.25,39.1),z+1))
            tube('Base_'+side+f'_buttress_inset_{yy}_{rib}',spine,.65,dark,7)
            tube('Base_'+side+f'_buttress_feed_{yy}_{rib}',[(x,y+yy*.5,z) for x,y,z in spine],.18,cyan,6)
    # Corrected user direction: 50m ALONG the wall past body ends Y=+/-40m.
    # Feet remain within X=+/-40m; the shield plane X=0 stays unobstructed.
    for direction in [-1,1]:
        for fin,xc in enumerate([24.5,30.5,37.5]):
            fv=[];edge=[];steps=28
            for i in range(steps+1):
                t=i/steps;z=380*t*t
                yo=90*(1-t)*(1-t)+36*(1-t)*t+17*t*t
                yi=10+4*t
                half=(2.1 if fin in [0,2] else 2.6)*(1-.55*t)
                fv.extend([(sign*(xc-half),direction*yi,z),(sign*(xc+half),direction*yi,z),(sign*(xc+half),direction*yo,z),(sign*(xc-half),direction*yo,z)])
                edge.append((sign*xc,direction*(yo-.65),z+1.0))
            ff=[(3,2,1,0),tuple(steps*4+j for j in range(4))]+[(k*4+j,k*4+(j+1)%4,(k+1)*4+(j+1)%4,(k+1)*4+j) for k in range(steps) for j in range(4)]
            ob=mesh('Base_'+side+f'_50m_along_wall_swept_fin_{direction}_{fin}',fv,ff,ivory if fin in [0,2] else light,0)
            ob['projection_past_original_body_end_along_wall_m']=50.0
            tube('Base_'+side+f'_extended_foot_recess_{direction}_{fin}',edge,.58,dark,8)
            tube('Base_'+side+f'_extended_foot_live_inlay_{direction}_{fin}',[(x,y,z+.52) for x,y,z in edge],.18,cyan,6)
    # socket dark back is recessed 6m behind mouth; opening unobstructed
    box('Base_'+side+'_intake_back',(sign*14,-25,19),(14,1.5,20),black,.25)
    for offset in [-1,1]:
        box('Base_'+side+f'_intake_jamb_{offset}',(sign*14+offset*8,-32,19),(2,14,24),metal,.25)
        tube('Base_'+side+f'_port_edge_{offset}',[(sign*14+offset*6.6,-38.6,9),(sign*14+offset*6.6,-38.6,29)],.3,cyan,7)
    box('Base_'+side+'_port_header',(sign*14,-32,31),(18,14,3),bronze,.25)
    box('Base_'+side+'_port_threshold',(sign*14,-32,7.5),(18,14,3),bronze,.25)
    for zz in [12,18,24]:box('Base_'+side+f'_recessed_contact_{zz}',(sign*14,-26,zz),(9,.7,1.0),cyan,.1)
    # Curved shell hood bridges the socket into the soaring inside blade, not a rectangular block.
    hood=[(33,4,-39,24,-26),(35,7,-32,25,-23),(43,12,-27,26,-20),(60,17,-24,27,-18),(90,21,-22,28,-16),(145,24,-21,29,-15),(230,25.7,-20.2,29.5,-15)]
    hv=[]
    for z,xi,yf,xo,yb in hood:
        hv.extend([(sign*xi,yf,z),(sign*xo,yf,z),(sign*xo,yb,z),(sign*xi,yb,z)])
    hf=[(3,2,1,0),tuple((len(hood)-1)*4+j for j in range(4))]+[(k*4+j,k*4+(j+1)%4,(k+1)*4+(j+1)%4,(k+1)*4+j) for k in range(len(hood)-1) for j in range(4)]
    mesh('Base_'+side+'_swept_socket_to_blade_hood',hv,hf,ivory,.35)
    tube('Base_'+side+'_hood_inner_trim',[(sign*(xi+.3),yf-.3,z) for z,xi,yf,xo,yb in hood],.32,bronze,6)
    # insulated feed climbs behind the intake into the tower
    tube('Base_'+side+'_power_feed',[(sign*14,-25,26),(sign*16,-21,45),(sign*22,-18,78),(sign*24,-15,120)],1.25,dark,8)
    tube('Base_'+side+'_power_feed_cyan',[(sign*13,-25,26),(sign*15,-21,45),(sign*21,-18,78),(sign*23,-15,120)],.4,cyan,8)
# Root and metadata, all export meshes parented without transformed scale.
root=bpy.data.objects.new('ATMO_Projector_1600m',None);scene.collection.objects.link(root);root.empty_display_type='PLAIN_AXES';root.empty_display_size=30
root['asset_version']='1.2';root['body_footprint_m']=[80.0,80.0];root['nominal_complete_footprint_m']=[80.0,180.0];root['nominal_feet_extension_along_wall_m']=50.0;root['units']='meters';root['height_m']=1600.0;root['sphere_diameter_m']=45.0;root['sphere_pitch_m']=70.0;root['clear_sphere_gap_m']=25.0;root['shield_plane']='Blender X=0, YZ plane; Godot X=0';root['wall_axis']='Blender +Y; Godot -Z';root['origin']='wall top at center, Z=0 in Blender';root['sphere_centers_z_m']=SPHERE_Z
for ob in ASSET:ob.parent=root
# Evaluate final bounds, including actual modifiers.
bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get();mins=[1e9]*3;maxs=[-1e9]*3;tris=0
for ob in ASSET:
    ev=ob.evaluated_get(deps);me=ev.to_mesh();me.calc_loop_triangles();tris+=len(me.loop_triangles)
    for v in me.vertices:
        p=ev.matrix_world@v.co
        for k in range(3):mins[k]=min(mins[k],p[k]);maxs[k]=max(maxs[k],p[k])
    ev.to_mesh_clear()
report={'asset':'ATMO Projector','blender_version':bpy.app.version_string,'mesh_objects':len(ASSET),'evaluated_triangles':tris,'bounds_min_m':mins,'bounds_max_m':maxs,'dimensions_m':[maxs[i]-mins[i] for i in range(3)],'body_footprint_m':[80,80],'wall_width_m':100,'foot_extension_past_each_body_end_along_wall_m':50,'sphere_count':len(SPHERE_Z),'sphere_centers_z_m':SPHERE_Z,'sphere_diameter_m':45,'sphere_center_pitch_m':70,'clear_gap_between_sphere_envelopes_m':25,'split_clearance_m':gap,'notes':['Bounds include crown, base, intake and all emission trim.','No shield, beam, highway, wall, lights or cameras in GLB.']}
report['foot_extension_past_each_body_end_along_wall_m']=maxs[1]-40
root['complete_footprint_m']=report['dimensions_m'][:2]
root['feet_extension_past_body_end_along_wall_m']=maxs[1]-40
with open(os.path.join(OUT,'qa','build_geometry_report.json'),'w') as f:json.dump(report,f,indent=2)
print('ASSET_REPORT',json.dumps(report),flush=True)
# Optional context is excluded from export selection.
active=COLS['90_Render_Context_NOT_EXPORTED'];start=len(ASSET)
wall=box('OPTIONAL_100m_wall_width',(0,0,-50),(100,650,100),contextmat,1)
for yy in [-300,-200,-100,0,100,200,300]:box('Wall_expansion_joint',(0,yy,-.15),(99,.35,.3),dark,0)
# wall deck edge strips outside the asset footprint
for xx in [-48,48]:box('Wall_edge_cap',(xx,0,.15),(1.0,650,.3),bronze,.05)
for ob in ASSET[start:]:ob['context_only']=True
ASSET=ASSET[:start]
active=COLS['91_Cameras_Lights_NOT_EXPORTED']
def camera(name,loc,target,scale):
    dat=bpy.data.cameras.new(name);ob=bpy.data.objects.new(name,dat);active.objects.link(ob);ob.location=loc;ob.rotation_euler=(Vector(target)-ob.location).to_track_quat('-Z','Y').to_euler();dat.type='ORTHO';dat.ortho_scale=scale;dat.clip_end=20000;return ob
def light_obj(name,typ,loc,power,size,target):
    dat=bpy.data.lights.new(name,typ);dat.energy=power
    if typ=='AREA':dat.shape='DISK';dat.size=size
    ob=bpy.data.objects.new(name,dat);active.objects.link(ob);ob.location=loc;ob.rotation_euler=(Vector(target)-ob.location).to_track_quat('-Z','Y').to_euler();return ob
light_obj('Key sunlight','SUN',(400,-700,1500),3.0,0,(0,0,800)).data.angle=.18
light_obj('Cool rim','SUN',(-500,250,900),1.2,0,(0,0,800)).data.color=(.4,.68,1)
light_obj('Fill','SUN',(-500,-200,600),.5,0,(0,0,800))
scene.world.color=(.18,.22,.3);scene.world.use_nodes=True;scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.11,.17,.24,1);scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.5
scene.render.engine='CYCLES';scene.cycles.samples=64;scene.cycles.use_denoising=False
scene.render.image_settings.file_format='PNG';scene.render.film_transparent=False
scene.view_settings.view_transform='AgX'
views=[('01_full_height',(750,-1750,1150),(0,0,795),1750,720,1800),('02_emitter_detail',(100,-270,530),(0,0,495),165,1200,1200),('03_crown_detail',(120,-290,1570),(0,0,1510),220,1200,1200),('04_base_intake',(330,-320,250),(0,0,100),330,1200,1200),('05_wall_transition',(350,-400,340),(0,0,205),500,1100,1500)]
for name,loc,target,scale,w,h in views:camera(name,loc,target,scale)
scene.camera=bpy.data.objects['01_full_height']
# Save native scene before first early rendered deliverable.
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT,'atmosphere_shield_projector.blend'))
# Export only asset mesh plus root, standard right-handed glTF Y-up conversion.
bpy.ops.object.select_all(action='DESELECT');root.select_set(True)
for ob in ASSET:ob.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,'atmosphere_shield_projector.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_extras=True,export_cameras=False,export_lights=False)
# First actual geometry preview is the readable three-module close-up.
for idx in [3,4,0,1,2]:
    name,loc,target,scale,w,h=views[idx];scene.camera=bpy.data.objects[name];scene.render.resolution_x=w;scene.render.resolution_y=h;scene.render.resolution_percentage=100;scene.render.filepath=os.path.join(OUT,'previews',name+'.png')
    bpy.ops.render.render(write_still=True);print('PREVIEW_READY',scene.render.filepath,flush=True)
# Restore editable overview and save cameras/render settings.
scene.camera=bpy.data.objects['01_full_height'];scene.render.resolution_x=720;scene.render.resolution_y=1800
bpy.ops.object.select_all(action='DESELECT');root.select_set(True);bpy.context.view_layer.objects.active=root
for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D':
        area.spaces.active.region_3d.view_distance=1900;area.spaces.active.region_3d.view_location=(0,0,800)
        area.spaces.active.clip_end=10000
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT,'atmosphere_shield_projector.blend'))
print('BUILD_COMPLETE',flush=True)
