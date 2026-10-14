"""Independent geometry inspection of the combined spire and hollow base facility."""
import bpy, os, json, math
from mathutils import Vector
from mathutils.bvhtree import BVHTree
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Surface_Spire_with_Facility.blend'))
collections=[c for c in bpy.data.collections if c.name.startswith(('00_','10_','11_','12_'))]
objects=[o for c in collections for o in c.objects if o.type=='MESH']; objects=list(dict.fromkeys(objects))
def edge_dist(a,b):
    dx=b[0]-a[0]; dy=b[1]-a[1]; ll=dx*dx+dy*dy
    if ll<1e-16:return math.hypot(*a)
    t=max(0,min(1,-(a[0]*dx+a[1]*dy)/ll)); return math.hypot(a[0]+t*dx,a[1]+t*dy)
def tri_dist(v):
    a,b,c=v; area=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]); ss=[a[0]*b[1]-a[1]*b[0],b[0]*c[1]-b[1]*c[0],c[0]*a[1]-c[1]*a[0]]
    if abs(area)>1e-10 and (all(x>=-1e-12 for x in ss) or all(x<=1e-12 for x in ss)):return 0
    return min(edge_dist(a,b),edge_dist(b,c),edge_dist(c,a))
minrad=1e30; culprit=''; violations=[]; coords=[]; idx=[]; names=[]; triangles=0
for o in objects:
    o.data.calc_loop_triangles(); offset=len(coords); coords.extend(o.matrix_world@v.co for v in o.data.vertices)
    for tri in o.data.loop_triangles:
        vv=[o.matrix_world@o.data.vertices[i].co for i in tri.vertices]; d=tri_dist([(p.x,p.y) for p in vv]); triangles+=1
        idx.append(tuple(offset+i for i in tri.vertices)); names.append(o.name)
        if d<minrad:minrad=d;culprit=o.name
        if d<75-1e-4:violations.append({'mesh':o.name,'radius_m':d})
bvh=BVHTree.FromPolygons(coords,idx,all_triangles=True)
report={'reserved_beam_radius_m':75,'minimum_surface_radius_m':minrad,'limiting_mesh':culprit,'beam_intersections':violations,'clear_beam_path':not violations,'triangle_count':triangles,'mesh_objects':len(objects),'bounds_min_m':[min(p[i] for p in coords) for i in range(3)],'bounds_max_m':[max(p[i] for p in coords) for i in range(3)],'validation_scope':'Triangle-level cylindrical beam clearance; measured bounds. Route audit is geometric only, not gameplay/navigation/collision testing.'}
# Optional route coordinates provided by the facility builder. Each route is sampled on its centerline.
routefile=os.path.join(ROOT,'facility_route_samples.json')
if os.path.exists(routefile):
    routes=json.load(open(routefile)); results=[]
    for route in routes:
        failures=[]; checked=0
        for j,p in enumerate(route['points']):
            p=Vector(p); checked+=1
            floor=bvh.ray_cast(p+Vector((0,0,.25)),Vector((0,0,-1)),.7)
            if floor[0] is None:failures.append({'sample':j,'kind':'floor_not_found','point':list(p)})
            head=bvh.ray_cast(p+Vector((0,0,.15)),Vector((0,0,1)),1.85)
            if head[0] is not None:failures.append({'sample':j,'kind':'standing_headroom_block','mesh':names[head[2]],'point':list(p)})
            close=bvh.find_nearest(p+Vector((0,0,.95)),.35)
            if close[0] is not None:failures.append({'sample':j,'kind':'body_clearance_under_0p35m','mesh':names[close[2]],'point':list(p)})
        results.append({'route':route['name'],'samples':checked,'pass':not failures,'failures':failures})
    report['sampled_routes']=results
json.dump(report,open(os.path.join(ROOT,'facility_validation.json'),'w'),indent=2)
print(json.dumps(report,indent=2))
assert not violations, 'Machine geometry intersects reserved beam cylinder.'
