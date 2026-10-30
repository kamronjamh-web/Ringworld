extends SceneTree

# Rain streaks and splashes GPU test (docs/storm-revamp-s1-design.md sections 2, 3 and 6; WO5/WO6). Windowed Vulkan, production world, real Sunshine clouds.
#   Godot --path . --resolution 960x540 --script res://tools/rain_streaks_gpu_test.gd [-- --out <dir> --smoke]
# The four user complaints are hard acceptance checks here:
#   always_lit      streaks are dark at night and in storm shade, bright in sun, and flash only on a lightning pulse
#   clouds_over     near streaks are drawn after the cloud composite: with an opaque cloud deck overhead they still show, and a negative control that
#                   moves the pass before Sunshine makes them vanish (so the check can fail)
#   clear_sky       at a storm edge streaks exist only on the storm side
#   wind            streak slant tracks wind speed and the gust field
# Plus: paused clock gives identical frames, a roof removes the streaks under it, splashes on open ground only, tier ordering and the preset budget.
# Prints RAIN_STREAKS_GPU=PASS or =FAIL <reason>; each check prints a RAIN_STREAKS line.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
var world: Node
var driver: RefCounted
var local: Node
var camera: Camera3D
var out_dir := ""
var failures: Array[String] = []
var finished := false
var checks_done := false
var axes: Dictionary
var eye := Vector3.ZERO
var viewport: SubViewport
var rig: Node3D
var rig_env: WorldEnvironment
var fake_clouds: CompositorEffect
const SLAB_HEIGHT := 3.0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_finish("FAIL requires Vulkan/Forward+ (not headless)"); return
	run.call_deferred()

func check(name: String, ok: bool, detail := "") -> void:
	print("RAIN_STREAKS %s %s %s" % [name, "PASS" if ok else "FAIL", detail])
	if not ok: failures.append(name + " " + detail)

func _finish(message: String) -> void:
	if finished: return
	finished = true
	var verdict := message
	if verdict == "": verdict = "PASS" if failures.is_empty() else "FAIL " + " | ".join(failures)
	if verdict.begins_with("PASS"): print("RAIN_STREAKS_GPU=PASS")
	else: push_error("RAIN_STREAKS_GPU=" + verdict)
	await teardown()
	quit(0 if verdict.begins_with("PASS") else 1)

# Frees the rig viewport and the production world while the renderer is still running; quitting with them alive crashed the engine at exit (0xC0000005 after PASS).
func teardown() -> void:
	if is_instance_valid(camera) and is_instance_valid(world): camera.reparent(world.get_node("Explorer/Head"), false)
	if is_instance_valid(viewport): viewport.queue_free()
	if is_instance_valid(world): world.queue_free()
	for i in 6: await process_frame

func step(count: int) -> void:
	for i in count: local.call("_Process", 1.0 / 60.0)

func frames(count: int) -> void:
	for i in count: await process_frame

func capture(label: String) -> Image:
	step(1)
	await frames(4)
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image.is_empty(): _finish("FAIL empty capture " + label); return image
	image.save_png(out_dir + "/" + label + ".png")
	return image

func luma(c: Color) -> float:
	return c.r * .2126 + c.g * .7152 + c.b * .0722

# Clips a sampling region to both captures, so a region sized for another resolution never reads past the image edge.
func clip(a: Image, b: Image, region: Rect2i) -> Rect2i:
	return region.intersection(Rect2i(Vector2i.ZERO, a.get_size())).intersection(Rect2i(Vector2i.ZERO, b.get_size()))

# Pixels whose luma changed by more than the threshold between two captures, inside a region.
func changed_pixels(a: Image, b: Image, region: Rect2i, threshold := 0.01) -> int:
	region = clip(a, b, region)
	var count := 0
	for y in range(region.position.y, region.end.y):
		for x in range(region.position.x, region.end.x):
			if absf(luma(a.get_pixel(x, y)) - luma(b.get_pixel(x, y))) > threshold: count += 1
	return count

func mean_abs_delta(a: Image, b: Image, region: Rect2i) -> float:
	region = clip(a, b, region)
	var total := 0.0
	for y in range(region.position.y, region.end.y, 2):
		for x in range(region.position.x, region.end.x, 2): total += absf(luma(a.get_pixel(x, y)) - luma(b.get_pixel(x, y)))
	return total / maxf(1.0, region.size.x * region.size.y / 4.0)

# Aims the production camera: eye at the explorer, looking along `forward` with world up (the ring up axis).
func aim(forward: Vector3, pitch_degrees := 8.0) -> void:
	var up: Vector3 = axes.up
	var f := (forward - up * forward.dot(up)).normalized()
	var right := f.cross(up).normalized()
	var look := (f * cos(deg_to_rad(pitch_degrees)) + up * sin(deg_to_rad(pitch_degrees))).normalized()
	var basis := Basis(right, right.cross(look).normalized(), -look).orthonormalized()
	camera.global_transform = Transform3D(basis, eye)

