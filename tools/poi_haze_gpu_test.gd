extends SceneTree

## POI architecture shader contract (ring_poi_architecture*.gdshader). Needs a GPU renderer: run WINDOWED.
## Run:  Godot_v4.8-dev7_mono_win64.exe --path . --script res://tools/poi_haze_gpu_test.gd [-- --model=legacy]
## --model=legacy selects the Legacy haze (atmosphere_haze_model = 0); the default is Physical.
const SIZE := 160
const OUT := "res://.godot/poi_haze_gpu"
const BACKGROUND := Color(0.015, 0.02, 0.03, 1.0)
const SHADER := "res://materials/ring_poi_architecture.gdshader"
const SHADER_DS := "res://materials/ring_poi_architecture_double_sided.gdshader"
const IDENTITY_TOLERANCE := 2.0 / 255.0
## The shader owns its light() (local-day gate), so the standalone fallback is Lambert + the ported GGX rather than Godot's Burley + GGX: a few levels of difference against StandardMaterial3D.
const STANDARD_TOLERANCE := 8.0 / 255.0
var physical := false
var failed := false
var done := false
var viewport: SubViewport
var camera: Camera3D
var holder: MeshInstance3D

const PROBE_SHADER := """
shader_type spatial;
render_mode unshaded, cull_disabled;
#include "res://materials/ring_atmosphere.gdshaderinc"
uniform int probe_mode = 0;
varying vec3 probe_world;
void vertex() { probe_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
	RingHaze haze = ring_haze_sample(ring_atmosphere_camera_world_position, probe_world, vec3(0.0), 1.0);
	ALBEDO = probe_mode == 0 ? ring_haze_inscatter(haze) : haze.T;
}
"""

