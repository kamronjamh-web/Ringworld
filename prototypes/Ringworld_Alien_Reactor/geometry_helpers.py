"""Mesh and material construction helpers for build_alien.py. Executed in the builder context."""
def place(o,name,mat=None,col=None):
    o.name=name
    for c in list(o.users_collection):c.objects.unlink(o)
    (col or CUR).objects.link(o)
    if mat:o.data.materials.append(mat)
    return o

def mat(name,color,metal=0,rough=.5,emit=None,strength=0):
    m=bpy.data.materials.new(name);m.diffuse_color=(*color,1);m.use_nodes=True
    p=m.node_tree.nodes.get('Principled BSDF');p.inputs['Base Color'].default_value=(*color,1);p.inputs['Metallic'].default_value=metal;p.inputs['Roughness'].default_value=rough
    if emit:p.inputs['Emission Color'].default_value=(*emit,1);p.inputs['Emission Strength'].default_value=strength
    return m
metal=mat('M01 | Carbon titanium / main armor',(0.055,.072,.082),.65,.44)
edge=mat('M02 | Brushed pale structural alloy',(.23,.28,.285),.6,.36)
concrete=mat('M03 | Warm grey engineering ceramic',(.28,.25,.20),.15,.73)
black=mat('M04 | Recess / graphite',(.008,.015,.02),.25,.7)
red=mat('M05 | Oxidized copper service panels',(.26,.088,.035),.5,.62)
teal=mat('M06 | Live coolant / restrained teal',(.03,.36,.34),.35,.3,(.07,.8,.68),4.2)
white=mat('M07 | Task-light / warm white',(.6,.67,.59),.1,.35,(.76,1,.85),5)
amber=mat('M08 | Fault / amber',(.5,.17,.015),.15,.4,(1,.28,.025),3)
rock=mat('R01 | Reference rock / warm basalt',(.135,.073,.04),0,.94)
p=rock.node_tree.nodes.get('Principled BSDF');nt=rock.node_tree
tex=nt.nodes.new('ShaderNodeTexNoise');tex.inputs['Scale'].default_value=.021;tex.inputs['Detail'].default_value=3.3;tex.inputs['Roughness'].default_value=.8
coord=nt.nodes.new('ShaderNodeTexCoord');nt.links.new(coord.outputs['Object'],tex.inputs['Vector'])
ramp=nt.nodes.new('ShaderNodeValToRGB');ramp.color_ramp.elements[0].position=.16;ramp.color_ramp.elements[0].color=(.035,.018,.009,1);ramp.color_ramp.elements[1].position=.85;ramp.color_ramp.elements[1].color=(.23,.14,.079,1)
nt.links.new(tex.outputs['Fac'],ramp.inputs[0]);nt.links.new(ramp.outputs[0],p.inputs['Base Color'])
bump=nt.nodes.new('ShaderNodeBump');bump.inputs['Strength'].default_value=.55;bump.inputs['Distance'].default_value=4;nt.links.new(tex.outputs['Fac'],bump.inputs['Height']);nt.links.new(bump.outputs[0],p.inputs['Normal'])

def cube(name,loc,dim,ma=metal,bevel=0,col=None):
    x,y,z=[v/2 for v in dim]
    v=[(-x,-y,-z),(-x,-y,z),(-x,y,-z),(-x,y,z),(x,-y,-z),(x,-y,z),(x,y,-z),(x,y,z)]
    f=[(0,4,6,2),(1,3,7,5),(0,1,5,4),(2,6,7,3),(0,2,3,1),(4,5,7,6)]
    d=bpy.data.meshes.new(name+' Mesh');d.from_pydata(v,[],f);d.update()
    o=bpy.data.objects.new(name,d);(col or CUR).objects.link(o);o.location=loc
    if ma:d.materials.append(ma)
    if bevel:
        m=o.modifiers.new('Small manufactured edge radius','BEVEL');m.width=bevel;m.segments=1
        o.modifiers.new('Weighted corner normals','WEIGHTED_NORMAL')
    return o

def cyl(name,loc,r,depth,ma=metal,verts=48,r2=None):
    if r2 is None:bpy.ops.mesh.primitive_cylinder_add(vertices=verts,radius=r,depth=depth,location=loc)
    else:bpy.ops.mesh.primitive_cone_add(vertices=verts,radius1=r,radius2=r2,depth=depth,location=loc)
    return place(bpy.context.object,name,ma)

def mesh(name,v,f,ma=metal,col=None):
    d=bpy.data.meshes.new(name+' Mesh');d.from_pydata(v,[],f);d.update();o=bpy.data.objects.new(name,d);(col or CUR).objects.link(o);d.materials.append(ma);return o

def ring(name,ro,ri,z,h,ma=metal,n=72,start=0,end=2*pi):
    full=abs(end-start-2*pi)<.001;v=[];f=[]
    for j in range(n+1):
        a=start+(end-start)*j/n
        for r,zz in [(ro,z-h/2),(ri,z-h/2),(ro,z+h/2),(ri,z+h/2)]:v.append((r*cos(a),r*sin(a),zz))
    for j in range(n):
        k=j*4;q=k+4;f.extend([(k,q,q+2,k+2),(k+1,k+3,q+3,q+1),(k+2,q+2,q+3,k+3),(k+1,q+1,q,k)])
    if not full:f.extend([(0,2,3,1),(4*n,4*n+1,4*n+3,4*n+2)])
    return mesh(name,v,f,ma)

def beam(name,a,b,width,depth,ma=metal,bevel=0):
    a,b=Vector(a),Vector(b);o=cube(name,(a+b)/2,(width,depth,(b-a).length),ma,bevel);o.rotation_euler=(b-a).to_track_quat('Z','Y').to_euler();return o

def radial(a,r,z=0,t=0):return (r*cos(a)-t*sin(a),r*sin(a)+t*cos(a),z)
def rbox(name,a,r,z,dim,ma=metal,t=0,bevel=0):
    o=cube(name,radial(a,r,z,t),dim,ma,bevel);o.rotation_euler.z=a;return o

def sector_profile(name,a,width,profile,thick,ma=metal):
    v=[];f=[]
    for r,z in profile:
        for rr,aa in [(r,a-width/2),(r,a+width/2),(r-thick,a-width/2),(r-thick,a+width/2)]:v.append((rr*cos(aa),rr*sin(aa),z))
    for j in range(len(profile)-1):
        k=j*4;q=k+4;f.extend([(k,q,q+1,k+1),(k+2,k+3,q+3,q+2),(k,k+2,q+2,q),(k+1,q+1,q+3,k+3)])
    f.extend([(0,1,3,2),(len(v)-4,len(v)-2,len(v)-1,len(v)-3)])
    return mesh(name,v,f,ma)

