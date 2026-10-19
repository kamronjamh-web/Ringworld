extends SceneTree

const SHADER := preload("res://materials/ring_cloud_transmittance_mask.gdshader")
var viewport: SubViewport
var material: ShaderMaterial
var failed := false

func _init() -> void:
	call_deferred("run")
	create_timer(30).timeout.connect(func(): fail("watchdog expired"))

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("Forward+ GPU required")
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	viewport = SubViewport.new()
	viewport.size = Vector2i(96, 96)
	viewport.transparent_bg = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	material = ShaderMaterial.new()
	material.shader = SHADER
	var rect := ColorRect.new()
	rect.size = Vector2(96, 96)
	rect.material = material
	viewport.add_child(rect)
	for pair in [["camera_canonical_km", Vector3(0,.1,0)], ["camera_forward_canonical", Vector3(0,0,1)], ["camera_tan_half_fov", .04], ["viewport_aspect", 1.0], ["volume_center_canonical_km", Vector3(0,5,25)], ["volume_size_meters", Vector3(20000,10000,50000)], ["coverage", 1.0], ["storm_influence", 1.0], ["cloud_base_fraction", 0.0], ["cloud_top_fraction", 1.0], ["handoff_start_meters", 45000.0], ["handoff_end_meters", 50000.0], ["raymarch_steps", 24], ["sunlit_color", Color.WHITE], ["shadow_color", Color.WHITE], ["storm_color", Color.WHITE], ["night_color", Color.WHITE]]:
		material.set_shader_parameter(pair[0], pair[1])
	# Aim upward through the band, using exactly the production ray basis.
	material.set_shader_parameter("camera_forward_canonical", Vector3(0,.15,1).normalized())
	var off := await capture()
	var profile: Array[Vector4] = []
	profile.resize(16)
	for slot in 16: profile[slot] = Vector4.ZERO
	profile[0] = Vector4(1,5000000,2500,2500)
	profile[1] = Vector4(20000,60000,300000,.95)
	profile[2] = Vector4(.2,.08,.9,1)
	profile[3] = Vector4(0,3000,0,3000)
	profile[4] = Vector4(1500,12000,0,0)
	profile[5] = Vector4(.1,.2,.3,0)
	profile[6] = Vector4(.1,.2,.3,1)
	profile[7] = Vector4(0,-1,0,100)
	profile[8] = Vector4(0,0,1,159000)
	profile[9] = Vector4(0,1,0,159000)
	# `-- --model=physical` exercises slots 10-15 (physical haze); the zero-density step then zeroes every extinction instead of the Legacy caps.
	var physical := "--model=physical" in OS.get_cmdline_user_args()
	if physical:
		profile[10] = Vector4(1, 1, 5.802e-6, 13.558e-6)
		profile[11] = Vector4(33.1e-6, 8000, 4.175e-6, .9)
		profile[12] = Vector4(1500, 4.0e-6, .95, 400)
		profile[13] = Vector4(.6, 8, 1, 12.566)
		profile[14] = Vector4(12.566, 12.566, .1855, .203)
		profile[15] = Vector4(.2275, .102, .12, .156)
	material.set_shader_parameter("ring_cloud_atmosphere_profile", profile)
	var on := await capture()
	profile[2] = Vector4(0,0,0,1)
	if physical:
		profile[10] = Vector4(1, 1, 0, 0)
		profile[11] = Vector4(0, 8000, 0, .9)
		profile[12] = Vector4(1500, 0, .95, 400)
	material.set_shader_parameter("ring_cloud_atmosphere_profile", profile)
	var zero := await capture()
	profile[0].x = 0.0
	material.set_shader_parameter("ring_cloud_atmosphere_profile", profile)
	var disabled := await capture()
	var rgb_delta := 0.0
	var alpha_delta := 0.0
	var zero_delta := 0.0
	var body_pixels := 0
	for y in 96:
		for x in 96:
			var a := off.get_pixel(x,y)
			var b := on.get_pixel(x,y)
			var c := zero.get_pixel(x,y)
			var d := disabled.get_pixel(x,y)
			alpha_delta = maxf(alpha_delta, absf(a.a-b.a))
			zero_delta = maxf(zero_delta, absf(a.r-c.r)+absf(a.g-c.g)+absf(a.b-c.b)+absf(a.a-c.a))
			zero_delta = maxf(zero_delta, absf(a.r-d.r)+absf(a.g-d.g)+absf(a.b-d.b)+absf(a.a-d.a))
			if a.a > .1:
				body_pixels += 1
				rgb_delta += absf(a.r-b.r)+absf(a.g-b.g)+absf(a.b-b.b)
	if body_pixels < 100 or rgb_delta / maxf(1,body_pixels) < .03: return fail("haze did not attenuate visible production cloud contributions")
	if alpha_delta > .00001: return fail("haze changed mask density/alpha")
	if zero_delta > .00001: return fail("zero-density profile changed baseline")
	print("FALLBACK_CLOUD_MASK_ATMOSPHERE=PASS model=%s body_pixels=%s alpha_delta=%s zero_delta=%s rgb_delta=%s" % ["physical" if physical else "legacy",body_pixels,alpha_delta,zero_delta,rgb_delta / maxf(1,body_pixels)])
	viewport.queue_free()
	await process_frame
	await process_frame
	if not failed: quit(0)

func capture() -> Image:
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

func fail(message: String) -> void:
	if failed: return
	failed = true
	if is_instance_valid(viewport): viewport.queue_free()
	push_error("FALLBACK_CLOUD_MASK_ATMOSPHERE=FAIL " + message)
	quit(1)
