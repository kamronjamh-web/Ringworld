extends SceneTree

## Shadow revamp S6 - side-by-side contact-shadow captures for decision 5 (contact shadows on grass: accept the new look and its default strength).
## Boots the gameplay fixture (tools/shadow_harness_common.gd), goes to each pose (default forest_noon, ground_horizon) and takes the same frame with contact
## shadows off, on (default settings) and on at --grass-strengths (default 0.0,0.35,0.7 for the grass slot, surface strength 1), plus the contact term alone (debug mode 3).
## Writes <out>/<pose>_off.png, _on_g<strength>.png, _contact_term.png, _side_by_side.png (off | on, bottom-centre crop 2x) and _diff.png (|on - off| x8) and prints
## CONTACT_CAPTURE lines with the changed-pixel fraction and mean luminance delta. Auto exposure off, clocks and weather held (same as shadow_quality_capture).
## Run (windowed Vulkan, editor closed, no other Godot process):
##   Godot_console.exe --path . --resolution 1920x1080 --script res://tools/contact_shadow_capture.gd -- [--poses=a,b] [--grass-strengths=0.0,0.35,0.7] [--out=<dir>] [--steps=16]

const COMMON := preload("res://tools/shadow_harness_common.gd")

var game: Node
var explorer: Node
var surface: Node
var graphics: Node
var atmosphere: Node
var world_environment: WorldEnvironment
var failed := false
var out_dir := ""