func run() -> void:
	create_timer(900.0, true, false, true).timeout.connect(func(): _finish("FAIL watchdog expired"))
	var args := OS.get_cmdline_user_args()
	out_dir = ProjectSettings.globalize_path("res://.godot/storm_s1/streaks_%d" % OS.get_process_id())
	for i in args.size():
		if args[i] == "--out" and i + 1 < args.size(): out_dir = args[i + 1]
	DirAccess.make_dir_recursive_absolute(out_dir)
	print("RAIN_STREAKS device=", RenderingServer.get_video_adapter_name())
	world = WORLD.instantiate()
	var isolated: Dictionary = ISOLATION.new().ExecuteFixture("isolation", world, "res://.godot/storm_s1/prefs_%d" % OS.get_process_id())
	if not bool(isolated.get("pass", false)): _finish("FAIL isolation " + str(isolated)); return
	root.add_child(world)
	driver = DRIVER.new()
	await frames(12)
	local = world.get_node("LocalWeatherEffects")
	local.set_process(false); local.set("EnableLightning", false)
	var explorer := world.get_node("Explorer")
	explorer.set_physics_process(false); explorer.set_process(false)
	for name in ["LiveTuningDrawer", "TerrainMinimap", "PresentationHud", "LodDebugHud", "CelestialDebugHud", "LodPauseMenu"]:
		var overlay := world.get_node_or_null(name) as CanvasLayer
		if overlay != null: overlay.visible = false
	world.get_node("RingDayNightCycle").set("Paused", true)
	driver.call("SetDaylightForFixture", world.get_node("RingDayNightCycle"), 1.0)
	driver.call("SetShelterForFixture", world.get_node("WeatherAmbience"), false)
	driver.call("SetUnderwaterForFixture", explorer, false)
	camera = world.get_node("Explorer/Head/Camera3D")
	world.get_node("PrecipitationOcclusion").set("CameraSource", camera)   # the map observer is the viewport camera, which now lives in the rig
	# The camera renders a static rig world (flat colour sky, one slab) so frames differ only by what the test changes; the production world stays in the
	# root viewport and supplies weather, wind, the occlusion map producer and the explorer frame. A matching slab and roofs exist in both worlds.
	viewport = SubViewport.new()
	viewport.size = Vector2i(1920, 1080) if "--perf" in args else (Vector2i(1280, 720) if "--movie" in args else Vector2i(960, 540)); viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	full = Rect2i(Vector2i.ZERO, viewport.size)
	rig = Node3D.new(); viewport.add_child(rig)
	var rig_environment := Environment.new()
	rig_environment.background_mode = Environment.BG_COLOR
	rig_environment.background_color = Color(.42, .5, .6)
	rig_environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	rig_env = WorldEnvironment.new(); rig_env.environment = rig_environment; rig.add_child(rig_env)
	camera.reparent(rig, false); camera.transform = Transform3D.IDENTITY
	camera.fov = 80; camera.near = .1; camera.far = 2000; camera.make_current()
	driver.call("SetWindSettingsForFixture", 0.0, 1.0, 600.0)
	driver.call("SetIntensity", local, 1.0, false, Vector2(0, 0))
	step(220)
	for name in (["Explorer", "SunshineCloudBridge", "SunshineCloudsDriver", "WeatherAmbience", "DistantRainCurtains"] if "--movie" in args else ["Explorer", "RingVolumetricClouds", "SunshineCloudBridge", "SunshineCloudsDriver", "WeatherAmbience", "DistantRainCurtains"]):
		var node := world.get_node_or_null(name)
		if node != null: node.set_process(false); node.set_physics_process(false)
	await frames(30)
	axes = driver.call("FrameAxes", world)
	var first_axes: Dictionary = axes
	eye = Vector3(axes.origin) + Vector3(axes.up) * (SLAB_HEIGHT + 1.7)
	add_box_pair(Vector3(axes.origin) + Vector3(axes.up) * SLAB_HEIGHT, 400.0, 1.0, Color(.18, .2, .18))
	aim(Vector3(axes.across))
	step(1)
	await frames(6)
	var diag: Dictionary = local.call("GetRainStreakDiagnostics")
	print("RAIN_STREAKS diagnostics ", diag)
	check("compositor_ready", bool(diag.compositor_present) and bool(diag.compositor_ready) and String(diag.compositor_error) == "", str(diag))
	check("chain_order", bool(diag.in_chain), "streaks directly before the rain haze, haze last")
	if "--movie" in OS.get_cmdline_user_args():
		await movie()
		checks_done = true
		_finish("")
		return
	if "--perf" in OS.get_cmdline_user_args():
		await perf()
		checks_done = true
		_finish("")
		return
	await checks()
	if not checks_done: failures.append("checks aborted by a script error")
	_finish("")

# ---------------------------------------------------------------- helpers
var full := Rect2i(0, 0, 960, 540)
var bridge: Node
var cloud: Resource

