extends SceneTree

## T2R-11: inland water renders through the proxy water path (docs/work-orders/terrain-vegetation/T2.md). Boots the landscape harness world (generation 1 with water.rivers), goes to a lake pose and a
## river pose at noon, and checks: an installed water sector at the point on the 2, 8 and 32 m proxy levels; water pixels from each of those levels alone (pixels that change when only that level's
## proxies are hidden); the water shader still carries the haze, cloud-shadow and rain-ripple calls; and forced rain changes the water pixels near the camera (ripples).
## T2R-11b bank strip: thin water beside a river bank and a lake shore is not brighter than the bed under it (the white bank band); see _check_bank_strip. Review pictures (bank views, bed alone and
## an ocean shore of the same world) go to user://generated/captures/smoke/inland_*.png.
## Capture mode (not a test): --capture-dir=<dir> [--shots=river_bank,lake_shore,confluence_300m,valley_2km] [--tag=<suffix>] writes the milestone pictures as <name><tag>.png and exits.
## Also river_bank_top, lake_shore_top, ocean_shore_top and river_aerial; --variants-file=<json> adds parameter variants of every shot (see _capture_variants).
## RUN: tools/run_tests.ps1 -Tests inland_water_render_gpu_test  (manifest user_args: --generation=1 --review-features=water.rivers). Prints INLAND_WATER_RENDER=PASS|FAIL.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const PROBE := preload("res://tools/InlandSwimProbe.cs")
const GENERATED_OUTPUT_PATHS := preload("res://tools/generated_output_paths.gd")
const MARKER := "INLAND_WATER_RENDER"
const LEVELS := [2, 8, 32]
const WATER_PIXEL_DELTA := 30
const MIN_LEVEL_PIXELS := 400
const MIN_RAIN_PIXELS := 150
# T2R-11b bank strip check (see _check_bank_strip).
const BANK_BAND_MIN_DEPTH_M := 0.03
const BANK_BAND_MAX_DEPTH_M := 0.6
const BANK_BAND_EXCESS_LIMIT := 0.10
const BANK_MIN_SAMPLES := 4

var booted := {}
var finished := false
var failures: Array = []


func _init() -> void:
	call_deferred("_run")


func _arg(name: String, fallback := "") -> String:
	for arg in OS.get_cmdline_user_args():
		if str(arg).begins_with("--%s=" % name):
			return str(arg).substr(name.length() + 3)
	return fallback


