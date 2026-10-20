extends SceneTree

## Shadow revamp S5a: receiver side of the terrain height-shadow clipmap (docs/shadow-revamp-plan.md S5, materials/ring_height_shadow.gdshaderinc).
## Synthetic RG16F Texture2DArrays are written straight into the ringworld_height_shadow_* globals (no producer node), a flat ground plane is viewed from above with
## an orthographic camera and ringworld_shadow_debug_mode 4 (2 for the vegetation product path), so a pixel is the receiver's heightfield term itself.
## Checks: step edge within one texel, penumbra width ~ G * tan(softening) within 20%, unresolved texels (G < 0) fall back to the previous path (OFF == ON pixel
## identical for every ported receiver), L0 -> L1 and L1 -> macro crossfades are monotonic and hit their endpoints, the eclipse-hub share scales the term (0.6),
## the authority rule drops blockers the sun map owns, a mid-crossfade layer pair, debug mode 4 vs the overlay shader classification, and every port (proxy,
## global x2, voxel, both oceans, Terrain3D/cards lighting include, ring_tree, imported_tree) shows the shadow in the shadowed half only.
##   godot --path . --script res://tools/height_shadow_receiver_gpu_test.gd      (windowed Vulkan, no other Godot process)
## Prints HEIGHT_SHADOW_RECEIVER=PASS or FAIL lines. The sea-level clamp unit test lives in tools/HeightShadowLatticeTest.cs (VerifySeaLevelClamp).

const SIZE := Vector2i(512, 64)
const SETTLE_FRAMES := 6
const SUN_DIR := Vector3(0.3, 0.9, 0.1)
const TEXTURE_SIZE := 1024
const GLOBAL_BOUNDS_L0 := Vector4(-2048.0, 2044.0, -2048.0, 2044.0)
const GLOBAL_BOUNDS_L1 := Vector4(-8192.0, 8176.0, -8192.0, 8176.0)
const EMPTY_BOUNDS := Vector4(1e9, -1e9, 1e9, -1e9)
const OVERLAY_SHADER := "res://materials/shadows/ring_height_shadow_debug_overlay.gdshader"

# name, shader, receiver (plane | sphere), debug mode used to read the term, params
const MATERIALS := [
	{"name": "voxel", "shader": "res://materials/voxel_ring_terrain.gdshader", "receiver": "plane", "mode": 4, "params": {}},
	{"name": "proxy", "shader": "res://materials/ring_terrain_proxy.gdshader", "receiver": "plane", "mode": 4, "params": {"geomorph_enabled": false}},
	{"name": "global_world", "shader": "res://materials/ring_terrain_global.gdshader", "receiver": "plane", "mode": 4, "params": {"regional_band_distance_clip": false, "terrain_macro_shadow_use_uv": false}},
	{"name": "global_uv", "shader": "res://materials/ring_terrain_global.gdshader", "receiver": "plane", "mode": 4, "params": {"regional_band_distance_clip": false, "terrain_macro_shadow_use_uv": true}},
	{"name": "ocean_proxy", "shader": "res://materials/ring_ocean_proxy.gdshader", "receiver": "plane", "mode": 4, "params": {"geomorph_enabled": false, "water_wind_clock_enabled": true}},
	{"name": "ocean_global", "shader": "res://materials/ring_ocean_global.gdshader", "receiver": "plane", "mode": 4, "params": {"water_wind_clock_enabled": true, "fade_start_meters": 0.0, "fade_end_meters": 0.01}},
	{"name": "terrain_lighting", "shader": "", "receiver": "plane", "mode": 4, "params": {}},
	{"name": "ring_tree", "shader": "res://materials/ring_tree.gdshader", "receiver": "sphere", "mode": 2, "params": {"vegetation_wind_enabled": false}},
	{"name": "imported_tree", "shader": "res://materials/imported_tree.gdshader", "receiver": "sphere", "mode": 2, "params": {"vegetation_wind_enabled": false}},
]

const TERRAIN_LIGHTING_CODE := """shader_type spatial;
render_mode cull_back;
uniform vec3 camera_world_position = vec3(0.0);
#include "res://materials/ring_terrain_lighting.gdshaderinc"
varying vec3 receiver_world;
void vertex() { receiver_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
	ALBEDO = vec3(1.0);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	AO = 0.0;
	AO_LIGHT_AFFECT = 0.0;
}
void light() {
	float direct_visibility;
	DIFFUSE_LIGHT += ring_terrain_direct_diffuse(NORMAL, LIGHT, ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR, VIEW_MATRIX, 1.0, 0.0, vec2(0.5),
		receiver_world, camera_world_position, SCREEN_UV, FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, RING_SHADOW_DEFAULT, direct_visibility);
}
"""

