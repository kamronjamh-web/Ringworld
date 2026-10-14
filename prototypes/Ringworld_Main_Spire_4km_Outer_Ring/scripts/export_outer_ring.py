"""Export the current editable outer-ring revision as one combined GLB."""
import bpy,os
ROOT=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if not bpy.data.filepath or os.path.basename(bpy.data.filepath)!='Main_Spire_Outer_Ring.blend':bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Main_Spire_Outer_Ring.blend'))
PREFIXES=('00_','10_','11_','12_','20_','21_','22_','23_','24_','25_','31_','32_','33_','34_','35_','36_')
obs=[]
for c in bpy.data.collections:
    if c.name.startswith(PREFIXES):
        c.hide_viewport=False;obs.extend(o for o in c.objects if o.type=='MESH')
root=bpy.data.objects['MAIN_SPIRE_OUTER_RING_ASSEMBLY'];obs=list(dict.fromkeys(obs))+[root]+list(root.children)
bpy.ops.object.select_all(action='DESELECT')
for o in obs:o.hide_set(False);o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.gltf(filepath=os.path.join(ROOT,'Main_Spire_Outer_Ring_Godot.glb'),export_format='GLB',use_selection=True,export_apply=True,export_yup=True,export_cameras=False,export_lights=False,export_extras=True)
print('EXPORTED',len(obs),'nodes')
