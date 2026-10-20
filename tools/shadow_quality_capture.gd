extends SceneTree

## Shadow revamp S0 - quality baseline capture (docs/shadow-revamp-plan.md, S0 "Quality" + the "at least the same quality" contract). TEST ONLY.
## Per pose it takes three screenshots with the same camera, clocks and (frozen) weather, auto-exposure off:
##   full      = the game as it is
##   sun_off   = every directional light's energy forced to 0 (ambient, sky, haze and clouds unchanged)
##   all_off   = full lighting with every shadow type off (sun map, planet map, macro + wall macro, cloud-shadow receivers)
## and derives per-pixel sun visibility  v ~ (full - sun_off) / (all_off - sun_off)  in linear luminance (valid where all_off - sun_off is bright enough),
## then reports, per depth band (linear scene depth from a test-only depth CompositorEffect): shadowed-area fraction (v < 0.5), mean visibility and a
## penumbra width estimate (pixels of 0.1 < v < 0.9 per 0.5-crossing, and metres at the band's mean depth). Optionally 120-frame still and slow-pan sequences give
## a flicker / shimmer metric (full vs shadows-off, the excess is the shadow-attributable part).
##
## RUN (windowed Vulkan, no other Godot process, editor closed):
##   Godot_console.exe --path . --resolution 1920x1080 --script res://tools/shadow_quality_capture.gd -- [flags]
## Flags:
##   --quick                 validation run: poses ground_horizon + alt_1km, stride 4, 24-frame sequences on ground_horizon
##   --poses=a,b             subset (default: all), see COMMON.POSES: ground_horizon forest_noon emitter_rim emitter_bore_noon dawn_{3,5,8,15}deg dusk_{3,8,15}deg
##                           alt_1km alt_12km night_planet_lit eclipse_totality eclipse_hub   (coast, cave mouth/overhang, dedicated 2/5/10 km hill poses: TODO)
##   --stride=N              pixel subsampling for the maths (default 2)
##   --aa=off|live           off (default): TAA/FSR/MSAA disabled on the root viewport for clean per-pixel maths; live: keep the configured AA
##   --no-sequences          skip the still / pan sequences
##   --seq-poses=a,b         poses that get sequences (default ground_horizon,forest_noon)
##   --seq-frames=N          frames per sequence (default 120)
##   --pan-deg=D             yaw step per frame for the pan sequence (default 0.05 deg)
##   --out=<dir>             output root (default <project>/.godot/shadow_baseline/<machine>/); results in <out>/quality/<stamp>/ (baseline.json + PNGs)
##   --res=WxH               window size (default 1920x1080)
## MACHINE RULE: goldens/baselines are machine-specific; the authoritative set is captured on the A1000 laptop. Everything written here is stamped
## `machine: <tag> (non-authoritative)` unless the adapter is an A1000. Outputs live under .godot/ and are never committed.
## Approximations: depth bands use the main viewport depth only (terrain beyond the main camera far clip lands in the ">10km/sky" band); penumbra metres use a
## pinhole estimate; v is clamped to [0, 1]; the screenshot is the final LDR image (sRGB 2.2 approximation to linear).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const DEPTH_EFFECT := preload("res://tools/shadow_depth_capture_effect.gd")
const BAND_EDGES := [0.0, 10.0, 50.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 1.0e30]
const BAND_NAMES := ["0-10m", "10-50m", "50-200m", "200-500m", "500m-1km", "1-2km", "2-5km", "5-10km", ">10km_or_sky"]
const BAND_COLORS := [Color(0.9, 0.1, 0.1), Color(0.95, 0.5, 0.1), Color(0.95, 0.9, 0.1), Color(0.4, 0.85, 0.2), Color(0.1, 0.7, 0.5), Color(0.1, 0.6, 0.9), Color(0.3, 0.3, 0.95), Color(0.6, 0.2, 0.85), Color(0.25, 0.25, 0.3)]
const DEFAULT_POSES := ["ground_horizon", "forest_noon", "emitter_rim", "emitter_bore_noon", "dawn_3deg", "dawn_8deg", "dawn_15deg", "dusk_3deg", "dusk_8deg", "dusk_15deg", "alt_1km", "alt_12km", "night_planet_lit", "eclipse_totality", "eclipse_hub"]
const VISIBILITY_MIN_RANGE := 0.004 # linear luminance (all_off - sun_off) below which a pixel carries no usable sun signal

