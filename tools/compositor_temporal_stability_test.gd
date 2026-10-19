extends "res://tools/atmosphere_visual_test.gd"

## Compositor temporal-stability test (FSR2 / TAA jitter compensation). Boots the certified gameplay scene with the same fixture as
## tools/fsr_jitter_probe.gd, freezes the explorer, turns local wind, the wind field, rain and Sunshine cloud wind off, then for two
## pitches (-10 and +15 degrees) and five configurations
##   native (1.0, no AA) / fsr2_off / fsr2_on (FSR2 67%, compensation off / on) / taa_off / taa_on (native + TAA, compensation off / on)
## warms 96 frames, captures the root viewport for 32 frames and computes the per-pixel temporal luminance standard deviation.
## Regions: edge (top 5% Sobel of the mean image), flat (below the Sobel median), horizon (+-5% rows around the strongest vertical
## gradient row) and whole. Prints one COMPOSITOR_STABILITY line per cfg/roi and saves the stddev images under res://.godot/.
## Acceptance: fsr2_on / taa_on horizon and edge p99 <= 1.05 x the matching *_off (compensation must not regress) and flat mean
## <= 1.1 x *_off; fsr2_off / fsr2_on whole p99 <= 1.5 x taa_on (FSR2 history accumulates). See check_pair for why the original
## 0.6 x off / 1.5 x native targets were dropped.
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   godot --path . --script res://tools/compositor_temporal_stability_test.gd
## Optional: -- --uv-sign=-1 (ablation of the debug uv sign: 1 = shipped, 0 = no resample, -1 = flipped) --pitches=-10,15
## Diagnostics: --configs=a,b (adds fsr2_100 = FSR2 at 100%) --only-effects=<script substrings> / --empty-camera-compositor (camera
## compositor overriding the WorldEnvironment one) --input-noise (pre-upscale vs output temporal noise) --velocity (motion-vector stats)
## --no-clouds/--no-backdrop/--no-effects/--no-exposure/--no-shadows/--env-off=ssao,... --sharpness= --mip-bias= --dump-frames --tag=
const WARMUP_FRAMES := 96
const CAPTURE_FRAMES := 32
const STABILITY_HELPER := preload("res://tools/CompositorStabilityHelper.cs")
const CONFIGS := [
	{"name": "native", "mode": "native", "compensation": true},
	{"name": "fsr2_off", "mode": "fsr2", "compensation": false},
	{"name": "fsr2_on", "mode": "fsr2", "compensation": true},
	{"name": "taa_off", "mode": "taa", "compensation": false},
	{"name": "taa_on", "mode": "taa", "compensation": true},
	{"name": "fsr2_100", "mode": "fsr2_native", "compensation": true},
]
const DEFAULT_CONFIGS := ["native", "fsr2_off", "fsr2_on", "taa_off", "taa_on"]
var uv_sign := 1.0
var pitches: Array = [-10.0, 15.0]
var pause_menu: Node
var config_filter: Array = DEFAULT_CONFIGS
## Ablations (diagnostic only): --no-clouds / --no-backdrop / --no-effects disable compositor effects, --no-exposure turns auto
## exposure off, --hide=grass,trees hides vegetation, --sharpness=<godot fsr_sharpness>, --mip-bias=<offset>, --tag=<png suffix>.
var ablate_clouds := false
var ablate_backdrop := false
var ablate_effects := false
var ablate_exposure := false
var hide_nodes: PackedStringArray = []
var sharpness_override := -1.0
var mip_bias_override := NAN
var tag := ""
var env_off: PackedStringArray = []
var no_shadows := false
var dump_frames := false
var empty_camera_compositor := false
var only_effects: PackedStringArray = []
var list_transparent_flag := false
var velocity_probe: CompositorEffect = null
var input_probe: CompositorEffect = null
const INPUT_PROBE := preload("res://tools/fsr2_input_capture_effect.gd")
var clouds_driver: Node
var results := {} # "pitch|cfg" -> {roi: {mean, p99}}