var failures: Array[String] = []
var checks := 0
var _viewport: SubViewport
var _camera: Camera3D
var _sun: DirectionalLight3D
var _plane: MeshInstance3D
var _sphere: MeshInstance3D
var _srgb := true
var _out_dir := "res://.godot/s5_receiver"
var _macro_texture: ImageTexture


func _init() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="):
			_out_dir = arg.substr(6)
	call_deferred("_run")
	create_timer(600.0).timeout.connect(func() -> void:
		push_error("HEIGHT_SHADOW_RECEIVER=FAIL timeout")
		quit(1))


func _check(condition: bool, message: String) -> void:
	checks += 1
	if not condition:
		failures.append(message)
		push_error("HEIGHT_SHADOW_RECEIVER check failed: " + message)


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("HEIGHT_SHADOW_RECEIVER=FAIL requires Vulkan/Forward+ (do not run headless)")
		quit(1)
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_out_dir))
	_build_scene()
	RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", 0.0)
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 4)
	var voxel := _material(MATERIALS[0])
	_show(MATERIALS[0], voxel)

	# Calibration: R = bias, G > 0 gives v = 0.5 exactly (h_rel 0, slope 0).
	_frame_plane(80.0)
	_set_globals(_layers_uniform(0.5, 100.0), _layers_uniform(0.5, 100.0))
	var half := await _row(0.0, 0.0)
	_srgb = half > 0.6
	print("HEIGHT_SHADOW_RECEIVER calibration: v=0.5 reads %.3f (%s)" % [half, "sRGB output" if _srgb else "linear output"])
	_check(absf(_linear(half) - 0.5) < 0.03, "calibration v=0.5 decoded %f" % _linear(half))

	await _test_step_edge(voxel)
	await _test_penumbra(voxel)
	await _test_unresolved_and_ports()
	await _test_unresolved_partial()
	await _test_l0_l1(voxel)
	await _test_l1_macro(voxel)
	await _test_hub(voxel)
	await _test_authority(voxel)
	await _test_crossfade(voxel)
	await _test_overlay(voxel)

	print("HEIGHT_SHADOW_RECEIVER checks=%d failures=%d" % [checks, failures.size()])
	if failures.is_empty():
		print("HEIGHT_SHADOW_RECEIVER=PASS")
	else:
		for message in failures:
			print("HEIGHT_SHADOW_RECEIVER FAIL: ", message)
		print("HEIGHT_SHADOW_RECEIVER=FAIL")
	quit(0 if failures.is_empty() else 1)


# ------------------------------------------------------------------ cases

func _test_step_edge(material: ShaderMaterial) -> void:
	# Texel i (around = 4 i) < 0 shadowed (R 10, G 100), >= 0 lit. The interpolated crossing sits between texel -1 and 0 (x = -2).
	_frame_plane(80.0)
	var l0 := _layer_step(10.0, 100.0, 0.0, 0.0)
	_set_globals([l0, l0, l0], _layers_uniform(0.0, 0.0))
	var values := await _strip()
	var edge := _crossing(values, 80.0, 0.0, 0.5)
	print("HEIGHT_SHADOW_RECEIVER step edge at x=%.2f m (expected -2, texel 4 m), left %.3f right %.3f" % [edge, _linear(values[10]), _linear(values[SIZE.x - 10])])
	_check(absf(edge + 2.0) <= 4.0, "step edge %f not within one texel of -2" % edge)
	_check(_linear(values[10]) < 0.05 and _linear(values[SIZE.x - 10]) > 0.95, "step edge endpoints")


func _test_penumbra(material: ShaderMaterial) -> void:
	# R ramps 0.5 + 0.1 x (metres), G = 400: v = 0.5 - 0.1 x / (G tan) so 5%..95% spans 0.9 * G tan / 0.1 metres.
	_frame_plane(100.0)
	var l0 := _layer_ramp(0.1, 400.0)
	_set_globals([l0, l0, l0], _layers_uniform(0.0, 0.0))
	var values := await _strip()
	var tan_pen: float = tan(deg_to_rad(0.5))
	var expected := 0.9 * 400.0 * tan_pen / 0.1
	var x_hi := _crossing(values, 100.0, 0.0, 0.95)
	var x_lo := _crossing(values, 100.0, 0.0, 0.05)
	var width := absf(x_lo - x_hi)
	print("HEIGHT_SHADOW_RECEIVER penumbra 5-95%% width %.2f m, expected %.2f m (G 400 m, tan %.5f)" % [width, expected, tan_pen])
	_check(absf(width - expected) <= 0.2 * expected, "penumbra width %f vs expected %f" % [width, expected])


