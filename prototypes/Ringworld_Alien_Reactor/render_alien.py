"""Render current authoritative .blend. Non-destructive to source geometry and source lighting."""
import bpy,os
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Ringworld_Tripod_Reactor_Blockout.blend'))
sc=bpy.context.scene;sc.cycles.use_denoising=False;sc.cycles.max_bounces=4;sc.cycles.volume_bounces=0
sc.camera=bpy.data.objects['CAM_01 | entrance reveal'];sc.cycles.samples=128;sc.render.resolution_percentage=80
sc.render.filepath=os.path.join(ROOT,'preview_01_entrance_reveal.png');bpy.ops.render.render(write_still=True);print('ALIEN_HERO_READY',flush=True)
bpy.data.objects['Preview atmosphere | non-export'].hide_render=True
sc.camera=bpy.data.objects['CAM_04 | reactor structure'];sc.cycles.samples=128
sc.render.filepath=os.path.join(ROOT,'preview_03_reactor_detail.png');bpy.ops.render.render(write_still=True);print('ALIEN_DETAIL_READY',flush=True)