var game: Node
var explorer: Node
var surface: Node
var sun: DirectionalLight3D
var planet: DirectionalLight3D
var graphics: Node
var cloud_bridge: Node
var atmosphere: Node
var camera: Camera3D
var world_environment: WorldEnvironment
var depth_effect: CompositorEffect
var machine := {}
var report := {}
var out_dir := ""
var stride := 2
var quick := false
var failed := false
var force_sun_zero := false
var orig := {}
var linear_lut := PackedFloat32Array()


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required"); return
	quick = COMMON.has_flag("--quick")
	stride = int(COMMON.arg_value("--stride=", "4" if quick else "2"))
	machine = COMMON.machine_info()
	print("SHADOW_QUALITY ", machine["label"], " adapter=", machine["adapter"])
	var stamp := Time.get_datetime_string_from_system(false).replace(":", "-").replace("T", "_")
	out_dir = COMMON.output_dir(machine, "quality/" + stamp)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		root.size = Vector2i(int(res[0]), int(res[1]))
	linear_lut.resize(256)
	for i in 256:
		linear_lut[i] = pow(float(i) / 255.0, 2.2)

	var pose_names: PackedStringArray = COMMON.csv_arg("--poses=")
	if pose_names.is_empty():
		pose_names = PackedStringArray(["ground_horizon", "alt_1km"]) if quick else PackedStringArray(DEFAULT_POSES)
	for pose in pose_names:
		if not COMMON.POSES.has(pose):
			_fail("unknown or TODO pose " + pose); return
	var seq_poses: PackedStringArray = COMMON.csv_arg("--seq-poses=")
	if seq_poses.is_empty():
		seq_poses = PackedStringArray(["ground_horizon"]) if quick else PackedStringArray(["ground_horizon", "forest_noon"])
	var seq_frames := int(COMMON.arg_value("--seq-frames=", "24" if quick else "120"))
	var pan_deg := float(COMMON.arg_value("--pan-deg=", "0.05"))
	var do_sequences := not COMMON.has_flag("--no-sequences")

	report = {"status": "RUNNING", "tool": "shadow_quality_capture", "machine": machine["label"], "machine_info": machine, "authoritative": machine["authoritative"],
		"args": OS.get_cmdline_user_args(), "stride": stride, "band_names": BAND_NAMES, "band_edges_m": BAND_EDGES.slice(0, 9), "todo_poses": COMMON.TODO_POSES,
		"contract": "docs/shadow-revamp-plan.md S0: shadowed fraction per band within 3% of baseline; penumbra at 10/50 m <= baseline + 1 px, at 200 m <= baseline; flicker/shimmer <= baseline; cloud-shadow publish delta <= baseline",
		"poses": {}, "skipped_poses": {}, "sequences": {}}
	var fixture_root := "res://.godot/shadow-quality/%d" % Time.get_ticks_usec()
	game = await COMMON.create_game(self, fixture_root)
	if game == null:
		_fail("game boot failed"); return
	if not _bind_nodes():
		return
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	COMMON.hold_world_clocks(game)
	atmosphere.set("storm_auto_exposure_enabled", false)
	if COMMON.arg_value("--aa=", "off") == "off":
		root.use_taa = false
		root.msaa_3d = Viewport.MSAA_DISABLED
		root.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED
		root.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
		root.scaling_3d_scale = 1.0
	_install_depth_effect()
	RenderingServer.frame_pre_draw.connect(_pre_draw)
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await COMMON.wait_builds_idle(self, game, 180.0, 60):
		push_warning("initial settle timed out")
	_capture_original()
	var base: Dictionary = COMMON.pose_now(game)
	report["quality"] = _quality_identity()
	report["base_pose"] = base

	for pose_name in pose_names:
		var info: Dictionary = await COMMON.go_pose(self, game, base, pose_name, 2.0 if quick else 6.0, 30.0 if quick else 120.0)
		if info.has("error"):
			report["skipped_poses"][pose_name] = info
			print("SHADOW_QUALITY pose %s SKIPPED: %s" % [pose_name, str(info["error"])])
			continue
		await _lock_exposure()
		var result: Dictionary = await _capture_pose(pose_name, info)
		if failed:
			return
		report["poses"][pose_name] = result
		if do_sequences and pose_name in seq_poses:
			report["sequences"][pose_name] = await _capture_sequences(pose_name, seq_frames, pan_deg)
			if failed:
				return
		_write_report()
	await _finish()


