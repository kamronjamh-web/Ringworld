"""Render the outer-arm spire; presentation-only visibility never changes the saved asset."""
import bpy,os,sys
ROOT=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Main_Spire_Outer_Ring.blend'));s=bpy.context.scene
args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else ['hero','entry']
PREFIX=('00_','10_','11_','12_','20_','21_','22_','23_','24_','25_','31_','32_','33_','34_','35_','36_')
s.cycles.use_denoising=False;s.cycles.use_adaptive_sampling=True;s.cycles.adaptive_threshold=.055;s.render.resolution_percentage=100;s.render.image_settings.file_format='PNG';s.render.image_settings.color_mode='RGB'
for mode in args:
    for c in bpy.data.collections:
        c.hide_render=False
        for o in c.objects:o.hide_render=False
    s.camera=bpy.data.objects['Camera_Outer_Ring_Hero'];s.cycles.samples=48;s.render.resolution_x=1440;s.render.resolution_y=1920;file='01_outer_ring_spire_hero.png'
    if mode=='draft':s.cycles.samples=12;s.render.resolution_x=720;s.render.resolution_y=1000;file='draft_outer_ring_full_height.png'
    elif mode=='entry':
        for c in bpy.data.collections:
            if c.name.startswith(('11_','20_','21_','24_','25_','31_','33_','35_','36_')):c.hide_render=True
        for o in bpy.data.collections['00_SURFACE_SPIRE_MACHINE_EXPORT'].objects:o.hide_render=not o.name.startswith(('Foot_','Ground_'))
        s.camera=bpy.data.objects['Camera_Outer_Entrance_Cutaway'];s.cycles.samples=32;s.render.resolution_x=1600;s.render.resolution_y=1150;file='02_outer_arm_entrance_cutaway.png'
    elif mode=='entry_draft':
        for c in bpy.data.collections:
            if c.name.startswith(('11_','20_','21_','24_','25_','31_','33_','35_','36_')):c.hide_render=True
        for o in bpy.data.collections['00_SURFACE_SPIRE_MACHINE_EXPORT'].objects:o.hide_render=not o.name.startswith(('Foot_','Ground_'))
        s.camera=bpy.data.objects['Camera_Outer_Entrance_Cutaway'];s.cycles.samples=12;s.render.resolution_x=960;s.render.resolution_y=690;file='draft_outer_entrance_cutaway.png'
    elif mode=='entrance_exterior':
        # Presentation ground is flush to the declared ground datum for the doorway view.
        ground=bpy.data.objects.get('Presentation_Ground')
        if ground:
            for v in ground.data.vertices:
                if abs(v.co.z+2.1)<.001:v.co.z=-.08
        s.camera=bpy.data.objects['Camera_Outer_Entrance_Exterior'];s.cycles.samples=32;s.render.resolution_x=1440;s.render.resolution_y=1000;file='03_outer_arm_entrance_exterior.png'
    s.render.filepath=os.path.join(ROOT,'previews',file);bpy.ops.render.render(write_still=True);print('RENDER_READY',file,flush=True)
