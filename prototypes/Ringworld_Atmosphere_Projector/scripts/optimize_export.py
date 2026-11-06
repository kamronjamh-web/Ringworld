import bpy, os, json
from mathutils import Vector
OUT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(OUT,'atmosphere_shield_projector.blend'))
root=bpy.data.objects['ATMO_Projector_1600m'];deps=bpy.context.evaluated_depsgraph_get()
asset=[ob for ob in bpy.data.objects if ob.type=='MESH' and ob.parent==root]
materials=[]
for ob in asset:
    for m in ob.data.materials:
        if m and m not in materials: materials.append(m)
buckets={-1:{'v':[],'f':[],'m':[],'n':[]},1:{'v':[],'f':[],'m':[],'n':[]}}
for ob in asset:
    ev=ob.evaluated_get(deps);me=ev.to_mesh();world=ev.matrix_world
    sign=1 if sum((world@v.co).x for v in me.vertices)>0 else -1
    dst=buckets[sign];start=len(dst['v']);dst['v'].extend([tuple(world@v.co) for v in me.vertices])
    for p in me.polygons:
        dst['f'].append(tuple(start+i for i in p.vertices));dst['m'].append([m.name for m in materials].index(me.materials[p.material_index].name))
        nm=world.to_3x3().inverted().transposed()
        for li in p.loop_indices:dst['n'].append(tuple((nm@me.corner_normals[li].vector).normalized()))
    ev.to_mesh_clear()
col=bpy.data.collections.new('EXPORT_TEMPORARY_BATCHED');bpy.context.scene.collection.children.link(col)
exp_root=bpy.data.objects.new('AtmosphereShieldProjector',None);col.objects.link(exp_root)
for key in root.keys():exp_root[key]=root[key]
export_objects=[exp_root]
for sign,b in buckets.items():
    name='TowerNegativeX' if sign<0 else 'TowerPositiveX'
    mesh=bpy.data.meshes.new(name);mesh.from_pydata(b['v'],[],b['f']);mesh.update()
    for m in materials:mesh.materials.append(m)
    for p,i in zip(mesh.polygons,b['m']):p.material_index=i;p.use_smooth=True
    mesh.normals_split_custom_set(b['n'])
    ob=bpy.data.objects.new(name,mesh);col.objects.link(ob);ob.parent=exp_root;export_objects.append(ob)
    ob['half_space']='X < 0' if sign<0 else 'X > 0';ob['draw_call_strategy']='Batched by material; editable parts remain in source .blend.'
for sign in [-1,1]:
    ob=bpy.data.objects.new('PowerIntakeSocket_'+('NegativeX' if sign<0 else 'PositiveX'),None);col.objects.link(ob);ob.location=(sign*14,-39,19);ob.parent=exp_root;ob['connection_direction_blender']='-Y';ob['connection_direction_godot']='+Z';export_objects.append(ob)
bpy.ops.object.select_all(action='DESELECT')
for ob in export_objects:ob.select_set(True)
bpy.context.view_layer.objects.active=exp_root
bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,'atmosphere_shield_projector.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_extras=True,export_cameras=False,export_lights=False)
print('BATCHED_EXPORT_COMPLETE',len(export_objects),len(materials))
