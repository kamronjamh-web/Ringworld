"""Presentation-only cameras and lighting for the completed hollow facility."""
import bpy, os, math, sys, json
from mathutils import Vector
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Surface_Spire_with_Facility.blend'))
s=bpy.context.scene
machine=bpy.data.collections['00_SURFACE_SPIRE_MACHINE_EXPORT']; roof=[c for c in bpy.data.collections if c.name.startswith('11_')]
preview=bpy.data.collections['90_PRESENTATION_ONLY_DO_NOT_EXPORT']; rig=bpy.data.collections['92_CAMERAS_LIGHTS']; beam=bpy.data.collections['91_BEAM_PREVIEW_ONLY_DO_NOT_EXPORT']
beam.hide_render=True; beam.hide_viewport=True
# Avoid coplanar overlap with the facility's ground-floor slab.
for v in bpy.data.objects['Presentation_Ground_Not_Exported'].data.vertices:
    if abs(v.co.z)<1e-6: v.co.z=-.08
for o in list(rig.objects):
    if o.name.startswith(('Facility_Light_','Camera_Facility_')): bpy.data.objects.remove(o,do_unlink=True)
def polar(r,a,z):return (r*math.cos(math.radians(a)),r*math.sin(math.radians(a)),z)
def cam(name,p,t,lens=28,ortho=None):
    d=bpy.data.cameras.new(name);o=bpy.data.objects.new(name,d);rig.objects.link(o);o.location=p;o.rotation_euler=(Vector(t)-o.location).to_track_quat('-Z','Y').to_euler();d.lens=lens;d.clip_start=.05;d.clip_end=15000
    if ortho:d.type='ORTHO';d.ortho_scale=ortho
    return o
def area(name,p,power,size,color=(.68,.86,1)):
    d=bpy.data.lights.new(name,'AREA');d.energy=power;d.size=size;d.color=color;o=bpy.data.objects.new(name,d);rig.objects.link(o);o.location=p
for i in range(24):area(f'Facility_Light_Ring_{i:02d}',polar(156,i*15,9.4),1800,8)
for i,a in enumerate([22.5,112.5,202.5]):area(f'Facility_Light_Room_{i:02d}',polar(180,a,11.5),4800,18,(.8,.88,1))
for i in range(16):area(f'Facility_Light_Gallery_{i:02d}',polar(111,i*22.5,25.5),1500,9,(.55,.95,.9))
# A true 1.8 m presentation figure supplies human scale in the elevated gallery.
for o in list(preview.objects):
    if o.name.startswith('Gallery_Scale_'):bpy.data.objects.remove(o,do_unlink=True)
px,py,pz=polar(111,260,16)
for name in ['Human_1p8m_Torso','Human_1p8m_Head','Human_1p8m_Leg','Human_1p8m_Leg.001','Human_1p8m_Arm','Human_1p8m_Arm.001']:
    original=bpy.data.objects.get(name)
    if original:
        me=original.data.copy()
        for v in me.vertices:v.co.x-=316;v.co.y+=45
        ob=bpy.data.objects.new('Gallery_Scale_'+name,me);preview.objects.link(ob);ob.location=(px,py,pz);ob.rotation_euler.z=math.radians(100)
cutaway=cam('Camera_Facility_Cutaway',(420,-560,610),(0,0,3),ortho=560)
# Optional builder camera suggestions override the sensible initial gallery view.
camera_file=os.path.join(ROOT,'facility_camera_suggestions.json')
cfg=json.load(open(camera_file)) if os.path.exists(camera_file) else {}
eyep=cfg.get('eye_position',polar(118,275.625,17.7));eyet=cfg.get('eye_target',polar(108,250,18.0))
eye=cam('Camera_Facility_Eye_Level',eyep,eyet,lens=20)
hero=bpy.data.objects['Camera_Hero'];hero.data.lens=34;hero.rotation_euler=(Vector((0,0,850))-hero.location).to_track_quat('-Z','Y').to_euler()
s.render.engine='CYCLES';s.cycles.samples=48;s.cycles.use_denoising=False;s.cycles.use_adaptive_sampling=True;s.cycles.adaptive_threshold=.055
s.render.resolution_percentage=100;s.render.image_settings.file_format='PNG';s.render.image_settings.color_mode='RGB';s.view_layers[0].material_override=None
for c in roof:c.hide_render=False;c.hide_viewport=False
s.camera=hero
for name in ['build_facility.py','render_facility.py','verify_facility.py']:
    if os.path.exists(os.path.join(ROOT,name)):
        if bpy.data.texts.get(name):bpy.data.texts.remove(bpy.data.texts[name])
        bpy.data.texts.load(os.path.join(ROOT,name))
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Surface_Spire_with_Facility.blend'))
args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else ['cutaway','eye','hero']
for mode in args:
    for c in roof:c.hide_render=(mode=='cutaway')
    for o in machine.objects:o.hide_render=(mode=='cutaway' and not o.name.startswith(('Foot_','Ground_')))
    beam.hide_render=(mode!='eye')
    if mode=='cutaway':s.camera=cutaway;s.render.resolution_x=2000;s.render.resolution_y=1600;file='preview_06_facility_cutaway.png'
    elif mode=='eye':s.camera=eye;s.render.resolution_x=1800;s.render.resolution_y=1100;file='preview_07_facility_eye_level.png'
    else:s.camera=hero;s.render.resolution_x=1600;s.render.resolution_y=2100;file='preview_08_complete_surface_poi.png'
    s.render.filepath=os.path.join(ROOT,file);bpy.ops.render.render(write_still=True);print('RENDER_DONE',mode,flush=True)