func _bind_nodes() -> bool:
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	planet = game.get_node_or_null("PlanetLight") as DirectionalLight3D
	graphics = game.get_node_or_null("GraphicsQuality")
	cloud_bridge = game.get_node_or_null("SunshineCloudBridge")
	atmosphere = game.get_node_or_null("DaySideAtmosphere")
	world_environment = game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	camera = root.get_camera_3d()
	for pair in [["Explorer", explorer], ["Terrain3DGameplay", surface], ["StarLight", sun], ["GraphicsQuality", graphics], ["SunshineCloudBridge", cloud_bridge], ["DaySideAtmosphere", atmosphere], ["WorldEnvironment", world_environment], ["Camera3D", camera]]:
		if pair[1] == null:
			_fail("required node missing: " + str(pair[0])); return false
	return true


func _install_depth_effect() -> void:
	depth_effect = DEPTH_EFFECT.new()
	if world_environment.compositor == null:
		world_environment.compositor = Compositor.new()
	var effects := world_environment.compositor.compositor_effects.duplicate()
	effects.append(depth_effect)
	world_environment.compositor.compositor_effects = effects


func _capture_original() -> void:
	orig = {"sun_requested": bool(graphics.get("RequestedSunShadowsEnabled")), "macro": bool(graphics.get("EffectiveTerrainMacroShadowsEnabled")),
		"macro_walls": bool(graphics.get("EffectiveTerrainMacroWallShadowsEnabled")), "planet_cast": bool(atmosphere.get("planet_light_cast_shadows")),
		"cloud_receivers": bool(cloud_bridge.get("CloudShadowReceiversEnabled"))}


## Runs on the render thread just before drawing, after every _process: forces directional energy to 0 for the sun_off capture.
func _pre_draw() -> void:
	if force_sun_zero:
		if sun != null: sun.light_energy = 0.0
		if planet != null: planet.light_energy = 0.0


func _lock_exposure() -> void:
	atmosphere.set("storm_auto_exposure_enabled", false)
	await COMMON.settle(self, 0.3)
	var attributes := world_environment.camera_attributes as CameraAttributesPractical
	if attributes != null and attributes.auto_exposure_enabled:
		attributes.auto_exposure_enabled = false


func _shadows(enabled: bool) -> void:
	graphics.call("SetShadowTuningOverride", "Enabled", orig["sun_requested"] if enabled else false)
	graphics.call("SetShadowTuningOverride", "TerrainMacroEnabled", orig["macro"] if enabled else false)
	graphics.call("SetShadowTuningOverride", "TerrainMacroWallShadowsEnabled", orig["macro_walls"] if enabled else false)
	atmosphere.set("planet_light_cast_shadows", orig["planet_cast"] if enabled else false)
	cloud_bridge.set("CloudShadowReceiversEnabled", orig["cloud_receivers"] if enabled else false)


func _screenshot() -> Image:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image.get_format() != Image.FORMAT_RGBA8:
		image.convert(Image.FORMAT_RGBA8)
	return image


func _settle_capture() -> void:
	await COMMON.settle(self, 0.8 if quick else 2.0)


# ------------------------------------------------------------------ per pose