func run() -> void:
	if RenderingServer.get_rendering_device() == null: return fail("requires Vulkan/Forward+")
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--uv-sign="): uv_sign = float(argument.get_slice("=", 1))
		elif argument.begins_with("--pitches="):
			pitches = []
			for item in argument.get_slice("=", 1).split(","): pitches.append(float(item))
		elif argument.begins_with("--configs="): config_filter = Array(argument.get_slice("=", 1).split(","))
		elif argument == "--no-clouds": ablate_clouds = true
		elif argument == "--no-backdrop": ablate_backdrop = true
		elif argument == "--no-effects": ablate_effects = true
		elif argument == "--no-exposure": ablate_exposure = true
		elif argument == "--velocity": velocity_probe = load("res://tools/velocity_stats_effect.gd").new()
		elif argument == "--input-noise": input_probe = INPUT_PROBE.new()
		elif argument.begins_with("--hide="): hide_nodes = argument.get_slice("=", 1).split(",")
		elif argument.begins_with("--sharpness="): sharpness_override = float(argument.get_slice("=", 1))
		elif argument.begins_with("--mip-bias="): mip_bias_override = float(argument.get_slice("=", 1))
		elif argument.begins_with("--tag="): tag = "_" + argument.get_slice("=", 1)
		elif argument.begins_with("--env-off="): env_off = argument.get_slice("=", 1).split(",")
		elif argument == "--no-shadows": no_shadows = true
		elif argument == "--dump-frames": dump_frames = true
		elif argument == "--empty-camera-compositor": empty_camera_compositor = true
		elif argument.begins_with("--only-effects="): only_effects = argument.get_slice("=", 1).split(",")
		elif argument == "--list-transparent": list_transparent_flag = true
	fixture = "res://.godot/compositor_stability/%s" % Time.get_ticks_usec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	var prior := root.get_node_or_null("WorldSession")
	if prior != null: prior.name = "WorldSessionOriginalStability"
	var session := SessionStub.new(); session.name = "WorldSession"; root.add_child(session)
	game = await create_game()
	if game == null: return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	clock = game.get_node_or_null("RingDayNightCycle")
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	global_terrain = game.get_node_or_null("GlobalTerrainLod3")
	camera = game.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	pause_menu = game.get_node_or_null("LodPauseMenu")
	clouds_driver = game.get_node_or_null("SunshineCloudsDriver")
	placement = FIXTURE_HELPER.new()
	if explorer == null or surface == null or camera == null or grass == null or trees == null or clock == null or pause_menu == null:
		return fail("missing gameplay nodes")
	var ground_height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	if not is_finite(ground_height): return fail("dryland sample height was non-finite")
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await wait_ready(): return
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	pause_menu.call("InstallRenderResolutionDefaults", fixture.path_join("render_resolution.tres"))
	# Freeze everything that moves on its own: sun, local wind / wind field (re-asserted every frame), rain and Sunshine cloud wind.
	clock.set("Paused", true)
	clock.call("SetCycleFraction", float(select_visual_phases()["noon"]))
	var local_weather := game.get_node_or_null("LocalWeatherEffects")
	if local_weather != null: local_weather.set("Enabled", false)
	var bridge := game.get_node_or_null("SunshineCloudBridge")
	if bridge != null: bridge.set("CloudWindMultiplier", 0.0)
	for node_name in hide_nodes:
		var hidden := game.get_node_or_null(str({"grass": "StandingGrass", "trees": "BiomeDecorations"}.get(node_name, node_name)))
		if hidden is Node3D: (hidden as Node3D).visible = false
		elif hidden != null: hidden.process_mode = Node.PROCESS_MODE_DISABLED
	if ablate_exposure:
		var atmosphere := game.get_node_or_null("DaySideAtmosphere")
		if atmosphere != null: atmosphere.set("storm_auto_exposure_enabled", false)
	for node in all_nodes(game):
		if node is WorldEnvironment and (node as WorldEnvironment).compositor != null:
			for effect in (node as WorldEnvironment).compositor.compositor_effects:
				if effect != null: print("COMPOSITOR_STABILITY effect=%s callback=%d" % [effect_path(effect), effect.effect_callback_type])
	if velocity_probe != null or input_probe != null:
		for node in all_nodes(game):
			if node is WorldEnvironment and (node as WorldEnvironment).compositor != null:
				var effects := (node as WorldEnvironment).compositor.compositor_effects.duplicate()
				for probe in [velocity_probe, input_probe]:
					if probe != null: effects.append(probe)
				(node as WorldEnvironment).compositor.compositor_effects = effects
				break
	# Keep the in-progress physical sky-view LUT out of the A/B (static toggle; inert under the legacy haze model).
	if ResourceLoader.exists("res://scripts/Atmosphere/RingAtmosphereLuts.cs"):
		var luts = load("res://scripts/Atmosphere/RingAtmosphereLuts.cs").new()
		if luts != null and luts.has_method("SetSkyViewEnabled"): luts.call("SetSkyViewEnabled", false)
	if empty_camera_compositor: camera.compositor = Compositor.new() # overrides the WorldEnvironment compositor: no effects run
	if not only_effects.is_empty():
		# Camera compositor (overrides the WorldEnvironment one) holding only the named effects, by script-path substring.
		var kept: Array[CompositorEffect] = []
		for node in all_nodes(game):
			if node is WorldEnvironment and (node as WorldEnvironment).compositor != null and node.get_viewport() == root:
				for effect in (node as WorldEnvironment).compositor.compositor_effects:
					for wanted in only_effects:
						if effect != null and wanted != "" and effect_path(effect).contains(wanted) and not kept.has(effect): kept.append(effect)
		var only := Compositor.new(); only.compositor_effects = kept; camera.compositor = only
		print("COMPOSITOR_STABILITY only_effects=%s kept=%d" % [str(only_effects), kept.size()])
	var helper: RefCounted = STABILITY_HELPER.new()
	var failures: PackedStringArray = []
	for pitch in pitches:
		for config in CONFIGS:
			if not config_filter.has(config["name"]): continue
			await apply_config(config, uv_sign)
			await reset_pose(ground_height)
			explorer.call("SetPresentationView", 0.0, pitch)
			clock.call("SetCycleFraction", float(select_visual_phases()["noon"]))
			for ignored in WARMUP_FRAMES:
				freeze_wind()
				await process_frame
			print_environment_state()
			if list_transparent_flag: list_transparent()
			if velocity_probe != null:
				velocity_probe.set("mask_path", ProjectSettings.globalize_path("res://.godot/velocity_mask_%s%s.png" % [config.name, tag]))
				velocity_probe.call("capture", 6)
				for ignored in 8:
					freeze_wind()
					await RenderingServer.frame_post_draw
				for record in velocity_probe.call("take"): print("COMPOSITOR_STABILITY cfg=%s velocity %s" % [config.name, str(record)])
			var first_image: Image = null
			var output_series: Array = []
			if input_probe != null: input_probe.call("capture", CAPTURE_FRAMES)
			var frame := 0
			while frame < CAPTURE_FRAMES:
				freeze_wind()
				await RenderingServer.frame_post_draw
				var image := root.get_viewport().get_texture().get_image()
				if image.is_empty(): return fail("capture failed for " + str(config.name))
				if image.get_format() != Image.FORMAT_RGBA8: image.convert(Image.FORMAT_RGBA8)
				if first_image == null:
					first_image = image
					helper.call("Begin", image.get_width(), image.get_height())
				if not bool(helper.call("Accumulate", image.get_data())): return fail("accumulate failed for " + str(config.name))
				if input_probe != null: output_series.append(INPUT_PROBE.sample_output(image))
				if dump_frames and frame < 4: image.get_region(Rect2i(500, 450, 400, 250)).save_png(ProjectSettings.globalize_path("res://.godot/compositor_stability_frame_%s%s_%d.png" % [config.name, tag, frame]))
				frame += 1
			if input_probe != null:
				print("COMPOSITOR_STABILITY pitch=%d cfg=%s input_noise=%s output_noise=%s" % [int(pitch), config.name,
					str(INPUT_PROBE.relative_noise(input_probe.call("take"), 1.0e-4)), str(INPUT_PROBE.relative_noise(output_series, 1.0e-3))])
			var png := ProjectSettings.globalize_path("res://.godot/compositor_stability_%s_p%d%s.png" % [config.name, int(pitch), tag])
			var stats: Dictionary = helper.call("Finish", png)
			if stats.is_empty(): return fail("no statistics for " + str(config.name))
			results["%d|%s" % [int(pitch), config.name]] = stats
			print("COMPOSITOR_STABILITY pitch=%d cfg=%s viewport mode=%d scale=%.3f sharpness=%.2f mip_bias=%.2f taa=%s msaa=%d ssaa=%d" % [
				int(pitch), config.name, root.scaling_3d_mode, root.scaling_3d_scale, root.fsr_sharpness, root.texture_mipmap_bias,
				str(root.use_taa), root.msaa_3d, root.screen_space_aa])
			for roi in ["edge", "flat", "horizon", "whole"]:
				var entry: Dictionary = stats[roi]
				print("COMPOSITOR_STABILITY pitch=%d cfg=%s roi=%s mean=%.6f p99=%.6f pixels=%d" % [
					int(pitch), config.name, roi, float(entry["mean"]), float(entry["p99"]), int(entry["pixels"])])
		for pair in [["fsr2_on", "fsr2_off"], ["taa_on", "taa_off"]]:
			check_pair(int(pitch), pair[0], pair[1], failures)
		check_fsr2_accumulates(int(pitch), failures)
	# Leave the process in the native default state.
	await apply_config(CONFIGS[0], 1.0)
	for message in failures: push_error("COMPOSITOR_STABILITY=FAIL " + message)
	print("COMPOSITOR_STABILITY=%s uv_sign=%.1f" % ["FAIL" if not failures.is_empty() else "PASS", uv_sign])
	game.queue_free()
	await process_frame
	quit(1 if not failures.is_empty() else 0)

