extends "res://tools/cloud_terrain_history_gpu_test.gd"

# Actual main compute atlas descriptors and direct-light response. The separate
# producer fixture covers spatial atlas generation and canonical frame mapping.
func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	_build_world()
	if failed: return
	opaque_plane.visible = false
	clouds.far_opaque_depth_texture = null
	clouds.accumulation_decay = 0.0
	clouds.diagnostic_menu_history_weights = false
	clouds.point_lights_data = []
	clouds.directional_lights_data = [Vector4(0,0,-1,1), Vector4(1,1,1,1)]
	clouds.lights_updated = true
	var radius := 5000000.0
	var profile: Array[Vector4] = [Vector4(TAU*radius,100000,-50000,50000), Vector4(1,1,2,1),
		Vector4(1,1,32000,0), Vector4(0,1,0,0), Vector4(radius,0,0,0)]
	var frame: Array[Vector4] = [Vector4(1,0,0,radius), Vector4(0,1,0,50000),
		Vector4(0,0,1,0), Vector4(radius,0,0,TAU*radius)]
	clouds.terrain_cloud_sun_profile = profile
	clouds.terrain_cloud_sun_canonical_frame = frame
	clouds.terrain_cloud_sun_regional_texture = _visibility_texture(1.0)
	clouds.terrain_cloud_sun_global_texture = _visibility_texture(1.0)
	await _draws(8)
	var lit := await _read_cloud_output(1)
	if failed: return
	clouds.terrain_cloud_sun_global_texture = _visibility_texture(0.0)
	clouds.request_history_reset()
	await _draws(4)
	var blocked := await _read_cloud_output(1)
	if failed: return
	var changed := 0; var alpha_max := 0.0; var lit_sum := 0.0; var blocked_sum := 0.0
	for y in lit.get_height():
		for x in lit.get_width():
			var a := lit.get_pixel(x,y); var b := blocked.get_pixel(x,y)
			alpha_max = maxf(alpha_max, absf(a.a-b.a))
			if a.a > 0.05:
				lit_sum += a.r+a.g+a.b; blocked_sum += b.r+b.g+b.b
				if a.r+a.g+a.b > b.r+b.g+b.b + 0.01: changed += 1
	if alpha_max > 0.00001 or changed < 100 or blocked_sum <= 0.0 or blocked_sum >= lit_sum:
		return _fail("atlas direct-light/density/ambient contract failed: " + JSON.stringify({"alpha_max":alpha_max,"changed":changed,"lit":lit_sum,"blocked":blocked_sum}))
	lit.save_png(ProjectSettings.globalize_path(OUTPUT.path_join("atlas_lit.png")))
	blocked.save_png(ProjectSettings.globalize_path(OUTPUT.path_join("atlas_blocked.png")))
	print("CLOUD_TERRAIN_SUN_LIGHTING_GPU_TEST=PASS changed=",changed," alpha_max=",alpha_max)
	viewport_3d.queue_free(); await process_frame; await process_frame
	quit(0)

func _visibility_texture(value: float) -> ImageTexture:
	var image := Image.create(1,1,false,Image.FORMAT_RGF)
	image.set_pixel(0,0,Color(value,1,0,1))
	return ImageTexture.create_from_image(image)