# Streak pixels = pixels that differ between the rain frame and the same frame with streaks switched off (haze, clouds and splashes stay identical).
func pair(label: String, region := Rect2i(0, 0, 0, 0)) -> Dictionary:
	driver.call("SetStreakSettingForFixture", "StreakDensity", 1.0)
	var on := await capture(label + "_on")
	driver.call("SetStreakSettingForFixture", "StreakDensity", 0.0)
	var off := await capture(label + "_off")
	driver.call("SetStreakSettingForFixture", "StreakDensity", 1.0)
	var r := region if region.size.x > 0 else Rect2i(Vector2i.ZERO, on.get_size())   # the rig viewport, not the root window (which follows the profile's window size)
	return {"on": on, "off": off, "count": changed_pixels(on, off, r), "mean": mean_abs_delta(on, off, r)}

func set_day(day: float) -> void:
	driver.call("SetDaylightForFixture", world.get_node("RingDayNightCycle"), day)

func rain(intensity: float, wind := Vector2.ZERO) -> void:
	driver.call("SetIntensity", local, intensity, false, wind)
	step(220)

# Median streak slant from vertical in degrees (positive = head displaced to the image right). Each isolated, elongated blob of the streak difference mask is one streak;
# its principal axis gives the slant. (A gradient structure tensor is biased towards the pixel diagonals for 1.5 px lines.)
func slant_angles(on: Image, off: Image, region: Rect2i) -> Array[float]:
	region = clip(on, off, region)
	var w := region.size.x; var h := region.size.y
	var mask := PackedByteArray(); mask.resize(w * h)
	for y in h:
		for x in w:
			if absf(luma(on.get_pixel(region.position.x + x, region.position.y + y)) - luma(off.get_pixel(region.position.x + x, region.position.y + y))) > 0.006: mask[y * w + x] = 1
	var angles: Array[float] = []
	for start in w * h:
		if mask[start] != 1: continue
		var stack: Array[int] = [start]; mask[start] = 2
		var points: Array[int] = []
		while not stack.is_empty():
			var p: int = stack.pop_back(); points.append(p)
			var px := p % w; var py := p / w
			for dy in range(-1, 2):
				for dx in range(-1, 2):
					var nx := px + dx; var ny := py + dy
					if nx < 0 or ny < 0 or nx >= w or ny >= h: continue
					var q := ny * w + nx
					if mask[q] == 1: mask[q] = 2; stack.append(q)
		if points.size() < 10 or points.size() > 600: continue
		var mx := 0.0; var my := 0.0
		for p in points: mx += p % w; my += p / w
		mx /= points.size(); my /= points.size()
		var sxx := 0.0; var syy := 0.0; var sxy := 0.0
		for p in points:
			var ddx := (p % w) - mx; var ddy := (p / w) - my
			sxx += ddx * ddx; syy += ddy * ddy; sxy += ddx * ddy
		var disc := sqrt((sxx - syy) * (sxx - syy) * 0.25 + sxy * sxy)
		var l1 := (sxx + syy) * 0.5 + disc; var l2 := (sxx + syy) * 0.5 - disc
		if l2 > 0.0 and l1 / l2 < 10.0: continue
		var phi := 0.5 * atan2(2.0 * sxy, sxx - syy)
		angles.append(rad_to_deg(PI * 0.5 - phi) if phi > 0.0 else -rad_to_deg(PI * 0.5 + phi))
	return angles

func slant_degrees(on: Image, off: Image, region: Rect2i) -> float:
	var angles := slant_angles(on, off, region)
	if angles.size() < 5: return NAN
	angles.sort()
	return angles[angles.size() / 2]

# Interquartile range of the individual streak slants: gusts spread it, a steady wind keeps it tight.
func strip_spread(pair_images: Dictionary) -> float:
	var angles := slant_angles(pair_images.on, pair_images.off, Rect2i(0, 20, 960, 500))
	if angles.size() < 12: return 0.0
	angles.sort()
	return angles[angles.size() * 3 / 4] - angles[angles.size() / 4]


# A flat box in BOTH worlds: the root world feeds the occlusion map, the rig world is what the camera sees. Returns [root copy, rig copy].
func add_box_pair(top_centre: Vector3, half_size: float, thickness: float, color: Color) -> Array:
	var pair_nodes := []
	var up: Vector3 = axes.up
	for parent: Node in [root, rig]:
		var instance := MeshInstance3D.new(); var box := BoxMesh.new(); box.size = Vector3(half_size * 2.0, thickness, half_size * 2.0); instance.mesh = box
		var material := StandardMaterial3D.new(); material.albedo_color = color; material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED; instance.material_override = material
		parent.add_child(instance)
		instance.global_transform = Transform3D(Basis(Vector3(axes.around), up, Vector3(axes.across)).orthonormalized(), top_centre - up * (thickness * 0.5))
		pair_nodes.append(instance)
	return pair_nodes

func add_roof(half_size: float, height: float) -> Array:
	return add_box_pair(Vector3(axes.origin) + Vector3(axes.up) * (SLAB_HEIGHT + height), half_size, 0.4, Color(.3, .3, .32))