func _run() -> void:
	var watchdog := create_timer(1500.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1500 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	booted = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		_cleanup()
		quit(1)
		return
	var game: Node = booted["game"]
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	HARNESS.COMMON.hold_world_clocks(game)
	var probe: RefCounted = PROBE.new()
	var points: Dictionary = probe.call("FindPoints", game)
	if not bool(points.get("ok", false)):
		_fail("no water points: %s" % str(points.get("error", "")))
		return
	print("%s lake=%s" % [MARKER, str(points["lake"])])
	print("%s river=%s" % [MARKER, str(points["river"])])
	var capture_dir := _arg("capture-dir")
	if not capture_dir.is_empty():
		await _capture_shots(game, probe, points, capture_dir)
		_cleanup()
		finished = true
		quit(0)
		return
	for key in ["lake", "river"]:
		await _check_water_pose(game, key, points[key])
	var shots: Dictionary = probe.call("FindShots", game, points)
	for key in ["river", "lake"]:
		var shot_name: String = "river_bank_top" if key == "river" else "lake_shore_top"
		if not shots.has(shot_name):
			failures.append("%s bank strip: no %s shot (%s)" % [key, shot_name, str(shots.get("error", ""))])
			continue
		await _check_bank_strip(game, probe, key, points[key], shots[shot_name])
	await _capture_ocean_shore(game, shots)
	_check_shader_contract()
	await _check_rain(game, points["river"])
	# T2R-11 escalation: more than 40 % of the land sectors of the 2 km sector level (32 m spacing) routing to the CPU is the early warning for T2R-14.
	for spacing in [8, 16, 32]:
		var share: Dictionary = probe.call("RiverRoutingShare", game, spacing, 2)
		print("%s CPU routing share at %d m spacing: %d of %d sampled land sectors (%.1f %%)" % [MARKER, spacing, int(share["river_land"]), int(share["land"]), 100.0 * float(share["share"])])
		if spacing == 32 and float(share["share"]) > 0.40:
			# A reported measure, not a failure: the work order's escalation condition (limit 40 %).
			print("%s ESCALATION: %.0f %% of the land sectors of the 32 m level route to the CPU (limit 40 %%)" % [MARKER, 100.0 * float(share["share"])])
	if not failures.is_empty():
		_fail("; ".join(failures))
		return
	_cleanup()
	finished = true
	print("%s=PASS" % MARKER)
	quit(0)


# ------------------------------------------------------------------ helpers

func _proxy_levels(game: Node) -> Dictionary:
	var found := {}
	var stack: Array = [game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node.has_method("HasActiveWaterSectorAt"):
			found[int(node.get("SampleSpacingMeters"))] = node
		for child in node.get_children():
			stack.append(child)
	return found


func _grab() -> Image:
	await HARNESS.COMMON.settle(self, 0.4)
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image.get_format() != Image.FORMAT_RGBA8:
		image.convert(Image.FORMAT_RGBA8)
	return image


func _changed_mask(a: Image, b: Image, delta: int) -> PackedByteArray:
	var mask := PackedByteArray()
	mask.resize(a.get_width() * a.get_height())
	var da := a.get_data()
	var db := b.get_data()
	var n := mini(da.size(), db.size()) / 4
	for i in n:
		var o := i * 4
		var d := absi(da[o] - db[o]) + absi(da[o + 1] - db[o + 1]) + absi(da[o + 2] - db[o + 2])
		mask[i] = 1 if d > delta else 0
	return mask


func _count(mask: PackedByteArray) -> int:
	var total := 0
	for v in mask:
		total += v
	return total


func _set_levels_visible(levels: Dictionary, visible_spacings: Array) -> void:
	for spacing in levels:
		(levels[spacing] as Node3D).visible = visible_spacings.has(spacing)


func _pose_for(key: String, p: Dictionary, lift: float, pitch: float) -> Dictionary:
	var level := float(p["level"])
	return {"name": "inland_" + key, "around_m": p["around"], "across_m": p["across"], "lift_m": level + lift, "vertical_mode": "abs", "ground_m": level, "yaw_deg": 0.0, "pitch_deg": pitch}


func _check_water_pose(game: Node, key: String, p: Dictionary) -> void:
	var info: Dictionary = await HARNESS.go_pose(self, game, _pose_for(key, p, 40.0, -40.0), "noon")
	if info.has("error"):
		failures.append("%s: %s" % [key, str(info["error"])])
		return
	var levels := _proxy_levels(game)
	for spacing in LEVELS:
		if not levels.has(spacing):
			failures.append("%s: no %d m proxy level in the scene" % [key, spacing])
			return
	var all_spacings: Array = levels.keys()
	# Sectors: every checked level holds water at the point.
	for spacing in LEVELS:
		var has: bool = levels[spacing].call("HasActiveWaterSectorAt", float(p["around"]), float(p["across"]))
		print("%s %s level %d m: water sector at the point = %s" % [MARKER, key, spacing, str(has)])
		if not has:
			failures.append("%s: no installed water sector at the point on the %d m level" % [key, spacing])
	# Pixels: with every proxy hidden the water is gone; (The levels are nested nodes, so one level cannot be shown alone.)
	_set_levels_visible(levels, [])
	var none_image := await _grab()
	_set_levels_visible(levels, all_spacings)
	var all_image := await _grab()
	var all_mask := _changed_mask(all_image, none_image, WATER_PIXEL_DELTA)
	var all_count := _count(all_mask)
	print("%s %s all proxies vs none: %d changed pixels of %d" % [MARKER, key, all_count, all_mask.size()])
	if all_count < MIN_LEVEL_PIXELS:
		failures.append("%s: hiding every proxy changed only %d pixels (no water drawn at the pose)" % [key, all_count])
	_set_levels_visible(levels, all_spacings)


## Rec. 709 luminance of the 3x3 block around (x, y), sRGB 0..1.
func _luma(image: Image, x: int, y: int) -> float:
	var total := 0.0
	for dy in range(-1, 2):
		for dx in range(-1, 2):
			var c := image.get_pixel(x + dx, y + dy)
			total += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b
	return total / 9.0


func _percentile(values: Array, q: float) -> float:
	if values.is_empty():
		return NAN
	var sorted := values.duplicate()
	sorted.sort()
	return float(sorted[clampi(int(round(q * (sorted.size() - 1))), 0, sorted.size() - 1)])


## T2R-11b: thin water beside a bank must not be brighter than the bed under it (the white bank band was shaded far brighter). From 12 m above the water point, looking at its nearest dry bank,
## InlandSwimProbe.BankProfile projects the line from the water point to the bank; the water pixels where the water is BANK_BAND_MIN_DEPTH_M to BANK_BAND_MAX_DEPTH_M deep are compared with the same
## pixels with every proxy level hidden (the bed alone). Fails when the 90th percentile of (water - bed) luminance exceeds BANK_BAND_EXCESS_LIMIT.
func _check_bank_strip(game: Node, probe: RefCounted, key: String, p: Dictionary, shot: Dictionary) -> void:
	var info: Dictionary = await HARNESS.go_pose(self, game, shot, "noon")
	if info.has("error"):
		failures.append("%s bank strip: %s" % [key, str(info["error"])])
		return
	var levels := _proxy_levels(game)
	var all_spacings: Array = levels.keys()
	_set_levels_visible(levels, [])
	var bed_image := await _grab()
	_set_levels_visible(levels, all_spacings)
	var water_image := await _grab()
	_save_capture(water_image, "inland_bank_%s.png" % key)
	_save_capture(bed_image, "inland_bank_%s_bed.png" % key)
	var profile: Dictionary = probe.call("BankProfile", game, p, 6.0)
	if not bool(profile.get("ok", false)):
		failures.append("%s bank strip: %s" % [key, str(profile.get("error", ""))])
		return
	var excess: Array = []
	var band: Array = []
	var deep: Array = []
	var bank: Array = []
	for entry in profile["entries"]:
		var wet := int(entry[1]) == 1
		var depth := float(entry[2])
		var x := int(round(float(entry[3])))
		var y := int(round(float(entry[4])))
		if int(entry[5]) == 1 or x < 2 or y < 2 or x >= water_image.get_width() - 2 or y >= water_image.get_height() - 2:
			continue
		var water_luma := _luma(water_image, x, y)
		if wet and depth >= BANK_BAND_MIN_DEPTH_M and depth <= BANK_BAND_MAX_DEPTH_M:
			band.append(water_luma)
			excess.append(water_luma - _luma(bed_image, x, y))
		elif wet and depth > 1.5:
			deep.append(water_luma)
		elif not wet:
			bank.append(water_luma)
	var excess_p90 := _percentile(excess, 0.9)
	print("%s %s bank strip: %d band samples (water %.2f-%.2f m deep), luminance band p90 %.3f, deep water median %.3f, dry bank median %.3f; band minus bed p90 %+.3f, max %+.3f (limit %.2f)" % [
		MARKER, key, band.size(), BANK_BAND_MIN_DEPTH_M, BANK_BAND_MAX_DEPTH_M, _percentile(band, 0.9), _percentile(deep, 0.5), _percentile(bank, 0.5),
		excess_p90, _percentile(excess, 1.0), BANK_BAND_EXCESS_LIMIT])
	if band.size() < BANK_MIN_SAMPLES:
		failures.append("%s bank strip: only %d band samples on screen" % [key, band.size()])
	elif excess_p90 > BANK_BAND_EXCESS_LIMIT:
		failures.append("%s bank strip: thin water beside the bank is %.2f brighter than its bed (p90, limit %.2f): the white bank band" % [key, excess_p90, BANK_BAND_EXCESS_LIMIT])


## Review picture, not a check: an ocean shore of the same world from 12 m (InlandSwimProbe ocean_shore_top), the reference that inland-water changes leave the ocean as it was.
func _capture_ocean_shore(game: Node, shots: Dictionary) -> void:
	if not shots.has("ocean_shore_top"):
		print("%s no ocean shore found for the reference picture" % MARKER)
		return
	var info: Dictionary = await HARNESS.go_pose(self, game, shots["ocean_shore_top"], "noon")
	if info.has("error"):
		print("%s ocean shore pose: %s" % [MARKER, str(info["error"])])
		return
	_save_capture(await _grab(), "inland_ocean_shore.png")


## Disposable review captures go to user://generated/captures/smoke (tools/generated_output_paths.gd), like the other water smoke pictures.
func _save_capture(image: Image, file_name: String) -> void:
	var path := GENERATED_OUTPUT_PATHS.smoke_capture_path(file_name)
	if not path.is_empty() and image.save_png(path) == OK:
		print("%s wrote %s" % [MARKER, path])


func _check_shader_contract() -> void:
	# Water takes haze, cloud shadow and rain ripples through the shared includes of the one proxy water shader; inland water uses that shader (no per-kind material).
	var shader := load("res://materials/ring_ocean_proxy.gdshader") as Shader
	if shader == null:
		failures.append("ring_ocean_proxy.gdshader did not load")
		return
	for call_name in ["ring_haze_sample(", "ring_haze_inscatter(", "sample_combined_cloud_shadow(", "ring_rain_ripple_gradient(", "ring_wet_sample("]:
		if not shader.code.contains(call_name):
			failures.append("the water shader no longer calls %s" % call_name)


func _check_rain(game: Node, p: Dictionary) -> void:
	var wet := game.get_node_or_null("WetWorld")
	if wet == null:
		failures.append("rain: WetWorld node missing")
		return
	var info: Dictionary = await HARNESS.go_pose(self, game, _pose_for("river_rain", p, 8.0, -55.0), "noon")
	if info.has("error"):
		failures.append("rain: %s" % str(info["error"]))
		return
	var levels := _proxy_levels(game)
	var all_spacings: Array = levels.keys()
	_set_levels_visible(levels, [])
	var none_image := await _grab()
	_set_levels_visible(levels, all_spacings)
	wet.call("ClearForce")
	await HARNESS.COMMON.settle(self, 0.6)
	var dry := await _grab()
	var water_mask := _changed_mask(dry, none_image, WATER_PIXEL_DELTA)
	wet.call("ForceUniform", 0.7, 0.5, 0.3, 1.0)
	await HARNESS.COMMON.settle(self, 1.0)
	var rainy := await _grab()
	wet.call("ClearForce")
	var rain_mask := _changed_mask(rainy, dry, 6)
	var in_water := 0
	for i in mini(water_mask.size(), rain_mask.size()):
		if water_mask[i] == 1 and rain_mask[i] == 1:
			in_water += 1
	print("%s rain: %d water pixels, %d of them change under forced rain" % [MARKER, _count(water_mask), in_water])
	if _count(water_mask) < MIN_LEVEL_PIXELS:
		failures.append("rain: no water pixels at the river pose")
	elif in_water < MIN_RAIN_PIXELS:
		failures.append("rain: only %d water pixels change under forced rain (no ripples)" % in_water)


func _capture_shots(game: Node, probe: RefCounted, points: Dictionary, directory: String) -> void:
	var shots: Dictionary = probe.call("FindShots", game, points)
	if not bool(shots.get("ok", false)):
		print("%s shots error: %s" % [MARKER, str(shots.get("error", ""))])
	DirAccess.make_dir_recursive_absolute(directory)
	var wanted := _arg("shots", "river_bank,lake_shore,confluence_300m,valley_2km").split(",")
	var tag := _arg("tag")
	var time_name := _arg("time", "noon")
	for shot_name in wanted:
		if not shots.has(shot_name):
			print("%s shot %s not available" % [MARKER, shot_name])
			continue
		var pose: Dictionary = shots[shot_name]
		print("%s shot %s %s" % [MARKER, shot_name, str(pose)])
		var info: Dictionary = await HARNESS.go_pose(self, game, pose, time_name)
		if info.has("error"):
			print("%s shot %s pose error: %s" % [MARKER, shot_name, str(info["error"])])
			continue
		var image := await _grab()
		var path := directory.path_join("%s%s.png" % [shot_name, tag])
		image.save_png(path)
		print("%s wrote %s" % [MARKER, path])
		var bank_point: Variant = {"river_bank_top": points.get("river"), "lake_shore_top": points.get("lake"), "ocean_shore_top": shots.get("ocean_point")}.get(shot_name)
		if bank_point is Dictionary:
			var profile: Dictionary = probe.call("BankProfile", game, bank_point, 6.0)
			var file := FileAccess.open(directory.path_join("%s%s_profile.json" % [shot_name, tag]), FileAccess.WRITE)
			if file != null:
				file.store_string(JSON.stringify({"point": bank_point, "profile": profile}))
				file.close()
		await _capture_variants(game, shot_name, directory, tag)


## Diagnostics: --variants-file=<json> holds [{"tag": "...", "params": {"shader_parameter": value}, "levels": [spacings visible]}]. Each variant is shot from the same pose with the
## parameters set on every proxy water material (restored afterwards) and only the listed proxy levels visible.
func _capture_variants(game: Node, shot_name: String, directory: String, tag: String) -> void:
	var file := _arg("variants-file")
	if file.is_empty() or not FileAccess.file_exists(file):
		return
	var variants: Array = JSON.parse_string(FileAccess.get_file_as_string(file))
	var levels := _proxy_levels(game)
	var all_spacings: Array = levels.keys()
	print("%s proxy levels: %s" % [MARKER, str(all_spacings)])
	var materials: Array = _water_materials(game)
	for material in materials:
		print("%s water material fade_out_start_meters=%s" % [MARKER, str(material.get_shader_parameter("fade_out_start_meters"))])
	for variant in variants:
		var originals := {}
		for material in materials:
			for param in variant.get("params", {}):
				originals[[material, param]] = material.get_shader_parameter(param)
				material.set_shader_parameter(param, variant["params"][param])
		_set_levels_visible(levels, variant.get("levels", all_spacings))
		var image := await _grab()
		var path := directory.path_join("%s%s_%s.png" % [shot_name, tag, str(variant["tag"])])
		image.save_png(path)
		print("%s wrote %s" % [MARKER, path])
		for key in originals:
			(key[0] as ShaderMaterial).set_shader_parameter(key[1], originals[key])
		_set_levels_visible(levels, all_spacings)


## Every proxy water material in the scene. A slot-installed coastal sector draws its water through an OceanSurface child whose mesh has no surface material (the level material is the child's
## material_override), so both places are searched.
func _water_materials(game: Node) -> Array:
	var found: Array = []
	var stack: Array = [game]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is GeometryInstance3D:
			var candidates: Array = [(node as GeometryInstance3D).material_override]
			if node is MeshInstance3D and (node as MeshInstance3D).mesh != null:
				var mesh := (node as MeshInstance3D).mesh
				for i in mesh.get_surface_count():
					candidates.append(mesh.surface_get_material(i))
			for candidate in candidates:
				var material := candidate as ShaderMaterial
				if material != null and material.shader != null and material.shader.resource_path == "res://materials/ring_ocean_proxy.gdshader" and not found.has(material):
					found.append(material)
		for child in node.get_children():
			stack.append(child)
	return found


func _cleanup() -> void:
	if booted.has("game") and is_instance_valid(booted["game"]):
		(booted["game"] as Node).queue_free()
	if booted.has("fixture_root"):
		HARNESS.remove_dir_recursive(ProjectSettings.globalize_path(str(booted["fixture_root"])))


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	_cleanup()
	quit(1)