func _capture_pose(pose_name: String, info: Dictionary) -> Dictionary:
	var result := {"pose": info, "files": {}}
	_shadows(true)
	force_sun_zero = false
	await _settle_capture()
	depth_effect.arm()
	var full := await _screenshot()
	var depth_data := {}
	for i in 12:
		depth_data = depth_effect.take()
		if not depth_data.is_empty():
			break
		await RenderingServer.frame_post_draw
	force_sun_zero = true
	await _settle_capture()
	var sun_off := await _screenshot()
	force_sun_zero = false
	_shadows(false)
	await _settle_capture()
	var all_off := await _screenshot()
	_shadows(true)
	result["state"] = {"sun_energy": sun.light_energy, "sun_shadow": sun.shadow_enabled, "planet_energy": planet.light_energy if planet != null else 0.0, "planet_shadow": planet.shadow_enabled if planet != null else false}
	var base_name := out_dir.path_join(pose_name)
	full.save_png(base_name + "_full.png"); sun_off.save_png(base_name + "_sun_off.png"); all_off.save_png(base_name + "_all_off.png")
	result["files"] = {"full": base_name + "_full.png", "sun_off": base_name + "_sun_off.png", "all_off": base_name + "_all_off.png"}
	result["depth_available"] = not depth_data.is_empty()
	var analysis := _analyze(full, sun_off, all_off, depth_data, base_name)
	result.merge(analysis)
	var summary: Array = []
	for band in analysis.get("bands", []):
		summary.append("%s:%.3f" % [band["name"], float(band.get("shadowed_fraction", NAN))])
	print("SHADOW_QUALITY pose %s sun_elev=%.2f valid=%.3f shadowed by band: %s" % [pose_name, float(info["time"].get("sun_elevation_deg", NAN)), float(analysis.get("valid_fraction", 0.0)), " ".join(summary)])
	return result


func _luminance(image: Image, step: int) -> PackedFloat32Array:
	var width := image.get_width()
	var height := image.get_height()
	var data := image.get_data()
	var out := PackedFloat32Array()
	out.resize((width / step) * (height / step))
	var i := 0
	for y in range(0, (height / step) * step, step):
		var row := y * width * 4
		for x in range(0, (width / step) * step, step):
			var o := row + x * 4
			out[i] = 0.2126 * linear_lut[data[o]] + 0.7152 * linear_lut[data[o + 1]] + 0.0722 * linear_lut[data[o + 2]]
			i += 1
	return out


func _band_of(depth: float) -> int:
	for b in range(BAND_NAMES.size()):
		if depth < float(BAND_EDGES[b + 1]):
			return b
	return BAND_NAMES.size() - 1