func _test_unresolved_and_ports() -> void:
	_frame_plane(80.0)
	var hub_saved := 0.0
	for entry in MATERIALS:
		var material := _material(entry)
		_show(entry, material)
		var name: String = entry.name
		var mode: int = entry.mode
		# (a) unresolved everywhere and lit so far (R below the receiver, G < 0): receivers behave exactly as with the heightfield off, in the normal view and in the debug view.
		var unresolved := _layer_fill(-50.0, -1.0)
		for debug_mode in [0, mode]:
			RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", debug_mode)
			_set_globals([unresolved, unresolved, unresolved], [unresolved, unresolved, unresolved], 0, -1, 1.0, 0.0, false)
			var off := await _grab()
			_set_globals([unresolved, unresolved, unresolved], [unresolved, unresolved, unresolved], 0, -1, 1.0, 0.0, true)
			var on := await _grab()
			var diff := _diff(off, on)
			_check(diff.pixels == 0, "%s: unresolved ON differs from OFF in debug %d (%d px, max %d)" % [name, debug_mode, diff.pixels, diff.max])
			on.save_png(ProjectSettings.globalize_path(_out_dir.path_join("unresolved_%s_d%d.png" % [name, debug_mode])))
		# (b) shadow on the left half: only the left half reads the shadow.
		RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", mode)
		var step := _layer_step(100.0, 500.0, 0.0, 0.0)
		_set_globals([step, step, step], _layers_uniform(0.0, 0.0), 0, -1, 1.0, 0.0, true)
		var image := await _grab()
		image.save_png(ProjectSettings.globalize_path(_out_dir.path_join("step_%s.png" % name)))
		# 80 m wide view: x in [-24, -12] and [12, 24] metres (inside the sphere's 28 m radius too)
		var left := _sample(image, Rect2i(int((-24.0 / 80.0 + 0.5) * SIZE.x), 20, int(12.0 / 80.0 * SIZE.x), 24))
		var right := _sample(image, Rect2i(int((12.0 / 80.0 + 0.5) * SIZE.x), 20, int(12.0 / 80.0 * SIZE.x), 24))
		print("HEIGHT_SHADOW_RECEIVER port %-16s debug %d left %.3f right %.3f" % [name, mode, left, right])
		_check(left < 0.25, "%s: shadowed half reads %f (expected < 0.25)" % [name, left])
		_check(right > 0.8, "%s: lit half reads %f (expected > 0.8)" % [name, right])
		RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", hub_saved)
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 4)
	_show(MATERIALS[0], _material(MATERIALS[0]))