func free_pair(nodes: Array) -> void:
	for node: Node in nodes: node.queue_free()

# Models Sunshine's composite: it blends cloud over the whole resolved colour after the transparent pass. Anything drawn earlier is covered by it.
class FakeClouds extends CompositorEffect:
	var rd: RenderingDevice
	var shader: RID
	var pipeline: RID
	var amount := 0.85
	func _init() -> void:
		effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
		access_resolved_color = true
	func _render_callback(_type: int, render_data: RenderData) -> void:
		if rd == null: rd = RenderingServer.get_rendering_device()
		if not shader.is_valid():
			var source := RDShaderSource.new(); source.language = RenderingDevice.SHADER_LANGUAGE_GLSL
			source.source_compute = "#version 450\nlayout(local_size_x=8,local_size_y=8) in;\nlayout(rgba16f,set=0,binding=0) uniform image2D img;\nlayout(push_constant) uniform P{float a;float b;float c;float d;} pc;\nvoid main(){ivec2 p=ivec2(gl_GlobalInvocationID.xy); if(any(greaterThanEqual(p,imageSize(img)))) return; vec4 c=imageLoad(img,p); imageStore(img,p,vec4(mix(c.rgb,vec3(.78,.8,.84),pc.a),c.a));}"
			var spirv := rd.shader_compile_spirv_from_source(source)
			shader = rd.shader_create_from_spirv(spirv)
			pipeline = rd.compute_pipeline_create(shader)
		var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
		var size := buffers.get_internal_size()
		var color := buffers.get_color_layer(0, false)
		var uniform := RDUniform.new(); uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; uniform.binding = 0; uniform.add_id(color)
		var set := UniformSetCacheRD.get_cache(shader, 0, [uniform])
		var push := PackedFloat32Array([amount, 0, 0, 0]).to_byte_array()
		var list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(list, pipeline)
		rd.compute_list_bind_uniform_set(list, set, 0)
		rd.compute_list_set_push_constant(list, push, push.size())
		rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1)
		rd.compute_list_end()

func wait_map(previous: int) -> bool:
	var occlusion := world.get_node("PrecipitationOcclusion")
	occlusion.call("RequestRender")
	for i in 240:
		await process_frame
		if int(occlusion.get("RenderCount")) > previous and bool(occlusion.get("Ready")): return true
	return false

# Capture without advancing the weather node (keeps the payload and compositor order exactly as the test left them).
func capture_raw(label: String) -> Image:
	await frames(5)
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	image.save_png(out_dir + "/" + label + ".png")
	return image

