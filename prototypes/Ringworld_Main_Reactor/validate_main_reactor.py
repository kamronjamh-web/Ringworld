"""Geometric audit of architecture, exhaust cylinder and main entry approach."""
import bpy,os,json,math
from mathutils import Vector
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Ringworld_Main_Reactor.blend'))
arch=[o for o in bpy.data.objects if o.type=='MESH' and any(c.name[:2] in [f'{i:02d}' for i in range(1,13)] for c in o.users_collection)]
def clip(poly,axis,bound,keepabove):
    out=[]
    if not poly:return out
    for a,b in zip(poly,poly[1:]+poly[:1]):
        ina=(a[axis]>=bound) if keepabove else (a[axis]<=bound)
        inb=(b[axis]>=bound) if keepabove else (b[axis]<=bound)
        if ina:out.append(a)
        if ina!=inb:
            t=(bound-a[axis])/(b[axis]-a[axis]);out.append(a+(b-a)*t)
    return out

def radial_min(poly):
    if not poly:return float('inf')
    signs=[];minimum=float('inf')
    for a,b in zip(poly,poly[1:]+poly[:1]):
        ax,ay=a.x,a.y;bx,by=b.x,b.y;dx,dy=bx-ax,by-ay
        signs.append(ax*by-ay*bx)
        l=dx*dx+dy*dy;t=max(0,min(1,-(ax*dx+ay*dy)/l)) if l else 0
        minimum=min(minimum,math.hypot(ax+t*dx,ay+t*dy))
    if abs(sum(signs))>1e-6 and (all(s>=-1e-8 for s in signs) or all(s<=1e-8 for s in signs)):return 0
    return minimum
beam_min=1e99;beam_object=None;beam_offenders=[];walk_offenders=[];triangles=0;nonfinite=[]
# The walkway's architectural profile is 24 m wide; its broad flat top is 18 m.
# Test an 8 m-wide, 2.1 m-high center approach corridor above the route surface.
for o in arch:
    o.data.calc_loop_triangles()
    for t in o.data.loop_triangles:
        poly=[o.matrix_world@o.data.vertices[i].co for i in t.vertices];triangles+=1
        if any(not math.isfinite(a) for p in poly for a in p):nonfinite.append(o.name)
        q=clip(clip(poly,2,600,True),2,2078,False)
        if q:
            r=radial_min(q)
            if r<beam_min:beam_min=r;beam_object=o.name
            if r<150-1e-4:beam_offenders.append(o.name)
        if o.name.startswith(('ENTRY | continuous','ENTRY | dark','ENTRY | inset','Arrival |','ENTRY | core docking','ENTRY | core vestibule')):continue
        q=poly
        for ax,lo,hi in [(0,-4,4),(1,-2780,-545),(2,162.31,164.41)]:q=clip(clip(q,ax,lo,True),ax,hi,False)
        if q:walk_offenders.append(o.name)
fig=[o for o in bpy.data.objects if o.name.startswith('Scale figure')]
figz=[(o.matrix_world@v.co).z for o in fig for v in o.data.vertices]
report={'architecture_mesh_objects':len(arch),'triangles_before_export':triangles,'nonfinite_geometry_objects':sorted(set(nonfinite)),'all_architecture_unit_scale':all(all(abs(s-1)<1e-6 for s in o.scale) for o in arch),'exhaust_validation_z_metres':[600,2078],'required_clear_radius_metres':150,'minimum_actual_clear_radius_metres':beam_min,'limiting_object':beam_object,'exhaust_intersection_objects':sorted(set(beam_offenders)),'entry_center_corridor_test':{'width_metres':8,'height_metres':2.1,'y_range_metres':[-2780,-545],'z_range_metres':[162.31,164.41],'obstructing_objects':sorted(set(walk_offenders))},'reference_person_height_metres':max(figz)-min(figz),'test_limits':'Entry center-corridor geometry audit is not collision, navigation or player-controller verification. Secondary damaged route is deliberately discontinuous. Test geometry is not exported.'}
with open(os.path.join(ROOT,'geometry_validation.json'),'w') as f:json.dump(report,f,indent=2)
print(json.dumps(report,indent=2))
assert not beam_offenders,sorted(set(beam_offenders))
assert not walk_offenders,sorted(set(walk_offenders))
assert not nonfinite