func _test_unresolved_partial() -> void:
	# Unresolved texels (G < 0) keep their in-window lower bound R: R above the receiver is a real shadow (vis 0, weight 1, unknown 0 -> macro scale 0), R below it is
	# only "lit so far" (vis 1, unknown 1 -> macro scale 1). |G| drives penumbra and authority; a bilinear quad and a crossfade carry the unknown share.
	var voxel := _material(MATERIALS[0])
	var proxy := _material(MATERIALS[1])
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 4)
	_show(MATERIALS[0], voxel)
	_frame_plane(80.0)
	var shadowed_unresolved := _layer_fill(50.0, -200.0)
	var lit_unresolved := _layer_fill(-50.0, -200.0)
	_set_globals([shadowed_unresolved, shadowed_unresolved, shadowed_unresolved], _layers_uniform(0.0, 0.0))
	var vis_shadowed := _linear((await _strip())[SIZE.x / 2])
	_set_globals([lit_unresolved, lit_unresolved, lit_unresolved], _layers_uniform(0.0, 0.0))
	var vis_lit := _linear((await _strip())[SIZE.x / 2])
	print("HEIGHT_SHADOW_RECEIVER partial: G=-200 R=+50 -> vis %.3f (expected 0), R=-50 -> vis %.3f (expected 1)" % [vis_shadowed, vis_lit])
	_check(vis_shadowed < 0.05, "unresolved texel with R=+50 reads %f (expected 0)" % vis_shadowed)
	_check(vis_lit > 0.95, "unresolved texel with R=-50 reads %f (expected 1)" % vis_lit)

	# Macro scale through the proxy with its macro term visible: R=+50 owns the receiver (identical to the resolved texel), R=-50 keeps the macro (identical to OFF).
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
	_show(MATERIALS[1], proxy)
	var resolved_shadowed := _layer_fill(50.0, 200.0)
	_set_globals([shadowed_unresolved, shadowed_unresolved, shadowed_unresolved], _layers_uniform(0.0, 0.0))
	var image_unresolved_shadow := await _grab()
	_set_globals([resolved_shadowed, resolved_shadowed, resolved_shadowed], _layers_uniform(0.0, 0.0))
	var image_resolved_shadow := await _grab()
	var diff_a := _diff(image_unresolved_shadow, image_resolved_shadow)
	_check(diff_a.pixels == 0, "proxy: unresolved R=+50 differs from the resolved texel (%d px, max %d): macro scale not 0 or |G| not used" % [diff_a.pixels, diff_a.max])
	_set_globals([lit_unresolved, lit_unresolved, lit_unresolved], _layers_uniform(0.0, 0.0), 0, -1, 1.0, 0.0, true)
	var image_lit_on := await _grab()
	_set_globals([lit_unresolved, lit_unresolved, lit_unresolved], _layers_uniform(0.0, 0.0), 0, -1, 1.0, 0.0, false)
	var image_lit_off := await _grab()
	var diff_b := _diff(image_lit_on, image_lit_off)
	_check(diff_b.pixels == 0, "proxy: unresolved R=-50 differs from OFF (%d px, max %d): macro scale not 1" % [diff_b.pixels, diff_b.max])
	var image_owned := image_resolved_shadow
	_check(_diff(image_owned, image_lit_off).pixels > 0, "proxy: heightfield-owned shadow is indistinguishable from the macro-only image (test would be vacuous)")
	print("HEIGHT_SHADOW_RECEIVER partial macro: unresolved+shadowed == resolved (%d px differ), unresolved+lit == OFF (%d px differ)" % [diff_a.pixels, diff_b.pixels])

	# Penumbra and authority use |G|.
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 4)
	_show(MATERIALS[0], voxel)
	_frame_plane(100.0)
	var ramp := _layer_ramp(0.1, -400.0)
	_set_globals([ramp, ramp, ramp], _layers_uniform(0.0, 0.0))
	var values := await _strip()
	var expected := 0.9 * 400.0 * tan(deg_to_rad(0.5)) / 0.1
	var width := absf(_crossing(values, 100.0, 0.0, 0.05) - _crossing(values, 100.0, 0.0, 0.95))
	print("HEIGHT_SHADOW_RECEIVER partial penumbra (G=-400) 5-95%% width %.2f m, expected %.2f m" % [width, expected])
	_check(absf(width - expected) <= 0.2 * expected, "unresolved penumbra width %f vs expected %f (|G| not used)" % [width, expected])
	_frame_plane(80.0)
	var near_blocker := _layer_step(100.0, -500.0, 0.0, 0.0)
	var far_blocker := _layer_step(100.0, -2000.0, 0.0, 0.0)
	var results := []
	for case in [[near_blocker, 1000.0], [far_blocker, 1000.0]]:
		_set_globals([case[0], case[0], case[0]], _layers_uniform(0.0, 0.0), 0, -1, 1.0, case[1])
		results.append(_linear((await _strip())[SIZE.x / 8]))
	print("HEIGHT_SHADOW_RECEIVER partial authority: |G| 500 / A 1000 -> %.3f (map owns it, lit), |G| 2000 / A 1000 -> %.3f" % [results[0], results[1]])
	_check(results[0] > 0.95, "authority did not use |G| for the near unresolved blocker (%f)" % results[0])
	_check(results[1] < 0.05, "authority dropped an unresolved blocker beyond the map (%f)" % results[1])

	# Mixed bilinear quad: left unresolved + lit so far, right resolved + shadowed; the crossing sits at the texel boundary (x = -2) and is monotonic.
	_frame_plane(80.0)
	var quad := _layer_step(-50.0, -100.0, 50.0, 100.0)
	_set_globals([quad, quad, quad], _layers_uniform(0.0, 0.0))
	values = await _strip()
	var edge := _crossing(values, 80.0, 0.0, 0.5)
	print("HEIGHT_SHADOW_RECEIVER partial mixed quad: crossing at x=%.2f m (expected -2), left %.3f right %.3f" % [edge, _linear(values[10]), _linear(values[SIZE.x - 10])])
	_check(absf(edge + 2.0) <= 4.0, "mixed quad crossing %f not within one texel of -2" % edge)
	_check(_linear(values[10]) > 0.95 and _linear(values[SIZE.x - 10]) < 0.05, "mixed quad endpoints")

	# Crossfade: front layer unresolved + lit (unknown 1), previous layer resolved + lit (unknown 0). t=1 keeps the macro (== OFF), t=0 removes it, t=0.5 is in between.
	_show(MATERIALS[1], proxy)
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
	_frame_plane(80.0)
	var resolved_lit := _layer_fill(-50.0, 0.0)
	var means := []
	var images := []
	for t in [0.0, 0.5, 1.0]:
		_set_globals([lit_unresolved, resolved_lit, resolved_lit], _layers_uniform(0.0, 0.0), 0, 1, t, 0.0, true, GLOBAL_BOUNDS_L0, GLOBAL_BOUNDS_L1, true, true, GLOBAL_BOUNDS_L0)
		var image := await _grab()
		images.append(image)
		means.append(_sample(image, Rect2i(0, 0, SIZE.x, SIZE.y)))
	var diff_end := _diff(images[2], image_lit_off)
	var lo := minf(means[0], means[2])
	var hi := maxf(means[0], means[2])
	print("HEIGHT_SHADOW_RECEIVER partial crossfade: mean t=0 %.4f, t=0.5 %.4f, t=1 %.4f; t=1 vs OFF %d px differ" % [means[0], means[1], means[2], diff_end.pixels])
	_check(diff_end.pixels == 0, "crossfade t=1 (unresolved lit front) differs from OFF (%d px)" % diff_end.pixels)
	_check(hi - lo > 0.0005, "crossfade endpoints do not differ (macro scale 0 vs 1 invisible)")
	_check(means[1] > lo + 0.1 * (hi - lo) and means[1] < hi - 0.1 * (hi - lo), "crossfade t=0.5 mean %f not between %f and %f" % [means[1], lo, hi])
	RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 4)
	_show(MATERIALS[0], voxel)