# ---------------------------------------------------------------- checks
func checks() -> void:
	# Freeze the production world's own animation; the occlusion producer keeps running (it re-renders on request).
	freeze_scene(world)
	if "--no-map" in OS.get_cmdline_user_args(): world.get_node("PrecipitationOcclusion").set("Enabled", false)
	world.get_node("PrecipitationOcclusion").set_process(true)
	await frames(20)
	var up: Vector3 = axes.up
	var around: Vector3 = axes.around
	var open_forward := up.cross(around)   # camera right = +around
	aim(open_forward, 6.0)
	rain(1.0)
	driver.call("SetHazeControl", "Enabled", false)
	step(60)   # let the first field sweep finish
	await frames(10)
	var quiet_a := await capture("static_a")
	var quiet_b := await capture("static_b")
	check("scene_static", changed_pixels(quiet_a, quiet_b, full) < 40, "pixels changing between identical frames=%d" % changed_pixels(quiet_a, quiet_b, full))
	var day := await pair("day")
	check("streaks_draw", int(day.count) > 400, "changed_pixels=%d mean=%.5f" % [day.count, day.mean])
	print("RAIN_STREAKS diagnostics_after ", local.call("GetRainStreakDiagnostics"), " haze ", local.call("GetRainHazeDiagnostics"))
	if "--smoke" in OS.get_cmdline_user_args():
		checks_done = true; return

	# ---- determinism: a paused clock gives identical frames
	var again := await capture("day_on_again")
	var paused_delta := mean_abs_delta(day.on, again, full)
	check("paused_clock_identical", paused_delta < 0.000001, "mean delta=%.9f" % paused_delta)

	# ---- tiers: visible rain grows with intensity
	var previous := 0
	for amount: float in [.15, .5, 1.0]:
		rain(amount)
		var t := await pair("tier_%d" % int(amount * 100))
		check("tier_%d" % int(amount * 100), int(t.count) > previous, "changed_pixels=%d (previous %d)" % [t.count, previous])
		previous = int(t.count)
	# ---- preset budget: more budget draws more
	rain(1.0)
	var budget_counts := []
	for budget: int in [2000, 6000, 12000]:
		local.set("MaximumStreakCount", budget)
		var b := await pair("budget_%d" % budget)
		budget_counts.append(int(b.count))
	check("budget_monotonic", budget_counts[0] < budget_counts[1] and budget_counts[1] < budget_counts[2], str(budget_counts))
	local.set("MaximumStreakCount", 6000)

	# ---- complaint 1: always lit. Dark at night and in storm shade, flash only on a lightning pulse.
	rain(1.0)
	var lit_day := await pair("lit_day")
	set_day(0.0)
	var night := await pair("lit_night")
	check("lit_night_dark", float(night.mean) < float(lit_day.mean) * 0.06 + 0.00005, "night mean=%.6f day mean=%.6f" % [night.mean, lit_day.mean])
	driver.call("SetLightningPulseForFixture", local, 1.0, 1.0)
	var flash := await pair("lit_night_flash")
	driver.call("SetLightningPulseForFixture", local, 0.0, 0.0)
	check("lit_flash_only_on_pulse", float(flash.mean) > float(night.mean) * 4.0 + 0.0003 and float(flash.mean) > float(lit_day.mean) * 0.2, "flash mean=%.6f night mean=%.6f day mean=%.6f" % [flash.mean, night.mean, lit_day.mean])
	set_day(1.0)
	driver.call("SetStormLevelForFixture", local, 1.0)
	var shade := await pair("lit_storm_shade")
	driver.call("SetStormLevelForFixture", local, -1.0)
	check("lit_storm_shade_dark", float(shade.mean) < float(lit_day.mean) * 0.45, "shade mean=%.6f day mean=%.6f" % [shade.mean, lit_day.mean])

	# ---- complaint 2: clouds never overdraw near rain. A cloud composite (blend over the whole resolved colour, as Sunshine does) runs first; the streak pass stays
	# on top of it. Negative control: the same pass moved in front of the cloud composite is covered by it, so this check can fail.
	aim(open_forward, 28.0)
	var sky_region := Rect2i(0, 0, 960, 200)
	var streak_effect: CompositorEffect = local.get("RainStreaksCompositor")
	fake_clouds = FakeClouds.new()
	var effects := rig_env.compositor.compositor_effects
	effects.push_front(fake_clouds)
	rig_env.compositor.compositor_effects = effects
	await frames(3)
	var chain := await pair("clouds_sky", sky_region)
	effects = rig_env.compositor.compositor_effects
	effects.erase(streak_effect); effects.push_front(streak_effect)
	rig_env.compositor.compositor_effects = effects
	var control_on := await capture_raw("clouds_control_on")
	driver.call("SetStreakSettingForFixture", "StreakDensity", 0.0)
	step(1)   # publishes the streaks-off frame (and would restore the chain order), so reorder again afterwards
	effects = rig_env.compositor.compositor_effects
	effects.erase(streak_effect); effects.push_front(streak_effect)
	rig_env.compositor.compositor_effects = effects
	var control_off := await capture_raw("clouds_control_off")
	driver.call("SetStreakSettingForFixture", "StreakDensity", 1.0)
	var control_count := changed_pixels(control_on, control_off, sky_region)
	check("clouds_over_rain", int(chain.count) > 150, "streak pixels over the cloud deck=%d" % chain.count)
	check("clouds_control_hides_rain", control_count < int(chain.count) * 0.4, "streak pixels with the pass before the cloud composite=%d vs after=%d" % [control_count, chain.count])
	effects = rig_env.compositor.compositor_effects
	effects.erase(fake_clouds); rig_env.compositor.compositor_effects = effects
	step(2)   # EnsureRainHazeCompositorLast restores the order
	await frames(3)
	check("chain_restored", bool(local.call("GetRainStreakDiagnostics").in_chain), "")

	# ---- complaint 4: wind. Slant tracks wind speed and sign, and the gust field.
	aim(open_forward, 4.0)
	var half_left := Rect2i(40, 40, 400, 460)
	var half_right := Rect2i(520, 40, 400, 460)
	var slants := {}
	for wind: float in [0.0, 9.0, 18.0, 36.0, -18.0]:
		rain(1.0, Vector2(wind, 0.0))
		var w := await pair("wind_%d" % int(wind))
		slants[wind] = slant_degrees(w.on, w.off, full)
	print("RAIN_STREAKS wind slant degrees ", slants)
	check("wind_calm_vertical", absf(slants[0.0]) < 6.0, "slant at 0 m/s=%.1f" % slants[0.0])
	check("wind_tracks_speed", slants[0.0] < slants[9.0] and slants[9.0] < slants[18.0] and slants[18.0] < slants[36.0], "slants=%s" % str(slants))
	check("wind_strong_storm_slanted", slants[36.0] > 48.0 and slants[18.0] > 35.0, "18 m/s=%.1f deg 36 m/s=%.1f deg" % [slants[18.0], slants[36.0]])
	check("wind_sign", slants[-18.0] < -15.0 and absf(slants[-18.0] + slants[18.0]) < 12.0, "-18 m/s=%.1f +18 m/s=%.1f" % [slants[-18.0], slants[18.0]])
	# Gusts: with a strongly varying gust field the slant differs across the view; without it the two halves match.
	rain(1.0, Vector2(14.0, 0.0))
	var calm := await pair("gust_off")
	var calm_spread := strip_spread(calm)
	driver.call("SetWindSettingsForFixture", 1.6, 0.05, 48.0)
	step(30); await frames(8)
	var gusty := await pair("gust_on")
	var gust_spread := strip_spread(gusty)
	driver.call("SetWindSettingsForFixture", 0.0, 1.0, 600.0)
	check("wind_gust_field_varies_slant", gust_spread > calm_spread + 5.0, "slant IQR (deg) without gusts=%.1f with gusts=%.1f" % [calm_spread, gust_spread])

	# ---- complaint 3: no rain from a clear sky. At a storm edge streaks exist only on the storm side.
	aim(open_forward, 6.0)
	rain(1.0)
	var uniform := await pair("edge_control")
	var left_third := Rect2i(0, 0, 320, 540)
	var right_third := Rect2i(640, 0, 320, 540)
	var uniform_left := changed_pixels(uniform.on, uniform.off, left_third)
	var uniform_right := changed_pixels(uniform.on, uniform.off, right_third)
	check("edge_control_both_sides", uniform_left > 100 and uniform_right > 100, "uniform rain left=%d right=%d" % [uniform_left, uniform_right])
	# Plumbing check: inject a field that is dry on the -around side and heavy on the +around side, independent of the weather query.
	var injected := PackedFloat32Array()   # plumbing: centre column dry
	for row in 5: injected.append_array(PackedFloat32Array([0.0, 0.0, 0.0, 1.0, 1.0]))
	driver.call("SetStreakFieldForFixture", local, injected)
	var field_pair := await pair("edge_injected")
	var field_left := changed_pixels(field_pair.on, field_pair.off, left_third)
	var field_right := changed_pixels(field_pair.on, field_pair.off, right_third)
	driver.call("SetStreakFieldForFixture", local, PackedFloat32Array())
	check("field_gates_drop_columns", field_right > 300 and field_left * 4 < field_right, "injected field left=%d right=%d" % [field_left, field_right])
	driver.call("RegisterEdgeStorm", world, 140.0, 100.0)
	# The rig freezes the Sunshine bridge, so its uploaded weather systems (the macro mirror the field normally reads) would not see the new storm: use the per-cell weather query here.
	# The mirror itself is checked against that query in the real world (tools/rain_real_world_capture.gd --mode=cpu, RAIN_REAL_MIRROR).
	driver.call("SetStreakGridMirrorForFixture", local, false)
	await frames(4)
	var centre_potential := float(driver.call("PotentialAt", world, 0.0))
	var clear_potential := float(driver.call("PotentialAt", world, -40.0))
	var storm_potential := float(driver.call("PotentialAt", world, 40.0))
	print("RAIN_STREAKS edge potentials clear=%.3f centre=%.3f storm=%.3f" % [clear_potential, centre_potential, storm_potential])
	driver.call("SetPotentialOverride", local, centre_potential, Vector2.ZERO)   # the observer stands in clear air (potential 0); the storm starts a few metres to the right
	step(300)
	var edge := await pair("edge_storm")
	var sd: Dictionary = local.call("GetRainStreakDiagnostics")
	print("RAIN_STREAKS edge grid ", sd.grid, " intensity ", local.call("GetPrecipitationDiagnostics").shared_intensity)
	var edge_left := changed_pixels(edge.on, edge.off, left_third)
	var edge_right := changed_pixels(edge.on, edge.off, right_third)
	check("edge_only_storm_side", edge_right > 60 and edge_left * 4 < edge_right, "left (clear side)=%d right (storm side)=%d potentials %.2f/%.2f/%.2f" % [edge_left, edge_right, clear_potential, centre_potential, storm_potential])
	driver.call("UnregisterEdgeStorm", world)
	driver.call("SetStreakGridMirrorForFixture", local, true)
	rain(1.0)

	await storm_arrival_checks(open_forward)

	# ---- shelter: a roof removes the streaks under it; splashes land on open ground only
	aim(open_forward, 6.0)
	var open_pair := await pair("roof_open")
	var occlusion := world.get_node("PrecipitationOcclusion")
	var renders := int(occlusion.get("RenderCount"))
	var roof: Array = add_roof(90.0, 5.0)
	var mapped := await wait_map(renders)
	check("roof_map_ready", mapped, "occlusion map re-rendered")
	await frames(10)
	var covered := await pair("roof_covered")
	check("roof_removes_streaks", int(covered.count) < int(open_pair.count) * 0.08, "under roof=%d open=%d" % [covered.count, open_pair.count])
	free_pair(roof)
	renders = int(occlusion.get("RenderCount"))
	await wait_map(renders)
	await frames(10)
	# Splashes: look down at open ground, then again under the roof.
	aim(open_forward, -32.0)
	rain(1.0)
	driver.call("SetStreakSettingForFixture", "SplashAmount", 2.0)
	var splash_on := await capture("splash_on")
	driver.call("SetStreakSettingForFixture", "SplashAmount", 0.0)
	var splash_off := await capture("splash_off")
	var splash_open := changed_pixels(splash_on, splash_off, full, 0.004)
	check("splashes_on_open_ground", splash_open > 25, "splash pixels=%d" % splash_open)
	roof = add_roof(90.0, 5.0)
	renders = int(occlusion.get("RenderCount"))
	await wait_map(renders)
	await frames(10)
	driver.call("SetStreakSettingForFixture", "SplashAmount", 2.0)
	var covered_on := await capture("splash_covered_on")
	driver.call("SetStreakSettingForFixture", "SplashAmount", 0.0)
	var covered_off := await capture("splash_covered_off")
	var splash_covered := changed_pixels(covered_on, covered_off, full, 0.004)
	check("no_splashes_under_roof", splash_covered * 6 < splash_open, "covered=%d open=%d" % [splash_covered, splash_open])
	driver.call("SetStreakSettingForFixture", "SplashAmount", 1.0)
	free_pair(roof)
	print("RAIN_STREAKS output=", out_dir)
	checks_done = true

