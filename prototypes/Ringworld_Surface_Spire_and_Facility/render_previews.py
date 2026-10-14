"""Render/refresh presentation views; never changes or re-exports the machine geometry."""
import bpy, os, sys, math
from mathutils import Vector
ROOT=os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.open_mainfile(filepath=os.path.join(ROOT,'Surface_Beam_Spire.blend'))
scene=bpy.context.scene
for name,power in [('Softbox_Key',90000000),('Softbox_Rim',65000000),('Softbox_Warm_Fill',30000000)]: bpy.data.lights[name].energy=power
beamcoll=bpy.data.collections['91_BEAM_PREVIEW_ONLY_DO_NOT_EXPORT']; beamcoll.hide_render=True; beamcoll.hide_viewport=True
base=bpy.data.objects['Camera_Base_Scale']; base.location=(430,-220,65); base.rotation_euler=(Vector((295,-15,23))-base.location).to_track_quat('-Z','Y').to_euler(); base.data.lens=38
# Ground itself is a non-exported reference. It sits precisely on the ground datum.
ground=bpy.data.objects['Presentation_Ground_Not_Exported']
for v in ground.data.vertices:
    if v.co.z==-1: v.co.z=0
platform=bpy.data.objects['Presentation_Scale_Platform']
platform.location.z=.1
for o in bpy.data.collections['90_PRESENTATION_ONLY_DO_NOT_EXPORT'].objects:
    if o.name.startswith('Human_') or o.name=='Scale_Cube_2m': o.location.z=.1
hero=bpy.data.objects['Camera_Hero']; hero.data.lens=34; hero.rotation_euler=(Vector((0,0,850))-hero.location).to_track_quat('-Z','Y').to_euler()
scene.camera=hero; scene.render.resolution_x=1600; scene.render.resolution_y=2100
scene.cycles.samples=80; scene.cycles.use_adaptive_sampling=True; scene.cycles.adaptive_threshold=.035; scene.cycles.use_denoising=False
scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGB'
scene.view_layers[0].material_override=None
# Slight restrained glow in the separate active-beam render only.
scene.use_nodes=True; nt=scene.node_tree; nt.nodes.clear(); layers=nt.nodes.new('CompositorNodeRLayers'); out=nt.nodes.new('CompositorNodeComposite'); glare=nt.nodes.new('CompositorNodeGlare'); glare.glare_type='FOG_GLOW'; glare.quality='HIGH'; glare.threshold=1.5; glare.mix=-.9; nt.links.new(layers.outputs['Image'],glare.inputs['Image']); nt.links.new(glare.outputs['Image'],out.inputs['Image'])
# Camera-only directional background: the uncapped shaft reads as deep darkness,
# while the sky and illumination retain their neutral blue-grey studio treatment.
wnt=scene.world.node_tree; oldbg=wnt.nodes.get('Background'); oldout=next(n for n in wnt.nodes if n.type=='OUTPUT_WORLD')
tex=wnt.nodes.new('ShaderNodeTexCoord'); sep=wnt.nodes.new('ShaderNodeSeparateXYZ'); ramp=wnt.nodes.new('ShaderNodeValToRGB')
ramp.color_ramp.elements[0].position=.62; ramp.color_ramp.elements[0].color=(.095,.12,.14,1)
ramp.color_ramp.elements[1].position=.97; ramp.color_ramp.elements[1].color=(.00005,.00008,.0001,1)
cam_bg=wnt.nodes.new('ShaderNodeBackground'); cam_bg.inputs['Strength'].default_value=.6
lp=wnt.nodes.new('ShaderNodeLightPath'); mix=wnt.nodes.new('ShaderNodeMixShader')
wnt.links.new(tex.outputs['Normal'],sep.inputs[0]); wnt.links.new(sep.outputs['Z'],ramp.inputs['Fac']); wnt.links.new(ramp.outputs['Color'],cam_bg.inputs['Color'])
wnt.links.new(lp.outputs['Is Camera Ray'],mix.inputs[0]); wnt.links.new(oldbg.outputs[0],mix.inputs[1]); wnt.links.new(cam_bg.outputs[0],mix.inputs[2]); wnt.links.new(mix.outputs[0],oldout.inputs[0])
# A simple open-ended shaft liner provides depth cues in the presentation only.
name='Presentation_Open_Shaft_Liner_Not_Exported'
if bpy.data.objects.get(name): bpy.data.objects.remove(bpy.data.objects[name],do_unlink=True)
N=64; ri=75/math.cos(math.pi/N)+.04; profile=[(ri,-750),(81,-750),(81,0),(ri,0)]; vv=[]; ff=[]
for r,z in profile:
    vv.extend((r*math.cos(i*math.tau/N),r*math.sin(i*math.tau/N),z) for i in range(N))
for k in range(4):
    for i in range(N): ff.append((k*N+i,k*N+(i+1)%N,((k+1)%4)*N+(i+1)%N,((k+1)%4)*N+i))
me=bpy.data.meshes.new(name+'_Mesh'); me.from_pydata(vv,[],ff); me.materials.append(bpy.data.materials['Deep_Recess']); me.update(); ob=bpy.data.objects.new(name,me); bpy.data.collections['90_PRESENTATION_ONLY_DO_NOT_EXPORT'].objects.link(ob)

# Save the final editable scene with no preview beam.
for name in ['build_surface_spire.py','render_previews.py','verify_geometry.py']:
    if bpy.data.texts.get(name): bpy.data.texts.remove(bpy.data.texts[name])
    bpy.data.texts.load(os.path.join(ROOT,name))
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT,'Surface_Beam_Spire.blend'))
args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else ['hero','crown','base','beam','top']
for mode in args:
    beamcoll.hide_render=(mode!='beam'); glare.mix=-.92
    scene.render.resolution_x=1600; scene.render.resolution_y=2100; scene.cycles.samples=80
    if mode=='top':scene.camera=bpy.data.objects['Camera_Top']; scene.render.resolution_x=1600; scene.render.resolution_y=1600
    elif mode=='crown':scene.camera=bpy.data.objects['Camera_Crown_Opening']; scene.render.resolution_x=1600; scene.render.resolution_y=1300
    elif mode=='base':scene.camera=bpy.data.objects['Camera_Base_Scale']; scene.render.resolution_x=1800; scene.render.resolution_y=1100
    elif mode=='neutral':scene.camera=bpy.data.objects['Camera_Neutral']; scene.view_layers[0].material_override=bpy.data.materials['Neutral_Silhouette']; scene.render.resolution_x=1080; scene.render.resolution_y=1500
    else:scene.camera=bpy.data.objects['Camera_Hero']
    if mode=='beam':glare.mix=-.8
    filenames={'hero':'preview_01_hero.png','top':'preview_02_top_aperture.png','crown':'preview_03_crown.png','base':'preview_04_human_scale.png','beam':'preview_05_active_beam.png','neutral':'preview_00_silhouette.png'}
    scene.render.filepath=os.path.join(ROOT,filenames[mode]); bpy.ops.render.render(write_still=True); print('RENDER_DONE',mode,flush=True)
    scene.view_layers[0].material_override=None