func _test_l0_l1(material: ShaderMaterial) -> void:
	_frame_plane(800.0)
	var shadow := _layer_fill(100.0, 500.0)
	var lit := _layer_fill(0.0, 0.0)
	_set_globals([shadow, shadow, shadow], [lit, lit, lit], 0, -1, 1.0, 0.0, true, Vector4(-300, 300, -300, 300))
	var values := await _strip()
	var centre := _linear(values[SIZE.x / 2])
	var far := _linear(values[SIZE.x - 4])
	var monotonic := true
	var previous := -1.0
	for px in range(SIZE.x / 2, SIZE.x):
		var v := _linear(values[px])
		if v < previous - 0.015:
			monotonic = false
		previous = maxf(previous, v)
	print("HEIGHT_SHADOW_RECEIVER L0->L1: centre %.3f at x=0, %.3f at x=400, monotonic=%s" % [centre, far, str(monotonic)])
	_check(monotonic, "L0 -> L1 crossfade is not monotonic")
	_check(centre < 0.05 and far > 0.95, "L0 -> L1 endpoints %f %f" % [centre, far])


func _test_l1_macro(material: ShaderMaterial) -> void:
	_frame_plane(3200.0)
	var shadow := _layer_fill(100.0, 500.0)
	_set_globals(_layers_uniform(0.0, 0.0), [shadow, shadow, shadow], 0, -1, 1.0, 0.0, true, EMPTY_BOUNDS, Vector4(-1500, 1500, -1500, 1500), false, true)
	var values := await _strip()
	var centre := _linear(values[SIZE.x / 2])
	var at1000 := _linear(values[int(SIZE.x / 2.0 + 1000.0 / 3200.0 * SIZE.x)])
	var outside := _linear(values[SIZE.x - 2])
	var monotonic := true
	var previous := -1.0
	for px in range(SIZE.x / 2, SIZE.x):
		var v := _linear(values[px])
		if v < previous - 0.015:
			monotonic = false
		previous = maxf(previous, v)
	print("HEIGHT_SHADOW_RECEIVER L1->macro: x=0 %.3f, x=1000 %.3f (expected ~0.5), x=1580 %.3f, monotonic=%s" % [centre, at1000, outside, str(monotonic)])
	_check(monotonic, "L1 -> macro fade is not monotonic")
	_check(centre < 0.05 and outside > 0.95 and absf(at1000 - 0.5) < 0.1, "L1 -> macro values %f %f %f" % [centre, at1000, outside])


func _test_hub(material: ShaderMaterial) -> void:
	_frame_plane(80.0)
	var step := _layer_step(100.0, 500.0, 0.0, 0.0)
	_set_globals([step, step, step], _layers_uniform(0.0, 0.0))
	RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", 0.6)
	var values := await _strip()
	RenderingServer.global_shader_parameter_set("ringworld_hub_light_weight", 0.0)
	var left := _linear(values[SIZE.x / 8])
	var right := _linear(values[SIZE.x * 7 / 8])
	print("HEIGHT_SHADOW_RECEIVER hub 0.6: shadowed side %.3f (expected 0.6), lit side %.3f" % [left, right])
	_check(absf(left - 0.6) < 0.03 and right > 0.95, "hub 0.6 scaling %f / %f" % [left, right])


