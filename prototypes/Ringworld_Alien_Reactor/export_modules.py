"""Export a small reusable assembly kit from the authoritative .blend. Preserve original source transforms."""
import bpy, os, json
from mathutils import Matrix, Vector
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Ringworld_Tripod_Reactor_Blockout.blend'))
OUT=os.path.join(ROOT,'modules');os.makedirs(OUT,exist_ok=True)
modules={
 'Reactor_Vessel': ([o for o in bpy.data.collections['01_REACTOR_PRESSURE_VESSEL'].objects],(0,0,0)),
 'Suspension_Buttress': ([o for o in bpy.data.collections['02_THREE_SUSPENSION_BUTTRESSES'].objects if o.name.startswith('B1')],(0,0,0)),
 'Wall_Anchor_and_Lift': ([o for o in bpy.data.collections['05_LIFTS_AND_WALL_ANCHORS'].objects if o.name.startswith(('Anchor 1','Lift 1'))],(0,1255,0)),
 'Arrival_Overlook': ([o for o in bpy.data.collections['06_ENTRANCE_AND_HUMAN_SCALE'].objects],(0,-1385,80)),
 'Exhaust_Cavern_Segment': ([o for o in bpy.data.collections['07_EXHAUST_LINK_TO_FUTURE_SURFACE'].objects],(0,0,301)),
}
manifest={}
for name,(objs,pivot) in modules.items():
 bpy.ops.object.select_all(action='DESELECT');saved={o:o.matrix_world.copy() for o in objs};T=Matrix.Translation(-Vector(pivot))
 for o in objs:o.matrix_world=T@o.matrix_world;o.select_set(True)
 bpy.context.view_layer.update()
 bpy.ops.export_scene.gltf(filepath=os.path.join(OUT,name+'.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True,export_materials='EXPORT')
 for o,m in saved.items():o.matrix_world=m
 manifest[name]={'objects':len(objs),'source_pivot_blender_xyz':pivot,'module_file':name+'.glb'}
with open(os.path.join(OUT,'module_manifest.json'),'w') as f:json.dump(manifest,f,indent=2)
print('MODULES_READY',flush=True)
