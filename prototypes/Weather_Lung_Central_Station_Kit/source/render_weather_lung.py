"""Render the saved Blender model. Use blender -b ASSET.blend --python this.py -- OUTPUT_DIR."""
import bpy,pathlib,sys
out=pathlib.Path(sys.argv[sys.argv.index('--')+1]);out.mkdir(parents=True,exist_ok=True)
s=bpy.context.scene;env=bpy.data.collections['90_PREVIEW_ONLY_Landscape_Water_Clouds']
def set_phase(p):f=1+240*p;s.frame_set(int(f),subframe=f-int(f));bpy.context.view_layer.update()
set_phase(.64)  # mid-breath pose: vanes open, gills partly flared
for name,cam in [('01_Wide_Actual_Blender','Camera_01_Wide_Actual_Blender'),('02_Intake_Bore','Camera_02_Intake_Bore'),('03_Architecture_Top','Camera_03_Architecture_Top'),('04_Maintenance_Gallery','Camera_04_Maintenance_Gallery'),('05_Turbine_Throat','Camera_05_Turbine_Throat'),('06_Arch_Gills','Camera_06_Arch_Gills')]:
 s.camera=bpy.data.objects[cam];env.hide_render=(name=='03_Architecture_Top');s.cycles.samples=48 if name.startswith('01_') else 32;s.render.filepath=str(out/(name+'.png'));bpy.ops.render.render(write_still=True)
env.hide_render=False;s.camera=bpy.data.objects['Camera_02_Intake_Bore'];rx,ry,sm=s.render.resolution_x,s.render.resolution_y,s.cycles.samples;s.render.resolution_x,s.render.resolution_y,s.cycles.samples=720,500,16
for k,ph in enumerate((0,.17,.33,.5,.67,.83)):set_phase(ph);s.render.filepath=str(out/f'breath_strip_{k:02d}.png');bpy.ops.render.render(write_still=True)
s.render.resolution_x,s.render.resolution_y,s.cycles.samples=rx,ry,sm;set_phase(0);s.camera=bpy.data.objects['Camera_01_Wide_Actual_Blender']
print('RENDERS_COMPLETE',flush=True)
