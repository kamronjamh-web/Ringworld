import bpy, os, json, math
from mathutils import Vector
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Surface_Beam_Spire.blend'))
c=bpy.data.collections['00_SURFACE_SPIRE_MACHINE_EXPORT']
# Distance of projected triangle to vertical origin. Checking only vertices would miss faces crossing the beam.
def edge_dist(a,b):
    dx=b[0]-a[0]; dy=b[1]-a[1]; ll=dx*dx+dy*dy
    if ll<1e-16: return math.hypot(*a)
    t=max(0,min(1,-(a[0]*dx+a[1]*dy)/ll))
    return math.hypot(a[0]+t*dx,a[1]+t*dy)
def tri_dist(v):
    a,b,c=v
    def cr(a,b):return a[0]*b[1]-a[1]*b[0]
    area=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
    ss=[cr(a,b),cr(b,c),cr(c,a)]
    if abs(area)>1e-10 and (all(x>=-1e-12 for x in ss) or all(x<=1e-12 for x in ss)): return 0
    return min(edge_dist(a,b),edge_dist(b,c),edge_dist(c,a))
minimum=1e99; culprit=''; tris=0; coords=[]; invalid=[]
for o in c.objects:
    o.data.calc_loop_triangles(); coords.extend(o.matrix_world@v.co for v in o.data.vertices)
    for t in o.data.loop_triangles:
        v=[o.matrix_world@o.data.vertices[i].co for i in t.vertices]; d=tri_dist([(p.x,p.y) for p in v]); tris+=1
        if d<minimum: minimum=d; culprit=o.name
        if d<75-1e-4: invalid.append((o.name,d))
armobjs=[o for o in c.objects if 'Faceted_Monolith' in o.name]
report={'height_m':max(p.z for p in coords)-min(p.z for p in coords),'z_min_m':min(p.z for p in coords),'z_max_m':max(p.z for p in coords),'bounds_x_m':[min(p.x for p in coords),max(p.x for p in coords)],'bounds_y_m':[min(p.y for p in coords),max(p.y for p in coords)],'arm_count':len(armobjs),'foot_center_radii_m':[o['foot_center_radius_m'] for o in sorted(armobjs,key=lambda o:o.name)],'arm_azimuth_degrees':[o['azimuth_degrees'] for o in sorted(armobjs,key=lambda o:o.name)],'minimum_machine_surface_radius_m':minimum,'limiting_mesh':culprit,'reserved_beam_radius_m':75,'intersecting_triangles':invalid,'clear_beam_path':len(invalid)==0,'triangles_checked':tris,'machine_mesh_objects':len(c.objects),'machine_vertices':sum(len(o.data.vertices) for o in c.objects)}
assert len(armobjs)==8
assert report['height_m']==1800
assert not invalid
with open(os.path.join(ROOT,'geometry_validation.json'),'w') as f: json.dump(report,f,indent=2)
print(json.dumps(report,indent=2))