func _test_authority(material: ShaderMaterial) -> void:
	_frame_plane(80.0)
	var near_blocker := _layer_step(100.0, 500.0, 0.0, 0.0)
	var far_blocker := _layer_step(100.0, 2000.0, 0.0, 0.0)
	var results := []
	for case in [[near_blocker, 1000.0], [far_blocker, 1000.0], [near_blocker, 0.0]]:
		_set_globals([case[0], case[0], case[0]], _layers_uniform(0.0, 0.0), 0, -1, 1.0, case[1])
		var values := await _strip()
		results.append(_linear(values[SIZE.x / 8]))
	print("HEIGHT_SHADOW_RECEIVER authority: blocker 500 m / A 1000 -> %.3f (map owns it, lit), 2000 m / A 1000 -> %.3f, 500 m / A 0 -> %.3f" % [results[0], results[1], results[2]])
	_check(results[0] > 0.95, "authority rule did not drop the near blocker (%f)" % results[0])
	_check(results[1] < 0.05, "authority rule dropped a blocker beyond the map (%f)" % results[1])
	_check(results[2] < 0.05, "suspended map (A 0) must not drop blockers (%f)" % results[2])


func _test_crossfade(material: ShaderMaterial) -> void:
	_frame_plane(80.0)
	var shadow := _layer_fill(100.0, 100.0)
	var lit := _layer_fill(0.0, 0.0)
	var results := []
	for t in [0.0, 0.5, 1.0]:
		# front layer 0 = shadow, previous layer 1 = lit
		_set_globals([shadow, lit, lit], _layers_uniform(0.0, 0.0), 0, 1, t, 0.0, true, GLOBAL_BOUNDS_L0, GLOBAL_BOUNDS_L1, true, true, GLOBAL_BOUNDS_L0)
		var values := await _strip()
		results.append(_linear(values[SIZE.x / 2]))
	print("HEIGHT_SHADOW_RECEIVER mid-crossfade (front shadowed, previous lit): t=0 %.3f, t=0.5 %.3f, t=1 %.3f" % [results[0], results[1], results[2]])
	_check(results[0] > 0.95 and absf(results[1] - 0.5) < 0.05 and results[2] < 0.05, "crossfade mix %s" % str(results))


func _test_overlay(material: ShaderMaterial) -> void:
	# Debug mode 4 on the receiver and the debug overlay (mode 0) classify the same ground points the same way.
	_frame_plane(200.0)
	var step := _layer_step(10.0, 100.0, 0.0, 0.0)
	_set_globals([step, step, step], _layers_uniform(0.0, 0.0))
	var values := await _strip()
	var overlay_viewport := SubViewport.new()
	overlay_viewport.size = Vector2i(256, 8)
	overlay_viewport.transparent_bg = false
	overlay_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rect := ColorRect.new()
	rect.size = Vector2(256, 8)
	var overlay := ShaderMaterial.new()
	overlay.shader = load(OVERLAY_SHADER)
	overlay.set_shader_parameter("view_origin", Vector3.ZERO)
	overlay.set_shader_parameter("axis_u", Vector3(1, 0, 0))
	overlay.set_shader_parameter("axis_v", Vector3(0, 0, 1))
	overlay.set_shader_parameter("extent_m", 200.0)
	overlay.set_shader_parameter("mode", 0)
	rect.material = overlay
	overlay_viewport.add_child(rect)
	root.add_child(overlay_viewport)
	for i in SETTLE_FRAMES:
		await RenderingServer.frame_post_draw
	var image := overlay_viewport.get_texture().get_image()
	overlay_viewport.queue_free()
	var agree := 0
	var total := 0
	for x_m in [-80.0, -50.0, -25.0, -12.0, 12.0, 25.0, 50.0, 80.0]:
		var receiver_px := int((x_m / 200.0 + 0.5) * SIZE.x)
		var overlay_px := int((x_m / 200.0 + 0.5) * 256.0)
		var receiver_shadowed := _linear(values[receiver_px]) < 0.5
		var colour := image.get_pixel(overlay_px, 4)
		var overlay_shadowed := colour.b > colour.r + 0.05 and colour.r < 0.3
		total += 1
		if receiver_shadowed == overlay_shadowed:
			agree += 1
		else:
			push_error("overlay disagrees at x=%f m: receiver shadowed=%s overlay colour=%s" % [x_m, str(receiver_shadowed), str(colour)])
	print("HEIGHT_SHADOW_RECEIVER overlay vs debug 4: %d/%d sample points agree" % [agree, total])
	_check(agree == total, "overlay classification disagrees with the receiver at %d points" % (total - agree))


# ------------------------------------------------------------------ globals and data

func _layers_uniform(r: float, g: float) -> Array:
	var layer := _layer_fill(r, g)
	return [layer, layer, layer]


