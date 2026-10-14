"""Refresh previews without regenerating or modifying the authoring scene.
Run: blender -b -t 8 -P render_main_reactor.py [-- --hero-only]
"""
import bpy,os,sys
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Ringworld_Main_Reactor.blend'))
sc=bpy.context.scene;sc.cycles.use_denoising=False;sc.cycles.max_bounces=4;sc.cycles.volume_bounces=0
cave=bpy.data.collections['80_CAVE_SDF_REFERENCE_ONLY'];fig=bpy.data.collections['92_HUMAN_SCALE_REFERENCE_ONLY']
bpy.data.objects['Preview atmosphere | non-export'].hide_render=True
sc.render.resolution_x=1800;sc.render.resolution_percentage=100
if '--hero-only' not in sys.argv:
 cave.hide_render=True;fig.hide_render=True;sc.camera=bpy.data.objects['MAIN_CAMERA | architecture cutaway'];sc.cycles.samples=64
 sc.render.resolution_y=1400;sc.render.filepath=os.path.join(ROOT,'preview_02_architecture_cutaway.png');bpy.ops.render.render(write_still=True)
if '--cutaway-only' not in sys.argv:
 cave.hide_render=False;fig.hide_render=False;sc.camera=bpy.data.objects['MAIN_CAMERA | central installation'];sc.cycles.samples=96
 sc.render.resolution_y=1200;sc.render.filepath=os.path.join(ROOT,'preview_01_entrance_reveal.png');bpy.ops.render.render(write_still=True)
print('MAIN_REACTOR_PREVIEWS_READY',flush=True)