func _analyze(full: Image, sun_off: Image, all_off: Image, depth_data: Dictionary, base_name: String) -> Dictionary:
	var width := full.get_width() / stride
	var height := full.get_height() / stride
	var lum_full := _luminance(full, stride)
	var lum_sun := _luminance(sun_off, stride)
	var lum_all := _luminance(all_off, stride)
	var has_depth := not depth_data.is_empty()
	var depth: PackedFloat32Array = depth_data.get("depth", PackedFloat32Array())
	var depth_size: Vector2i = depth_data.get("size", Vector2i.ONE)
	var band_count := BAND_NAMES.size()
	var total := PackedInt32Array(); total.resize(band_count)
	var valid := PackedInt32Array(); valid.resize(band_count)
	var shadowed := PackedInt32Array(); shadowed.resize(band_count)
	var mid := PackedInt32Array(); mid.resize(band_count)
	var crossings := PackedInt32Array(); crossings.resize(band_count)
	var vis_sum := PackedFloat64Array(); vis_sum.resize(band_count)
	var depth_sum := PackedFloat64Array(); depth_sum.resize(band_count)
	var vis := PackedFloat32Array(); vis.resize(width * height)
	var vis_bytes := PackedByteArray(); vis_bytes.resize(width * height)
	var band_bytes := PackedByteArray(); band_bytes.resize(width * height * 3)
	var band_index := PackedByteArray(); band_index.resize(width * height)
	var valid_total := 0
	for y in height:
		var depth_row := mini(depth_size.y - 1, int(float(y) / float(height) * depth_size.y)) * depth_size.x
		for x in width:
			var i := y * width + x
			var d := 1.0e30
			if has_depth:
				d = depth[depth_row + mini(depth_size.x - 1, int(float(x) / float(width) * depth_size.x))]
			var b := _band_of(d)
			band_index[i] = b
			total[b] += 1
			depth_sum[b] += minf(d, 1.0e5)
			var range_lum := lum_all[i] - lum_sun[i]
			if range_lum > VISIBILITY_MIN_RANGE:
				var v := clampf((lum_full[i] - lum_sun[i]) / range_lum, 0.0, 1.0)
				vis[i] = v
				vis_bytes[i] = int(v * 255.0)
				valid[b] += 1
				valid_total += 1
				vis_sum[b] += v
				if v < 0.5: shadowed[b] += 1
				if v > 0.1 and v < 0.9: mid[b] += 1
			else:
				vis[i] = -1.0
				vis_bytes[i] = 0
			var color: Color = BAND_COLORS[b]
			band_bytes[i * 3] = int(color.r * 255.0); band_bytes[i * 3 + 1] = int(color.g * 255.0); band_bytes[i * 3 + 2] = int(color.b * 255.0)
	# 0.5-crossings between horizontal / vertical neighbours (both valid) approximate the number of shadow edges.
	for y in range(0, height - 1):
		for x in range(0, width - 1):
			var i := y * width + x
			var v0 := vis[i]
			if v0 < 0.0: continue
			var vx := vis[i + 1]
			var vy := vis[i + width]
			var b := band_index[i]
			if vx >= 0.0 and ((v0 < 0.5) != (vx < 0.5)): crossings[b] += 1
			if vy >= 0.0 and ((v0 < 0.5) != (vy < 0.5)): crossings[b] += 1
	Image.create_from_data(width, height, false, Image.FORMAT_L8, vis_bytes).save_png(base_name + "_visibility.png")
	Image.create_from_data(width, height, false, Image.FORMAT_RGB8, band_bytes).save_png(base_name + "_depth_bands.png")
	var pixel_angle := 2.0 * tan(deg_to_rad(camera.fov) * 0.5) / float(full.get_height())
	var bands: Array = []
	for b in band_count:
		var mean_depth := depth_sum[b] / maxf(1.0, float(total[b]))
		var penumbra_px := float(mid[b]) / maxf(1.0, float(crossings[b])) * float(stride) if crossings[b] > 0 else NAN
		bands.append({"name": BAND_NAMES[b], "pixels": total[b], "valid_pixels": valid[b], "mean_depth_m": mean_depth if total[b] > 0 else NAN,
			"shadowed_fraction": float(shadowed[b]) / float(valid[b]) if valid[b] > 0 else NAN, "mean_visibility": vis_sum[b] / float(valid[b]) if valid[b] > 0 else NAN,
			"edge_crossings": crossings[b], "penumbra_px": penumbra_px, "penumbra_m": penumbra_px * pixel_angle * mean_depth if not is_nan(penumbra_px) else NAN})
	var sample_count := width * height
	return {"bands": bands, "valid_fraction": float(valid_total) / float(maxi(1, sample_count)), "visibility_mean": _mean_valid(vis), "size": [full.get_width(), full.get_height()],
		"files_derived": {"visibility": base_name + "_visibility.png", "depth_bands": base_name + "_depth_bands.png"},
		"mean_luma_linear": {"full": _mean(lum_full), "sun_off": _mean(lum_sun), "all_off": _mean(lum_all)}}


func _mean(values: PackedFloat32Array) -> float:
	var total := 0.0
	for v in values:
		total += v
	return total / float(maxi(1, values.size()))


func _mean_valid(values: PackedFloat32Array) -> float:
	var total := 0.0
	var count := 0
	for v in values:
		if v >= 0.0:
			total += v
			count += 1
	return total / float(count) if count > 0 else NAN


# ------------------------------------------------------------------ sequences

## Still and slow-pan sequences with shadows on and off. Flicker / shimmer metrics are luminance based (stride 8 sub-sampling):
## still: per-pixel temporal std (p50, p99) and mean abs frame diff; pan: mean and p99 of abs frame-to-frame luminance diff. excess = shadows_on - shadows_off.
func _capture_sequences(pose_name: String, frames: int, pan_deg: float) -> Dictionary:
	var out := {"frames": frames, "pan_deg_per_frame": pan_deg}
	var base: Dictionary = COMMON.pose_now(game)
	for kind in ["still", "pan"]:
		for state in ["shadows_on", "shadows_off"]:
			_shadows(state == "shadows_on")
			explorer.call("SetPresentationView", float(base["yaw"]), float(base["pitch"]))
			await _settle_capture()
			out["%s_%s" % [kind, state]] = await _run_sequence(kind == "pan", frames, pan_deg, base)
	for kind in ["still", "pan"]:
		var on: Dictionary = out["%s_shadows_on" % kind]
		var off: Dictionary = out["%s_shadows_off" % kind]
		var excess := {}
		for key in on:
			if typeof(on[key]) == TYPE_FLOAT and typeof(off.get(key)) == TYPE_FLOAT:
				excess[key] = float(on[key]) - float(off[key])
		out["%s_excess" % kind] = excess
	_shadows(true)
	explorer.call("SetPresentationView", float(base["yaw"]), float(base["pitch"]))
	print("SHADOW_QUALITY sequences %s: still p99 diff excess=%.6f pan p99 diff excess=%.6f" % [pose_name, float(out["still_excess"].get("p99_abs_diff", NAN)), float(out["pan_excess"].get("p99_abs_diff", NAN))])
	return out