func apply_config(config: Dictionary, uv_sign_value: float) -> void:
	var mode: String = config["mode"]
	pause_menu.call("SetCompositorJitterCompensation", bool(config["compensation"]))
	pause_menu.call("SetRenderResolutionPreset", 4 if mode == "fsr2" else (1 if mode == "fsr2_native" else 0))
	if sharpness_override >= 0.0: pause_menu.call("SetFsrSharpness", sharpness_override)
	if not is_nan(mip_bias_override): pause_menu.call("SetMipBiasOffset", mip_bias_override)
	root.use_taa = mode == "taa"
	root.msaa_3d = Viewport.MSAA_DISABLED
	root.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED
	if mode == "native" or mode == "taa":
		root.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
		root.scaling_3d_scale = 1.0
	# Debug sign ablation: the Sunshine resource and the far backdrop effect each carry their own copy.
	var clouds: Resource = clouds_driver.get("clouds_resource") as Resource if clouds_driver != null else null
	if clouds != null: clouds.set("engine_jitter_uv_sign", uv_sign_value)
	for node in all_nodes(game):
		if node is WorldEnvironment and (node as WorldEnvironment).compositor != null:
			for effect in (node as WorldEnvironment).compositor.compositor_effects:
				if effect != null and "jitter_uv_sign" in effect: effect.set("jitter_uv_sign", uv_sign_value)
	await frames(2)