# S3 hooks (WO-C1 fat drops, WO-C2 hail): visible when forced, nothing at weight 0, and no hail under a roof.
func storm_arrival_checks(open_forward: Vector3) -> void:
	aim(open_forward, 6.0)
	rain(1.0)
	var baseline := await capture("storm_identity_base")
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, false)
	var zeroed := await capture("storm_identity_zero")
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, true)
	check("arrival_weights_zero_identity", mean_abs_delta(baseline, zeroed, full) < 0.000001, "mean delta=%.9f" % mean_abs_delta(baseline, zeroed, full))
	# Fat drops below the rain start threshold: a dry sky with the skirt weight draws sparse drops; without the weight nothing.
	rain(0.0)
	var dry := await pair("fat_dry")
	driver.call("SetStreakArrivalForFixture", local, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, false)
	var fat := await pair("fat_on")
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, true)
	print("RAIN_STREAKS fat dry=%d fat=%d diagnostics %s" % [dry.count, fat.count, str(local.call("GetRainStreakDiagnostics").get("fat_drop"))])
	check("fat_drops_below_threshold", int(dry.count) < 30 and int(fat.count) > 120 and int(fat.count) * 8 < 63000, "dry=%d fat=%d (sparse: well under heavy rain)" % [dry.count, fat.count])
	# Hail pellets in heavy rain: the pellet class draws extra pixels; looking down shows bounce and puff pixels.
	rain(1.0)
	var no_hail := await capture("hail_off")
	driver.call("SetStreakArrivalForFixture", local, 0.0, 1.0, 0.0, 1.0, 1.0, 0.6, false)
	var hail_on := await capture("hail_on")
	var hail_changed := changed_pixels(no_hail, hail_on, full)
	check("hail_pellets_draw", hail_changed > 400, "changed pixels=%d" % hail_changed)
	aim(open_forward, -32.0)
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, false)
	var down_off := await capture("hail_down_off")
	driver.call("SetStreakArrivalForFixture", local, 0.0, 1.0, 0.0, 1.0, 1.0, 0.6, false)
	var down_on := await capture("hail_down_on")
	var down_changed := changed_pixels(down_off, down_on, full, 0.004)
	check("hail_bounces_on_ground", down_changed > 250, "changed pixels looking down=%d" % down_changed)
	# Under a roof: hail pellets and puffs vanish with the streaks.
	var occlusion := world.get_node("PrecipitationOcclusion")
	var renders := int(occlusion.get("RenderCount"))
	var roof: Array = add_roof(90.0, 5.0)
	await wait_map(renders)
	await frames(10)
	var roof_on := await capture("hail_roof_on")
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, false)
	var roof_off := await capture("hail_roof_off")
	var roof_changed := changed_pixels(roof_off, roof_on, full, 0.004)
	check("no_hail_under_roof", roof_changed * 5 < down_changed, "under roof=%d open=%d" % [roof_changed, down_changed])
	free_pair(roof)
	driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, true)
	renders = int(occlusion.get("RenderCount"))
	await wait_map(renders)
	await frames(10)
	aim(open_forward, 6.0)
	rain(1.0)