func _blank(r: float, g: float) -> Image:
	var image := Image.create_empty(TEXTURE_SIZE, TEXTURE_SIZE, false, Image.FORMAT_RGF)
	image.fill(Color(r, g, 0.0, 0.0))
	return image


func _layer_fill(r: float, g: float) -> Image:
	return _blank(r, g)


## Slots 512..1023 (around index < 0) get (left_r, left_g), slots 0..511 (index >= 0) get (right_r, right_g).
func _layer_step(left_r: float, left_g: float, right_r: float, right_g: float) -> Image:
	var image := _blank(right_r, right_g)
	image.fill_rect(Rect2i(512, 0, 512, TEXTURE_SIZE), Color(left_r, left_g, 0.0, 0.0))
	return image


## R = 0.5 + slope * x (metres, x = 4 index) per column, constant G.
func _layer_ramp(slope: float, g: float) -> Image:
	var image := _blank(0.0, g)
	for index in range(-512, 512):
		var slot := index & 1023
		image.fill_rect(Rect2i(slot, 0, 1, TEXTURE_SIZE), Color(0.5 + slope * 4.0 * index, g, 0.0, 0.0))
	return image


func _array(layers: Array) -> Texture2DArray:
	var images: Array[Image] = []
	for layer in layers:
		var copy: Image = layer.duplicate()
		copy.convert(Image.FORMAT_RGH)
		images.append(copy)
	var texture := Texture2DArray.new()
	var error := texture.create_from_images(images)
	if error != OK:
		push_error("Texture2DArray.create_from_images failed: %d" % error)
	return texture


func _set_globals(l0_layers: Array, l1_layers: Array, front := 0, prev := -1, t := 1.0, authority := 0.0, receivers := true,
		bounds0 := GLOBAL_BOUNDS_L0, bounds1 := GLOBAL_BOUNDS_L1, l0_valid := true, l1_valid := true, prev_bounds0 := EMPTY_BOUNDS) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l0", _array(l0_layers))
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l1", _array(l1_layers))
	# World -> anchor frame for a flat test ring: f = (x, z, y) with a 5000 km floor radius.
	var frame := Projection(Vector4(1, 0, 0, 0), Vector4(0, 0, 1, 0), Vector4(0, 1, 0, 0), Vector4(0, 0, 0, 1))
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_frame", frame)
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l0_params", Vector4(4.0, 1.0 / 4096.0, 1.0 if l0_valid else 0.0, 0.0))
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l1_params", Vector4(16.0, 1.0 / 16384.0, 1.0 if l1_valid else 0.0, 0.0))
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l0_bounds", bounds0 if l0_valid else EMPTY_BOUNDS)
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l0_prev_bounds", prev_bounds0)
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l1_bounds", bounds1 if l1_valid else EMPTY_BOUNDS)
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l1_prev_bounds", EMPTY_BOUNDS)
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_state", Vector4(front, prev, t, 1.0 if receivers else 0.0))
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_shape", Vector4(tan(deg_to_rad(0.5)), 0.5, 5000000.0, authority))


# ------------------------------------------------------------------ scene

func _build_scene() -> void:
	_viewport = SubViewport.new()
	_viewport.own_world_3d = true
	_viewport.size = SIZE
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.4, 0.45, 0.5)
	environment.ambient_light_energy = 0.3
	environment.ssao_enabled = false
	environment.glow_enabled = false
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	_viewport.add_child(world_environment)
	_camera = Camera3D.new()
	_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_camera.keep_aspect = Camera3D.KEEP_WIDTH
	_camera.near = 1.0
	_camera.far = 200.0
	_viewport.add_child(_camera)
	_camera.look_at_from_position(Vector3(0, 60, 0), Vector3.ZERO, Vector3(0, 0, -1))
	_camera.current = true
	_sun = DirectionalLight3D.new()
	_sun.light_energy = 1.0
	_sun.shadow_enabled = false
	_viewport.add_child(_sun)
	var up_hint := Vector3.RIGHT if absf(SUN_DIR.normalized().dot(Vector3.UP)) > 0.95 else Vector3.UP
	_sun.transform = Transform3D(Basis.looking_at(-SUN_DIR.normalized(), up_hint), Vector3.ZERO)
	_plane = MeshInstance3D.new()
	var plane_mesh := PlaneMesh.new()
	plane_mesh.size = Vector2(8000.0, 120.0)
	_plane.mesh = plane_mesh
	_viewport.add_child(_plane)
	_sphere = MeshInstance3D.new()
	var sphere_mesh := SphereMesh.new()
	sphere_mesh.radius = 28.0
	sphere_mesh.height = 56.0
	sphere_mesh.radial_segments = 64
	sphere_mesh.rings = 32
	_sphere.mesh = sphere_mesh
	_sphere.position = Vector3(0, 28, 0)
	_viewport.add_child(_sphere)
	_macro_texture = ImageTexture.create_from_image(_stripe_image())


