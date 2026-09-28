extends SceneTree
const OUTPUT := "res://.godot/sunshine_far_depth_gpu"
var failed := false
func _initialize() -> void: call_deferred("_run")
func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)); var result := await _far_depth_probe(); if failed or result.size()!=3: return
	print("RING_FAR_DEPTH_GPU_TEST=PASS ", JSON.stringify({"far":result,"output":OUTPUT})); quit(0)
func _far_depth_probe()->Dictionary:
	var viewport:=SubViewport.new(); viewport.own_world_3d=true; viewport.size=Vector2i(256,144); viewport.render_target_update_mode=SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var producer:CompositorEffect=(load("res://scripts/render/RingFarDepthCapture.gd") as Script).new(); producer.set("coordinate_scale",.001)
	var environment:=Environment.new(); environment.background_mode=Environment.BG_COLOR
	var world:=WorldEnvironment.new(); world.environment=environment; world.compositor=Compositor.new(); world.compositor.compositor_effects=[producer]; viewport.add_child(world)
	var camera:=Camera3D.new(); camera.current=true; camera.near=.001; camera.far=100.; viewport.add_child(camera)
	var box:=MeshInstance3D.new(); var mesh:=BoxMesh.new(); mesh.size=Vector3(4,4,1); box.mesh=mesh; box.position=Vector3(0,0,-10); var material:=StandardMaterial3D.new(); material.shading_mode=BaseMaterial3D.SHADING_MODE_UNSHADED; box.material_override=material; viewport.add_child(box)
	var carrier:=TextureRect.new(); carrier.texture=viewport.get_texture(); carrier.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT); root.add_child(carrier)
	for frame in 10: await RenderingServer.frame_post_draw
	var texture:Texture2D=producer.call("get_depth_texture") as Texture2D; if texture==null: _fail("far depth producer did not publish"); return {}
	var image:=texture.get_image(); if image==null or image.is_empty(): _fail("far depth GPU readback failed"); return {}
	var centre:=image.get_pixel(image.get_width()/2,image.get_height()/2).r; var sky:=image.get_pixel(2,2).r
	if image.save_png("%s/far_depth_256.png" % OUTPUT)!=OK: _fail("256 depth capture failed"); return {}
	if centre < 9400. or centre > 9600.: _fail("far physical depth incorrect: %f" % centre); return {}
	if sky >= .001: _fail("far sky depth was not zero: %f" % sky); return {}
	viewport.size=Vector2i(320,180); for frame in 10: await RenderingServer.frame_post_draw
	texture=producer.call("get_depth_texture") as Texture2D; if texture==null: _fail("far depth producer lost publication on resize"); return {}
	image=texture.get_image(); if image==null or image.is_empty(): _fail("far depth GPU readback failed after resize"); return {}
	var resized:=image.get_pixel(image.get_width()/2,image.get_height()/2).r
	if image.save_png("%s/far_depth_320.png" % OUTPUT)!=OK: _fail("320 depth capture failed"); return {}
	if resized < 9400. or resized > 9600.: _fail("far physical depth incorrect after resize: %f" % resized); return {}
	world.compositor.compositor_effects=[]; carrier.queue_free(); viewport.queue_free(); return {"occluder_distance":centre,"sky_distance":sky,"resized_occluder_distance":resized}
func _fail(message:String)->void: push_error(message); failed=true; quit(1)