func _run_sequence(pan: bool, frames: int, pan_deg: float, base: Dictionary) -> Dictionary:
	var previous := PackedFloat32Array()
	var sum := PackedFloat64Array()
	var sum_sq := PackedFloat64Array()
	var mean_diffs: Array[float] = []
	var p99_diffs: Array[float] = []
	for f in frames:
		if pan:
			explorer.call("SetPresentationView", float(base["yaw"]) + pan_deg * float(f), float(base["pitch"]))
		var image := await _screenshot()
		var lum := _luminance(image, 8)
		if sum.is_empty():
			sum.resize(lum.size()); sum_sq.resize(lum.size())
		for i in lum.size():
			sum[i] += lum[i]
			sum_sq[i] += lum[i] * lum[i]
		if not previous.is_empty() and previous.size() == lum.size():
			var diffs := PackedFloat32Array(); diffs.resize(lum.size())
			var total := 0.0
			for i in lum.size():
				var d := absf(lum[i] - previous[i])
				diffs[i] = d
				total += d
			mean_diffs.append(total / float(lum.size()))
			diffs.sort()
			p99_diffs.append(diffs[int(float(diffs.size() - 1) * 0.99)])
		previous = lum
	var stds := PackedFloat32Array(); stds.resize(sum.size())
	for i in sum.size():
		var mean := sum[i] / float(frames)
		stds[i] = sqrt(maxf(0.0, sum_sq[i] / float(frames) - mean * mean))
	stds.sort()
	return {"mean_abs_diff": COMMON.stats(mean_diffs).get("median", NAN), "p99_abs_diff": COMMON.stats(p99_diffs).get("median", NAN),
		"temporal_std_p50": stds[stds.size() / 2] if not stds.is_empty() else NAN, "temporal_std_p99": stds[int(float(stds.size() - 1) * 0.99)] if not stds.is_empty() else NAN}


# ------------------------------------------------------------------ io

func _quality_identity() -> Dictionary:
	return {"preset": graphics.get("CurrentPresetName"), "resolution": root.size, "render_scale": root.get("scaling_3d_scale"), "scaling_mode": root.get("scaling_3d_mode"), "msaa": root.get("msaa_3d"), "taa": root.get("use_taa"),
		"shadow_atlas": graphics.get("EffectiveDirectionalShadowAtlasSize"), "shadow_filter": graphics.get("EffectiveDirectionalShadowFilter"), "sun_max_distance": sun.directional_shadow_max_distance,
		"camera_fov": camera.fov, "auto_exposure": (world_environment.camera_attributes as CameraAttributesPractical).auto_exposure_enabled if world_environment.camera_attributes is CameraAttributesPractical else null}


func _write_report() -> void:
	var file := FileAccess.open(out_dir.path_join("baseline.json"), FileAccess.WRITE)
	if file == null:
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()


func _finish() -> void:
	_shadows(true)
	force_sun_zero = false
	report["status"] = "PASS"
	_write_report()
	print("SHADOW_QUALITY baseline: ", out_dir.path_join("baseline.json"))
	game.queue_free()
	await process_frame
	print("SHADOW_QUALITY=PASS ", machine["label"])
	quit(0)


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	report["status"] = "FAIL"
	report["failure"] = message
	push_error("SHADOW_QUALITY=FAIL " + message)
	if out_dir != "":
		_write_report()
	if is_instance_valid(game):
		game.queue_free()
	quit(1)