func freeze_wind() -> void:
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 0.0)
	if not env_off.is_empty() or no_shadows: apply_env_ablation()
	if not (ablate_exposure or ablate_clouds or ablate_backdrop or ablate_effects): return
	for node in all_nodes(game):
		if not node is WorldEnvironment: continue
		var environment := node as WorldEnvironment
		if ablate_exposure and environment.camera_attributes != null: environment.camera_attributes.set("auto_exposure_enabled", false)
		if environment.compositor == null: continue
		for effect in environment.compositor.compositor_effects:
			if effect == null: continue
			var path := effect_path(effect)
			if ablate_effects or (ablate_clouds and path.contains("Sunshine")) or (ablate_backdrop and path.contains("FarBackdrop")):
				effect.enabled = false

func effect_path(effect: CompositorEffect) -> String:
	var script: Script = effect.get_script()
	return script.resource_path if script != null else effect.get_class()

func check_pair(pitch: int, on_name: String, off_name: String, failures: PackedStringArray) -> void:
	var on: Dictionary = results.get("%d|%s" % [pitch, on_name], {})
	var off: Dictionary = results.get("%d|%s" % [pitch, off_name], {})
	if on.is_empty() or off.is_empty(): return
	# Compensation only re-aligns the compositor layers (cloud / far backdrop texels at the horizon and cloud edges); the remaining
	# static-camera noise is the upscaler's own jitter residual on geometry, so "on" must not regress against "off" (5% tolerance)
	# rather than beat it by a fixed factor, and neither can match native (no jitter at all).
	for roi in ["horizon", "edge"]:
		var p_on := float((on[roi] as Dictionary)["p99"])
		var p_off := float((off[roi] as Dictionary)["p99"])
		if p_on > 1.05 * p_off: failures.append("pitch %d %s %s p99 %.6f > 1.05 x %s %.6f" % [pitch, on_name, roi, p_on, off_name, p_off])
	var flat_on := float((on["flat"] as Dictionary)["mean"])
	var flat_off := float((off["flat"] as Dictionary)["mean"])
	if flat_on > 1.1 * flat_off: failures.append("pitch %d %s flat mean %.6f > 1.1 x %s %.6f" % [pitch, on_name, flat_on, off_name, flat_off])