func _stripe_image() -> Image:
	var image := Image.create_empty(32, 64, false, Image.FORMAT_R8)
	for y in 64:
		for x in 32:
			image.set_pixel(x, y, Color(clampf(0.5 + 0.5 * sin(float(x) / 32.0 * TAU * 3.0) * sin(float(y) / 64.0 * TAU * 5.0), 0.0, 1.0), 0, 0, 1))
	return image


func _frame_plane(width_m: float) -> void:
	_camera.size = width_m


func _material(entry: Dictionary) -> ShaderMaterial:
	var material := ShaderMaterial.new()
	if String(entry.shader) == "":
		var shader := Shader.new()
		shader.code = TERRAIN_LIGHTING_CODE
		material.shader = shader
	else:
		material.shader = load(entry.shader)
	var names := {}
	for uniform in material.shader.get_shader_uniform_list():
		names[uniform.name] = true
	var values := {
		"ring_center": Vector3(0, 0, 0),
		"camera_world_position": Vector3(0, 60, 0),
		"sun_direction": SUN_DIR.normalized(),
		"day_start_dot": -3.0,
		"day_full_dot": -2.0,
		"night_tint": Vector3(1, 1, 1),
		"night_direct_light_factor": 1.0,
		"day_direct_light_factor": 1.0,
		"shadow_floor": 0.0,
		"ring_width_meters": 120.0,
		"terrain_macro_shadow_enabled": true,
		"terrain_macro_shadow_texture": _macro_texture,
		"terrain_macro_shadow_strength": 0.45,
		"terrain_macro_shadow_fade_start_meters": 0.0,
		"terrain_macro_shadow_fade_end_meters": 1.0,
		"tree_tint": Vector3(0.3, 0.7, 0.25),
	}
	for key in values:
		if names.has(key):
			material.set_shader_parameter(key, values[key])
	var params: Dictionary = entry.params
	for key in params:
		if not names.has(key):
			push_error("%s has no uniform %s" % [entry.name, key])
			continue
		material.set_shader_parameter(key, params[key])
	return material


func _show(entry: Dictionary, material: ShaderMaterial) -> void:
	var sphere: bool = entry.receiver == "sphere"
	_plane.visible = not sphere
	_sphere.visible = sphere
	_plane.material_override = null if sphere else material
	_sphere.material_override = material if sphere else null


# ------------------------------------------------------------------ grabbing and analysis

func _grab() -> Image:
	for frame in SETTLE_FRAMES:
		await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _row(x_m: float, _unused: float) -> float:
	var image: Image = await _grab()
	return image.get_pixel(SIZE.x / 2, SIZE.y / 2).r


## The mid row of a grab as an array of raw red values (0..1 as stored).
func _strip() -> Array:
	var image: Image = await _grab()
	var values := []
	for x in SIZE.x:
		values.append(image.get_pixel(x, SIZE.y / 2).r)
	return values


func _linear(stored: float) -> float:
	if not _srgb:
		return stored
	return stored / 12.92 if stored <= 0.04045 else pow((stored + 0.055) / 1.055, 2.4)


func _sample(image: Image, rect: Rect2i) -> float:
	var sum := 0.0
	var count := 0
	for y in range(rect.position.y, rect.position.y + rect.size.y):
		for x in range(rect.position.x, rect.position.x + rect.size.x):
			sum += _linear(image.get_pixel(x, y).r)
			count += 1
	return sum / maxf(1.0, count)


## World x (metres) where the decoded strip first crosses `level` going from the left; the strip spans [centre - width/2, centre + width/2].
func _crossing(values: Array, width_m: float, centre_m: float, level: float) -> float:
	for px in range(1, SIZE.x):
		var a := _linear(values[px - 1])
		var b := _linear(values[px])
		if (a - level) * (b - level) <= 0.0 and a != b:
			var fraction := (level - a) / (b - a)
			return centre_m + ((px - 1 + fraction + 0.5) / SIZE.x - 0.5) * width_m
	return NAN


func _diff(a: Image, b: Image) -> Dictionary:
	var da := a.get_data()
	var db := b.get_data()
	var pixels := 0
	var max_diff := 0
	for i in range(0, da.size(), 4):
		var pixel := 0
		for c in 4:
			pixel = maxi(pixel, absi(int(da[i + c]) - int(db[i + c])))
		if pixel > 0:
			pixels += 1
			max_diff = maxi(max_diff, pixel)
	return {"pixels": pixels, "max": max_diff}