func _init() -> void:
	call_deferred("_run")
	create_timer(1500.0).timeout.connect(func() -> void: _fail("timeout"))


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("CONTACT_CAPTURE=FAIL " + message)
	call_deferred("quit", 1)


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	var machine: Dictionary = COMMON.machine_info()
	out_dir = COMMON.arg_value("--out=", "")
	if out_dir == "":
		out_dir = ProjectSettings.globalize_path("res://.godot/shadow_baseline/rtx4070/contact")
	DirAccess.make_dir_recursive_absolute(out_dir)
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		root.size = Vector2i(int(res[0]), int(res[1]))
	var poses: PackedStringArray = COMMON.csv_arg("--poses=")
	if poses.is_empty():
		poses = PackedStringArray(["forest_noon", "ground_horizon"])
	var strengths: PackedStringArray = COMMON.csv_arg("--grass-strengths=")
	if strengths.is_empty():
		strengths = PackedStringArray(["0.0", "0.35", "0.7"])
	var steps := int(COMMON.arg_value("--steps=", "16"))
	game = await COMMON.create_game(self, "res://.godot/contact-capture/%d" % Time.get_ticks_usec())
	if game == null:
		_fail("game boot failed")
		return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay")
	graphics = game.get_node_or_null("GraphicsQuality")
	atmosphere = game.get_node_or_null("DaySideAtmosphere")
	world_environment = game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if explorer == null or surface == null or graphics == null or atmosphere == null or world_environment == null or game.get_node_or_null("ContactShadows") == null:
		_fail("required node missing (Explorer / Terrain3DGameplay / GraphicsQuality / DaySideAtmosphere / WorldEnvironment / ContactShadows)")
		return
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	COMMON.hold_world_clocks(game)
	atmosphere.set("storm_auto_exposure_enabled", false)
	root.use_taa = false
	root.msaa_3d = Viewport.MSAA_DISABLED
	root.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED
	root.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
	root.scaling_3d_scale = 1.0
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await COMMON.wait_builds_idle(self, game, 180.0, 60):
		push_warning("initial settle timed out")
	var base: Dictionary = COMMON.pose_now(game)
	var report := {"machine": machine["label"], "poses": {}}
	for pose_name in poses:
		var info: Dictionary = await COMMON.go_pose(self, game, base, pose_name, 6.0, 120.0)
		if info.has("error"):
			print("CONTACT_CAPTURE pose %s SKIPPED: %s" % [pose_name, str(info["error"])])
			continue
		await _lock_exposure()
		_contact(false, steps, 0.35)
		await COMMON.settle(self, 2.0)
		# Freeze animation (grass wind, clouds, TIME) so the only difference between captures is the contact term.
		Engine.time_scale = 0.0
		await COMMON.settle(self, 0.5)
		var off := await _screenshot()
		off.save_png(out_dir.path_join(pose_name + "_off.png"))
		await COMMON.settle(self, 1.0)
		var off2 := await _screenshot()
		var floor_stats := _compare(off, off2)
		print("CONTACT_CAPTURE pose=%s NOISE_FLOOR off-vs-off darker=%.4f brighter=%.4f max_channel_diff=%d" % [pose_name, floor_stats.darker, floor_stats.brighter, floor_stats.max_diff])
		var entry := {"files": {"off": pose_name + "_off.png"}, "variants": {}, "noise_floor": floor_stats}
		var default_on: Image = null
		for strength in strengths:
			_contact(true, steps, float(strength))
			await COMMON.settle(self, 2.0)
			var status: String = str(graphics.get("EffectiveContactShadowStatus"))
			var on := await _screenshot()
			var name := "%s_on_g%s" % [pose_name, strength]
			on.save_png(out_dir.path_join(name + ".png"))
			var stats := _compare(off, on)
			print("CONTACT_CAPTURE pose=%s grass_strength=%s changed_px=%.4f darker(>3)=%.4f brighter(>3)=%.4f mean_luma_delta=%.5f max_channel_diff=%d status=%s" % [pose_name, strength, stats.changed, stats.darker, stats.brighter, stats.luma, stats.max_diff, status])
			entry["variants"][strength] = stats
			if default_on == null or is_equal_approx(float(strength), 0.35):
				default_on = on
		if default_on != null:
			_save_side_by_side(off, default_on, pose_name)
		# The contact term alone (debug mode 3), default strengths.
		_contact(true, steps, 0.35)
		graphics.call("SetShadowTuningOverride", "ShadowDebugMode", 3)
		await COMMON.settle(self, 1.5)
		(await _screenshot()).save_png(out_dir.path_join(pose_name + "_contact_term.png"))
		graphics.call("SetShadowTuningOverride", "ShadowDebugMode", 0)
		Engine.time_scale = 1.0
		report["poses"][pose_name] = entry
	_contact(false, steps, 0.35)
	var file := FileAccess.open(out_dir.path_join("contact_capture.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report, "\t"))
		file.close()
	print("CONTACT_CAPTURE=DONE out=" + out_dir)
	game.queue_free()
	await RenderingServer.frame_post_draw
	quit(0)


func _contact(enabled: bool, steps: int, grass_strength: float) -> void:
	graphics.call("SetShadowTuningOverride", "ContactShadowEnabled", enabled)
	graphics.call("SetShadowTuningOverride", "ContactShadowSteps", steps)
	graphics.call("SetShadowTuningOverride", "ContactShadowGrassStrength", grass_strength)


func _lock_exposure() -> void:
	atmosphere.set("storm_auto_exposure_enabled", false)
	await COMMON.settle(self, 0.3)
	var attributes := world_environment.camera_attributes as CameraAttributesPractical
	if attributes != null and attributes.auto_exposure_enabled:
		attributes.auto_exposure_enabled = false


func _screenshot() -> Image:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image.get_format() != Image.FORMAT_RGBA8:
		image.convert(Image.FORMAT_RGBA8)
	return image


func _compare(a: Image, b: Image) -> Dictionary:
	var da := a.get_data()
	var db := b.get_data()
	var changed := 0
	var darker := 0
	var brighter := 0
	var max_diff := 0
	var luma := 0.0
	var pixels := da.size() / 4
	for i in range(0, da.size(), 4):
		var d := 0
		for c in 3:
			d = maxi(d, absi(int(da[i + c]) - int(db[i + c])))
		if d > 0:
			changed += 1
			max_diff = maxi(max_diff, d)
		var delta := (0.2126 * float(db[i]) + 0.7152 * float(db[i + 1]) + 0.0722 * float(db[i + 2])) - (0.2126 * float(da[i]) + 0.7152 * float(da[i + 1]) + 0.0722 * float(da[i + 2]))
		luma += delta
		if delta < -3.0:
			darker += 1
		elif delta > 3.0:
			brighter += 1
	return {"changed": float(changed) / float(pixels), "darker": float(darker) / float(pixels), "brighter": float(brighter) / float(pixels), "luma": luma / float(pixels) / 255.0, "max_diff": max_diff}


func _save_side_by_side(off: Image, on: Image, pose_name: String) -> void:
	var size := off.get_size()
	var crop := Rect2i(size.x / 4, size.y / 2, size.x / 2, size.y / 2)
	var crop_off := off.get_region(crop)
	var crop_on := on.get_region(crop)
	var combined := Image.create_empty(crop.size.x * 2 + 8, crop.size.y, false, Image.FORMAT_RGBA8)
	combined.fill(Color(1, 0, 1, 1))
	combined.blit_rect(crop_off, Rect2i(Vector2i.ZERO, crop.size), Vector2i.ZERO)
	combined.blit_rect(crop_on, Rect2i(Vector2i.ZERO, crop.size), Vector2i(crop.size.x + 8, 0))
	combined.save_png(out_dir.path_join(pose_name + "_side_by_side.png"))
	var diff := Image.create_empty(size.x, size.y, false, Image.FORMAT_RGBA8)
	var da := off.get_data()
	var db := on.get_data()
	var out := PackedByteArray()
	out.resize(da.size())
	for i in range(0, da.size(), 4):
		for c in 3:
			out[i + c] = mini(255, absi(int(da[i + c]) - int(db[i + c])) * 8)
		out[i + 3] = 255
	diff.set_data(size.x, size.y, false, Image.FORMAT_RGBA8, out)
	diff.save_png(out_dir.path_join(pose_name + "_diff.png"))