func _initialize() -> void:
	physical = not ("--model=legacy" in OS.get_cmdline_user_args())
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(180.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	var sphere := SphereMesh.new(); sphere.radius = 6.0; sphere.height = 12.0; sphere.radial_segments = 48; sphere.rings = 24
	holder.mesh = sphere
	holder.position = Vector3(0, 0, -20)
	var background := await _capture_clear("background")

	# 1. identity_matches_standard (no frame, and haze disabled with a frame).
	var cases := [
		{"name": "Obsidian_Alloy", "albedo": Color(0.09, 0.1, 0.13), "metallic": 0.8, "roughness": 0.35, "emission": Color(0, 0, 0), "energy": 1.0, "cull": BaseMaterial3D.CULL_DISABLED},
		{"name": "Mirror_Solar", "albedo": Color(0.7, 0.72, 0.75), "metallic": 1.0, "roughness": 0.04, "emission": Color(0, 0, 0), "energy": 1.0, "cull": BaseMaterial3D.CULL_DISABLED},
		{"name": "Reactor_Teal_Inlay", "albedo": Color(0.05, 0.12, 0.12), "metallic": 0.2, "roughness": 0.5, "emission": Color(0.05, 0.28, 0.22), "energy": 1.785, "cull": BaseMaterial3D.CULL_DISABLED},
		{"name": "WL_cull_back", "albedo": Color(0.4, 0.35, 0.3), "metallic": 0.0, "roughness": 0.8, "emission": Color(0, 0, 0), "energy": 1.0, "cull": BaseMaterial3D.CULL_BACK},
	]
	for c in cases:
		var std := StandardMaterial3D.new()
		std.albedo_color = c.albedo; std.metallic = c.metallic; std.roughness = c.roughness; std.cull_mode = c.cull
		if c.emission != Color(0, 0, 0):
			std.emission_enabled = true; std.emission = c.emission; std.emission_energy_multiplier = c.energy
		holder.material_override = std
		var ref := await _capture(c.name + "_standard")
		var poi := _poi_material(c.albedo, c.metallic, c.roughness, c.emission, c.energy, c.cull == BaseMaterial3D.CULL_DISABLED)
		holder.material_override = poi
		var noframe := await _capture(c.name + "_noframe")
		var d1 := _max_diff(ref, noframe)
		if d1 > STANDARD_TOLERANCE: _fail("identity_matches_standard %s no frame max diff %.5f > %.5f" % [c.name, d1, IDENTITY_TOLERANCE]); return
		_configure(poi)
		poi.set_shader_parameter("ring_atmosphere_enabled", false)
		var disabled := await _capture(c.name + "_disabled")
		var d2 := _max_diff(ref, disabled)
		if d2 > STANDARD_TOLERANCE: _fail("identity_matches_standard %s haze disabled max diff %.5f > %.5f" % [c.name, d2, IDENTITY_TOLERANCE]); return
		print("identity %s: noframe=%.5f disabled=%.5f" % [c.name, d1, d2])

	# 2. haze_changes_rgb and 3. zero_density_identity at 30 km.
	holder.position = Vector3(0, 0, -30000)
	var poi2 := _poi_material(Color(0.4, 0.35, 0.3), 0.3, 0.5, Color(0, 0, 0), 1.0, true)
	_configure(poi2)
	holder.material_override = poi2
	poi2.set_shader_parameter("ring_atmosphere_enabled", false)
	var off30 := await _capture("haze30_off")
	poi2.set_shader_parameter("ring_atmosphere_enabled", true)
	var on30 := await _capture("haze30_on")
	var change := _max_diff(off30, on30)
	if change < 0.01: _fail("haze_changes_rgb: 30 km enabled vs disabled differ by only %.5f" % change); return
	_set_density(poi2, true)
	var zero30 := await _capture("haze30_zero")
	var zd := _max_diff(off30, zero30)
	if zd > 1.0 / 255.0: _fail("zero_density_identity: zero density vs disabled max diff %.5f" % zd); return
	_set_density(poi2, false)
	print("haze_changes_rgb=%.4f zero_density_diff=%.5f" % [change, zd])

	# 4. black_inscatter_matches_probe.
	var black := _poi_material(Color(0, 0, 0), 0.0, 1.0, Color(0, 0, 0), 1.0, true)
	black.set_shader_parameter("specular_value", 0.0)
	_configure(black)
	var probe := _probe_material(0)
	_configure(probe)
	for km in [2.0, 10.0, 50.0, 200.0]:
		holder.position = Vector3(0, 0, -km * 1000.0)
		holder.material_override = black
		var got := await _capture("black_%dkm" % int(km))
		holder.material_override = probe
		var want := await _capture("probe_%dkm" % int(km))
		var d := _max_diff(got, want)
		print("black_inscatter %d km: max diff %.5f (probe center %s)" % [int(km), d, want.get_pixel(SIZE / 2, SIZE / 2)])
		if d > IDENTITY_TOLERANCE: _fail("black_inscatter_matches_probe at %d km: max diff %.5f" % [int(km), d]); return

	# 5. emission_attenuated at 50 km and 200 km.
	var tprobe := _probe_material(1)
	_configure(tprobe)
	var emitter := _poi_material(Color(0, 0, 0), 0.0, 1.0, Color(0.5, 0.5, 0.5), 1.0, true)
	emitter.set_shader_parameter("specular_value", 0.0)
	_configure(emitter)
	for km in [50.0, 200.0]:
		holder.position = Vector3(0, 0, -km * 1000.0)
		holder.material_override = black
		var base := await _capture("emis_black_%d" % int(km))
		holder.material_override = emitter
		var lit := await _capture("emis_lit_%d" % int(km))
		emitter.set_shader_parameter("ring_atmosphere_enabled", false)
		var unhazed := await _capture("emis_unhazed_%d" % int(km))
		emitter.set_shader_parameter("ring_atmosphere_enabled", true)
		holder.material_override = tprobe
		var tcap := await _capture("emis_T_%d" % int(km))
		var c := SIZE / 2
		var delta_g := lit.get_pixel(c, c).srgb_to_linear().g - base.get_pixel(c, c).srgb_to_linear().g
		var e0 := unhazed.get_pixel(c, c).srgb_to_linear().g
		var tg := tcap.get_pixel(c, c).srgb_to_linear().g
		print("emission %d km: delta=%.4f unhazed=%.4f T.g=%.4f expected=%.4f (lit %s)" % [int(km), delta_g, e0, tg, tg * e0, lit.get_pixel(c, c).srgb_to_linear()])
		if delta_g > e0 + 0.01: _fail("emission_attenuated: hazed emissive delta %.4f exceeds unhazed %.4f at %d km" % [delta_g, e0, int(km)]); return
		if absf(delta_g - tg * e0) > 0.05 * maxf(tg * e0, 0.02) + 0.012: _fail("emission_attenuated: delta %.4f vs T.g*E %.4f at %d km" % [delta_g, tg * e0, int(km)]); return

	# 6. back_face_lit.
	holder.position = Vector3(0, 0, -20)
	var quad := QuadMesh.new(); quad.size = Vector2(14, 14)
	holder.mesh = quad
	holder.rotation = Vector3(0, PI, 0)  # faces +Z, away from the camera: only its back face is visible.
	var std_ds := StandardMaterial3D.new(); std_ds.albedo_color = Color(0.5, 0.4, 0.3); std_ds.cull_mode = BaseMaterial3D.CULL_DISABLED
	holder.material_override = std_ds
	var back_ref := await _capture("back_standard")
	var ds := _poi_material(Color(0.5, 0.4, 0.3), 0.0, 1.0, Color(0, 0, 0), 1.0, true)
	holder.material_override = ds
	var back_poi := await _capture("back_double_sided")
	var bd := _max_diff(back_ref, back_poi)
	var one := _poi_material(Color(0.5, 0.4, 0.3), 0.0, 1.0, Color(0, 0, 0), 1.0, false)
	holder.material_override = one
	var back_one := await _capture("back_cull_back")
	holder.rotation = Vector3.ZERO
	if bd > STANDARD_TOLERANCE: _fail("back_face_lit: double-sided vs StandardMaterial CULL_DISABLED max diff %.5f" % bd); return
	if _max_diff(back_one, background) > IDENTITY_TOLERANCE: _fail("back_face_lit: cull_back variant drew a back face"); return
	if _max_diff(back_poi, background) < 0.05: _fail("back_face_lit: back face drew nothing"); return
	# 7. cloud shadow: with a far cascade map the surface darkens (ambient AO and the primary sun).
	holder.mesh = sphere
	holder.position = Vector3(0, 0, -30000)
	var shaded := _poi_material(Color(0.4, 0.35, 0.3), 0.3, 0.5, Color(0, 0, 0), 1.0, true)
	_configure(shaded)
	holder.material_override = shaded
	shaded.set_shader_parameter("ring_atmosphere_enabled", false)
	# Local daylight on and at full day (the ring centre lies on the sun side), so the primary sun is shadowed too.
	shaded.set_shader_parameter("poi_ring_lighting_valid", true)
	shaded.set_shader_parameter("poi_ring_center_world", Vector3(-5000000.0, 0.0, 0.0))
	var cs_off := await _capture("cloudshadow_off")
	var map := Image.create(4, 4, false, Image.FORMAT_RG8)
	map.fill(Color(1, 0, 0, 1))
	shaded.set_shader_parameter("local_cloud_shadow_far_texture", ImageTexture.create_from_image(map))
	shaded.set_shader_parameter("local_cloud_shadow_enabled", true)
	shaded.set_shader_parameter("local_cloud_shadow_far_enabled", true)
	shaded.set_shader_parameter("local_cloud_shadow_far_radius_meters", 200000.0)
	shaded.set_shader_parameter("local_cloud_shadow_far_strength", 0.6)
	var cs_on := await _capture("cloudshadow_on")
	var cs_delta := _luma_ratio(cs_on, cs_off)
	print("cloud_shadow: shadowed/unshadowed foreground luminance %.3f" % cs_delta)
	if cs_delta > 0.9 or cs_delta < 0.1: _fail("cloud_shadow_darkens: luminance ratio %.3f (expected a clear but not total darkening)" % cs_delta); return
	# 8. local daylight: the sun is full overhead at one ring centre and fully set at the opposite one; the night side keeps only ambient, so it is clearly darker.
	shaded.set_shader_parameter("local_cloud_shadow_enabled", false)
	shaded.set_shader_parameter("poi_ring_center_world", Vector3(-5000000.0, 0.0, 0.0))
	var day_img := await _capture("daylight_day")
	shaded.set_shader_parameter("poi_ring_center_world", Vector3(5000000.0, 0.0, 0.0))
	var night_img := await _capture("daylight_night")
	var night_ratio := _luma_ratio(night_img, day_img)
	print("daylight: night/day foreground luminance %.3f" % night_ratio)
	if night_ratio > 0.6: _fail("local_daylight_gate: night/day luminance ratio %.3f (the sun must drop out after local sunset)" % night_ratio); return
	print("POI_HAZE_GPU=PASS model=%s haze30_change=%.4f back_diff=%.5f cloud_shadow_ratio=%.3f" % ["physical" if physical else "legacy", change, bd, cs_delta])
	done = true
	quit(0)

func _make_scene() -> void:
	viewport = SubViewport.new(); viewport.size = Vector2i(SIZE, SIZE); viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var world := WorldEnvironment.new(); var environment := Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = BACKGROUND; environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color(0.6, 0.65, 0.7); environment.ambient_light_energy = 0.5; environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR; world.environment = environment; viewport.add_child(world)
	camera = Camera3D.new(); camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 16.0; camera.near = 0.05; camera.far = 400000.0; camera.current = true; viewport.add_child(camera); camera.look_at(Vector3(0, 0, -100), Vector3.UP)
	# toSun = LEFT (-X): the light travels along +X; the camera sees the lit left side of every object.
	var light := DirectionalLight3D.new(); light.light_energy = 1.0; light.shadow_enabled = false; viewport.add_child(light); light.look_at_from_position(Vector3.ZERO, Vector3.RIGHT, Vector3.UP)
	holder = MeshInstance3D.new(); viewport.add_child(holder)

func _poi_material(albedo: Color, metallic: float, roughness: float, emission: Color, energy: float, double_sided: bool) -> ShaderMaterial:
	var m := ShaderMaterial.new(); m.shader = load(SHADER_DS if double_sided else SHADER)
	m.set_shader_parameter("albedo_color", albedo); m.set_shader_parameter("metallic_value", metallic); m.set_shader_parameter("roughness_value", roughness)
	m.set_shader_parameter("emission_color", emission); m.set_shader_parameter("emission_energy", energy)
	return m

func _probe_material(mode: int) -> ShaderMaterial:
	var shader := Shader.new(); shader.code = PROBE_SHADER
	var m := ShaderMaterial.new(); m.shader = shader; m.set_shader_parameter("probe_mode", mode)
	return m

func _configure(material: ShaderMaterial) -> void:
	material.set_shader_parameter("ring_atmosphere_ray_frame_valid", true); material.set_shader_parameter("ring_atmosphere_camera_world_position", Vector3.ZERO); material.set_shader_parameter("ring_atmosphere_camera_height_meters", 300.0 if physical else 0.0); material.set_shader_parameter("ring_atmosphere_outward_world", Vector3.RIGHT); material.set_shader_parameter("ring_atmosphere_axis_world", Vector3.UP); material.set_shader_parameter("ring_atmosphere_sidewall_negative_meters", 1000000.0); material.set_shader_parameter("ring_atmosphere_sidewall_positive_meters", 1000000.0); material.set_shader_parameter("ring_atmosphere_coordinate_scale", 1.0); material.set_shader_parameter("ring_atmosphere_to_sun", Vector3.LEFT)
	material.set_shader_parameter("ring_radius_meters", 5000000.0); material.set_shader_parameter("atmosphere_clear_distance_meters", 1000.0); material.set_shader_parameter("atmosphere_full_distance_meters", 2000.0); material.set_shader_parameter("atmosphere_boundary_max_opacity", .35); material.set_shader_parameter("atmosphere_upper_max_opacity", .1); material.set_shader_parameter("atmosphere_distance_haze_max_opacity", .65); material.set_shader_parameter("atmosphere_total_max_opacity", .8); material.set_shader_parameter("atmosphere_scattering_energy", .26)
	material.set_shader_parameter("atmosphere_haze_model", 1 if physical else 0)

func _set_density(material: ShaderMaterial, zero: bool) -> void:
	material.set_shader_parameter("atmosphere_boundary_max_opacity", 0.0 if zero else .35)
	material.set_shader_parameter("atmosphere_upper_max_opacity", 0.0 if zero else .1)
	material.set_shader_parameter("atmosphere_distance_haze_max_opacity", 0.0 if zero else .65)
	material.set_shader_parameter("ring_haze_sigma_R", Vector3.ZERO if zero else Vector3(5.802e-6, 13.558e-6, 33.1e-6))
	material.set_shader_parameter("ring_haze_sigma_A", 0.0 if zero else 4.175e-6)
	material.set_shader_parameter("ring_haze_sigma_M0", 0.0 if zero else 4.0e-6)

func _capture_clear(label: String) -> Image:
	holder.visible = false
	var image := await _capture(label)
	holder.visible = true
	return image

func _capture(label: String) -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty(): _fail("empty capture " + label); return Image.new()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT)); image.save_png(OUT.path_join(label + ".png"))
	return image

func _max_diff(a: Image, b: Image) -> float:
	var worst := 0.0
	for y in range(SIZE):
		for x in range(SIZE):
			var ca := a.get_pixel(x, y); var cb := b.get_pixel(x, y)
			worst = maxf(worst, maxf(absf(ca.r - cb.r), maxf(absf(ca.g - cb.g), absf(ca.b - cb.b))))
	return worst

## Mean linear luminance of the pixels that differ from BACKGROUND in b, a / b.
func _luma_ratio(a: Image, b: Image) -> float:
	var sum_a := 0.0; var sum_b := 0.0
	for y in range(SIZE):
		for x in range(SIZE):
			var cb := b.get_pixel(x, y)
			if absf(cb.r - BACKGROUND.r) + absf(cb.g - BACKGROUND.g) + absf(cb.b - BACKGROUND.b) > 0.02:
				sum_a += a.get_pixel(x, y).srgb_to_linear().get_luminance(); sum_b += cb.srgb_to_linear().get_luminance()
	return sum_a / maxf(sum_b, 0.000001)

func _fail(message: String) -> void:
	failed = true; done = true; push_error("POI_HAZE_GPU=FAIL " + message); quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
