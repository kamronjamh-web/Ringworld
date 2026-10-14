"""Small, dependency-free Blender mesh builders. Units are metres."""
import bpy, math
from mathutils import Vector
from math import sin, cos, pi

def mesh(name,vertices,faces,material,collection):
    data=bpy.data.meshes.new(name+' mesh'); data.from_pydata(vertices,[],faces); data.update()
    obj=bpy.data.objects.new(name,data); collection.objects.link(obj)
    if material: data.materials.append(material)
    return obj

def radial(a,r,z=0,t=0): return (r*cos(a)-t*sin(a),r*sin(a)+t*cos(a),z)

def blade(name,a,sections,material,collection):
    vertices=[]; faces=[]
    for r,z,w,h in sections:
        for t,dz in [(-w*.75,h/2),(w*.75,h/2),(w,h*.22),(w*.65,-h/2),(-w*.65,-h/2),(-w,h*.22)]: vertices.append(radial(a,r,z+dz,t))
    for j in range(len(sections)-1):
        for k in range(6): faces.append((j*6+k,j*6+(k+1)%6,(j+1)*6+(k+1)%6,(j+1)*6+k))
    faces += [tuple(range(5,-1,-1)),tuple((len(sections)-1)*6+k for k in range(6))]
    return mesh(name,vertices,faces,material,collection)

def petal(name,a,sections,material,collection):
    vertices=[]; faces=[]
    for ro,ri,w,z in sections:
        for r,aa in [(ro,a-w),(ro,a+w),(ri,a-w),(ri,a+w)]:vertices.append((r*cos(aa),r*sin(aa),z))
    for j in range(len(sections)-1):
        k=j*4;q=k+4;faces.extend([(k,q,q+1,k+1),(k+2,k+3,q+3,q+2),(k,k+2,q+2,q),(k+1,q+1,q+3,k+3)])
    faces.extend([(0,1,3,2),(len(vertices)-4,len(vertices)-2,len(vertices)-1,len(vertices)-3)])
    return mesh(name,vertices,faces,material,collection)

def ring(name,ro,ri,z,h,material,collection,n=32,start=0,end=2*pi):
    vertices=[];faces=[]
    for j in range(n+1):
        a=start+(end-start)*j/n
        for r,zz in [(ro,z-h/2),(ri,z-h/2),(ro,z+h/2),(ri,z+h/2)]:vertices.append((r*cos(a),r*sin(a),zz))
    for j in range(n):
        k=j*4;q=k+4;faces.extend([(k,q,q+2,k+2),(k+1,k+3,q+3,q+1),(k+2,q+2,q+3,k+3),(k+1,q+1,q,k)])
    # Seal all arcs; full rings weld the seam below.
    full=abs(end-start-2*pi)<.001
    if not full: faces.extend([(0,2,3,1),(4*n,4*n+1,4*n+3,4*n+2)])
    obj=mesh(name,vertices,faces,material,collection)
    if full:
        import bmesh
        bm=bmesh.new();bm.from_mesh(obj.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0001);bm.to_mesh(obj.data);bm.free()
    return obj

def profile(name,sections,material,collection,n=6,phase=0):
    vertices=[];faces=[]
    for r,z in sections:
        for k in range(n):
            a=phase+k*2*pi/n;vertices.append((r*cos(a),r*sin(a),z))
    for j in range(len(sections)-1):
        for k in range(n):faces.append((j*n+k,j*n+(k+1)%n,(j+1)*n+(k+1)%n,(j+1)*n+k))
    faces += [tuple(range(n-1,-1,-1)),tuple((len(sections)-1)*n+k for k in range(n))]
    return mesh(name,vertices,faces,material,collection)

def box(name,location,dimensions,material,collection,a=0):
    x,y,z=[d/2 for d in dimensions]
    vertices=[(-x,-y,-z),(-x,-y,z),(-x,y,-z),(-x,y,z),(x,-y,-z),(x,-y,z),(x,y,-z),(x,y,z)]
    faces=[(0,4,6,2),(1,3,7,5),(0,1,5,4),(2,6,7,3),(0,2,3,1),(4,5,7,6)]
    obj=mesh(name,vertices,faces,material,collection);obj.location=location;obj.rotation_euler.z=a;return obj