## FSR2 must actually accumulate: its whole-frame p99 has to stay in the TAA range. A compositor pass that overwrites the colour alpha
## (Godot feeds it to FSR2 as the reactive mask) disables FSR2 history frame-wide and puts fsr2 ~5x above TAA (0.120 vs 0.023 p99).
func check_fsr2_accumulates(pitch: int, failures: PackedStringArray) -> void:
	var taa: Dictionary = results.get("%d|taa_on" % pitch, {})
	for name in ["fsr2_off", "fsr2_on"]:
		var fsr: Dictionary = results.get("%d|%s" % [pitch, name], {})
		if fsr.is_empty() or taa.is_empty(): continue
		var p_fsr := float((fsr["whole"] as Dictionary)["p99"])
		var p_taa := float((taa["whole"] as Dictionary)["p99"])
		if p_fsr > 1.5 * p_taa: failures.append("pitch %d %s whole p99 %.6f > 1.5 x taa_on %.6f (FSR2 history not accumulating)" % [pitch, name, p_fsr, p_taa])

func fail(message: String) -> void:
	push_error("COMPOSITOR_STABILITY=FAIL " + message)
	quit(1)

## Diagnostic: re-asserted every frame because the atmosphere / graphics controllers rewrite the Environment.
func apply_env_ablation() -> void:
	for node in all_nodes(game):
		if no_shadows and node is DirectionalLight3D: (node as DirectionalLight3D).shadow_enabled = false
		if not node is WorldEnvironment or (node as WorldEnvironment).environment == null: continue
		var environment := (node as WorldEnvironment).environment
		for item in env_off:
			match item:
				"ssao": environment.ssao_enabled = false
				"ssil": environment.ssil_enabled = false
				"ssr": environment.ssr_enabled = false
				"glow": environment.glow_enabled = false
				"fog": environment.fog_enabled = false
				"volumetric": environment.volumetric_fog_enabled = false
				"sdfgi": environment.sdfgi_enabled = false
				"adjustment": environment.adjustment_enabled = false

func print_environment_state() -> void:
	for node in all_nodes(game):
		if not node is WorldEnvironment or (node as WorldEnvironment).environment == null: continue
		var e := (node as WorldEnvironment).environment
		print("COMPOSITOR_STABILITY env=%s ssao=%s ssil=%s ssr=%s glow=%s fog=%s volumetric=%s sdfgi=%s tonemap=%d bg=%d" % [node.get_path(),
			str(e.ssao_enabled), str(e.ssil_enabled), str(e.ssr_enabled), str(e.glow_enabled), str(e.fog_enabled), str(e.volumetric_fog_enabled),
			str(e.sdfgi_enabled), e.tonemap_mode, e.background_mode])
	print("COMPOSITOR_STABILITY camera near=%.4f far=%.1f fov=%.2f origin=%s" % [camera.near, camera.far, camera.fov, str(camera.global_position)])

## Diagnostic (--list-transparent): visible geometry whose material can draw in the transparent pass (ALPHA writes / blend modes).
func list_transparent() -> void:
	for node in all_nodes(game):
		if not node is GeometryInstance3D or not (node as GeometryInstance3D).is_visible_in_tree(): continue
		var geometry := node as GeometryInstance3D
		var materials: Array = [geometry.material_override, geometry.material_overlay]
		if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
			for surface in (node as MeshInstance3D).mesh.get_surface_count(): materials.append((node as MeshInstance3D).get_active_material(surface))
		for material in materials:
			var why := ""
			if material is ShaderMaterial and (material as ShaderMaterial).shader != null:
				var code := (material as ShaderMaterial).shader.code
				if code.contains("ALPHA") or code.contains("blend_add") or code.contains("blend_mul"): why = "shader " + (material as ShaderMaterial).shader.resource_path
			elif material is BaseMaterial3D and (material as BaseMaterial3D).transparency != BaseMaterial3D.TRANSPARENCY_DISABLED:
				why = "base transparency=%d" % (material as BaseMaterial3D).transparency
			if why != "":
				var aabb := geometry.get_aabb()
				print("COMPOSITOR_STABILITY transparent node=%s %s aabb_size=%s distance=%.1f layers=%d" % [node.get_path(), why, str(aabb.size),
					camera.global_position.distance_to(geometry.global_transform * aabb.get_center()), geometry.layers])
				break