func freeze_scene(node: Node) -> void:
	if node == local: return
	node.set_process(false); node.set_physics_process(false)
	for child in node.get_children(): freeze_scene(child)

# ---------------------------------------------------------------- perf (A-B-A) : pass --perf
# GPU time of the streak + splash pass from the real asynchronous timestamp markers (GPU.RainStreaks), heavy rain, open field, day. A = streaks on, B = streaks off
# (the pass does not run at all), A again; the A runs bracket the B run so drift shows. Prints RAIN_STREAKS_PERF lines.
func perf() -> void:
	freeze_scene(world)
	world.get_node("PrecipitationOcclusion").set_process(true)
	await frames(20)
	driver.call("ConfigureRainGpuProfilerForFixture", true)
	driver.call("SetHazeControl", "Enabled", false)
	aim(Vector3(axes.up).cross(Vector3(axes.around)), 6.0)
	var results := {}
	for budget: int in [6000, 12000]:
		local.set("MaximumStreakCount", budget)
		rain(1.0, Vector2(14.0, 0.0))
		var series: Array = []
		for phase in ["A", "S0", "A2"]:
			driver.call("SetStreakSettingForFixture", "StreakDensity", 0.0 if phase == "B" else 1.0)
			driver.call("SetStreakSettingForFixture", "SplashAmount", 0.0 if phase == "S0" else 1.0)
			# Z: the arrival override forced to zero weights (no fat drops, no hail), so a natural storm at the fixture cannot colour the A numbers.
			driver.call("SetStreakArrivalForFixture", local, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, phase != "Z")
			await frames(20)
			driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks")
			var samples: Array = []
			for i in 240:
				step(1)
				await process_frame
				driver.call("RequestRainGpuReadbackForFixture")
				if i % 8 == 7: samples.append_array(driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks"))
			await frames(6)
			samples.append_array(driver.call("DrainGpuMillisecondsForFixture", "GPU.RainStreaks"))
			samples.sort()
			var median := 0.0 if samples.is_empty() else float(samples[samples.size() / 2])
			var p95 := 0.0 if samples.is_empty() else float(samples[mini(samples.size() - 1, int(samples.size() * 0.95))])
			var pd: Dictionary = local.call("GetRainStreakDiagnostics")
			print("RAIN_STREAKS_PERF budget=%d phase=%s samples=%d median_ms=%.4f p95_ms=%.4f grid_ms=%.3f fat=%.3f hail=%.3f near=%d mid=%d" % [budget, phase, samples.size(), median, p95, float(pd.grid_ms), float(pd.fat_drop), float(pd.hail), int(pd.near_instances), int(pd.mid_instances)])
			series.append(median)
		results[budget] = series
	driver.call("SetStreakSettingForFixture", "StreakDensity", 1.0)
	print("RAIN_STREAKS_PERF summary ", results)

# ---------------------------------------------------------------- movie : pass --movie, record with --write-movie <png> --fixed-fps 30 --resolution 1280x720
# Heavy storm rain: open field, walk under a roof, look around, walk back out. The rig viewport is shown full screen so the movie writer records it. The shared clock runs
# (drops fall, gusts travel); the observer of the lattice follows the walking camera.
func movie() -> void:
	var layer := CanvasLayer.new(); root.add_child(layer)
	var rect := TextureRect.new(); rect.texture = viewport.get_texture(); rect.expand_mode = TextureRect.EXPAND_IGNORE_SIZE; rect.stretch_mode = TextureRect.STRETCH_SCALE
	rect.set_anchors_preset(Control.PRESET_FULL_RECT); layer.add_child(rect)
	var up: Vector3 = axes.up
	var across: Vector3 = axes.across
	var origin: Vector3 = axes.origin
	var rig_sky := rig_env.environment
	rig_sky.background_color = Color(.30, .34, .41)
	var roof_pair := add_box_pair(origin + across * 25.0 + up * (SLAB_HEIGHT + 3.4), 18.0, 0.4, Color(.22, .22, .25))
	# two pillars so the shelter reads as a building and gives depth cues
	for side: float in [-1.0, 1.0]:
		add_box_pair(origin + across * 25.0 + Vector3(axes.around) * side * 17.5 + up * (SLAB_HEIGHT + 3.4), 0.8, 3.4, Color(.2, .2, .22))
	driver.call("SetStormLevelForFixture", local, 0.45)
	driver.call("SetWindSettingsForFixture", 0.9, 0.55, 300.0)
	rain(1.0, Vector2(15.0, 3.0))
	var occlusion := world.get_node("PrecipitationOcclusion")
	await wait_map(int(occlusion.get("RenderCount")))
	var fps := 30.0
	var total := 22.0
	var frame_count := int(total * fps)
	for frame in frame_count:
		var t := frame / fps
		var walk := 0.0
		if t < 2.5: walk = 0.0
		elif t < 8.5: walk = smoothstep(2.5, 8.5, t) * 24.0
		elif t < 12.5: walk = 24.0
		elif t < 18.5: walk = 24.0 - smoothstep(12.5, 18.5, t) * 24.0
		var yaw := 0.0
		if t >= 8.5 and t < 12.5: yaw = sin((t - 8.5) / 4.0 * PI) * 0.9
		driver.call("SetObserverOffsetForFixture", world, local, 0.0, walk)
		eye = origin + across * walk + up * (SLAB_HEIGHT + 1.7)
		var forward := across.rotated(up, yaw)
		aim(forward, 5.0 if t < 8.5 or t > 12.5 else 14.0 * sin((t - 8.5) / 4.0 * PI) + 5.0)
		local.call("_Process", 1.0 / fps)
		await process_frame
	free_pair(roof_pair)
