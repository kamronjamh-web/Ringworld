extends SceneTree

## Shadow revamp S4c in-game smoke (docs/shadow-revamp-plan.md S4): boots the real gameplay scene, enables the terrain height shadow producer, waits for the
## first publish and checks the published globals, the world -> anchor frame (the camera must decode to its own canonical position inside the L0 bounds),
## publish atomicity while crossfading (front/previous never equal the layer being built), hold / release (underground and eclipse hub: nothing starts while held,
## the first frame after release rebuilds even without sun movement), the debug overlay pixels, disable (globals invalid, GPU released) and that nothing else
## S5a receivers: with the producer published, ON vs OFF frames (ReceiversEnabled) are compared against an OFF-vs-OFF noise floor at forest_noon, ground_horizon and
## dawn (the 6 km sun map owns every blocker it contains, so ON must stay close to OFF); with the sun map off (authority 0) debug view 4 must be non-uniform and differ
## between receivers ON and OFF (the receivers visibly read the heightfield).
## RUN (windowed Vulkan, no other Godot process, editor closed):  Godot_console.exe --path . --script res://tools/height_shadow_gameplay_smoke.gd
## Flags: --publish-timeout=S (default 240), --keep-fixture. Prints HEIGHT_SHADOW_SMOKE=PASS or FAIL lines. Machine-stamped; timings are Debug-build numbers.

const COMMON := preload("res://tools/shadow_harness_common.gd")

var game: Node
var node: Node
var graphics: Node
var cycle: Node
var failures: Array[String] = []
var checks := 0


func _init() -> void:
	call_deferred("_run")


func _check(condition: bool, message: String) -> void:
	checks += 1
	if not condition:
		failures.append(message)
		push_error("HEIGHT_SHADOW_SMOKE check failed: " + message)


func _global(name: String):
	return node.call("GetGlobalValues").get(name)


func _diag() -> Dictionary:
	return node.call("GetDiagnostics")


func _wait_until(predicate: Callable, timeout_s: float) -> bool:
	var deadline := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	while Time.get_ticks_msec() < deadline:
		await process_frame
		if predicate.call():
			return true
	return false


func _nudge_sun(delta_fraction: float) -> void:
	var fraction := float(cycle.call("GetCycleTimeSeconds")) / maxf(1.0, float(cycle.get("FullCycleSeconds")))
	cycle.call("SetCycleFraction", fraction + delta_fraction)


func _frame_image() -> Image:
	await process_frame
	await process_frame
	return root.get_texture().get_image()


func _image_diff(a: Image, b: Image) -> Dictionary:
	var total := 0.0
	var worst := 0.0
	var count := 0
	var step := 7
	var x := 0
	while x < a.get_width():
		var y := 0
		while y < a.get_height():
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var d := maxf(absf(ca.r - cb.r), maxf(absf(ca.g - cb.g), absf(ca.b - cb.b))) * 255.0
			total += d
			worst = maxf(worst, d)
			count += 1
			y += step
		x += step
	return {"mean": total / maxf(1.0, float(count)), "max": worst, "samples": count}


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("HEIGHT_SHADOW_SMOKE=FAIL RenderingDevice is required (windowed Vulkan)")
		quit(1)
		return
	var machine: Dictionary = COMMON.machine_info()
	print("HEIGHT_SHADOW_SMOKE ", machine["label"], " adapter=", machine["adapter"])
	var fixture_root := "res://.godot/height-shadow-smoke/%d" % Time.get_ticks_usec()
	game = await COMMON.create_game(self, fixture_root)
	if game == null:
		push_error("HEIGHT_SHADOW_SMOKE=FAIL game boot failed")
		quit(1)
		return
	node = game.get_node_or_null("TerrainHeightShadow")
	graphics = game.get_node_or_null("GraphicsQuality")
	cycle = game.get_node_or_null("RingDayNightCycle")
	var explorer := game.get_node_or_null("Explorer")
	var surface := game.get_node_or_null("Terrain3DGameplay")
	_check(node != null and graphics != null and cycle != null and explorer != null and surface != null, "required scene nodes exist (TerrainHeightShadow, GraphicsQuality, RingDayNightCycle, Explorer)")
	if node == null or graphics == null or cycle == null:
		_finish()
		return
	COMMON.hold_world_clocks(game)
	var atmosphere := game.get_node_or_null("DaySideAtmosphere")
	if atmosphere != null:
		atmosphere.set("storm_auto_exposure_enabled", false)
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	print("HEIGHT_SHADOW_SMOKE booted, waiting for streaming idle")
	await COMMON.wait_builds_idle(self, game, 90.0, 60)
	var base: Dictionary = COMMON.pose_now(game)
	var pose: Dictionary = await COMMON.go_pose(self, game, base, "forest_noon", 3.0, 60.0)
	print("HEIGHT_SHADOW_SMOKE pose ready: ", JSON.stringify(pose.get("time", {})))
	_check(not pose.has("error"), "forest_noon pose reachable: " + str(pose.get("error", "")))

	# ---- disabled: nothing runs, globals stay at their project defaults
	var settings: Dictionary = {}
	_check(not bool(graphics.get("EffectiveHeightShadowEnabled")), "feature defaults to off")
	await process_frame
	await process_frame
	var state0 = _global("ringworld_height_shadow_state")
	_check(state0 == null or (state0 as Vector4).w == 0.0, "state.w is 0 while disabled (nothing written yet)")
	_check(str(_diag()["phase"]) == "Disabled" and not bool(_diag()["gpu_ready"]), "disabled: no GPU session")
	var before_image: Image = await _frame_image()

	# ---- enable and wait for the first publish
	_check(bool(graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", true)), "HeightShadowEnabled accepted")
	var timeout := float(COMMON.arg_value("--publish-timeout=", "240"))
	var started := Time.get_ticks_msec()
	var published := false
	var last_print := Time.get_ticks_msec()
	while Time.get_ticks_msec() - started < int(timeout * 1000.0):
		await process_frame
		if int(_diag()["publishes"]) >= 1:
			published = true
			break
		if Time.get_ticks_msec() - last_print > 10000:
			last_print = Time.get_ticks_msec()
			var pd := _diag()
			print("HEIGHT_SHADOW_SMOKE waiting: phase=%s status=%s gpu_ready=%s fills=%s" % [pd["phase"], pd["status"], pd["gpu_ready"], pd["fills"]])
	var diag := _diag()
	print("HEIGHT_SHADOW_SMOKE first publish after %.1f s: phase=%s fills=%s fill_seconds=%.2f fill_texels=%s build_frames=%s gpu_ms=%s main_ms_avg=%.3f" % [
		(Time.get_ticks_msec() - started) / 1000.0, diag["phase"], diag["fills"], float(diag["fill_seconds"]), diag["fill_texels"], diag["build_frames"], diag["gpu_ms_last_build"], float(diag["main_thread_ms_avg"])])
	_check(published, "first publish within %.0f s (status: %s)" % [timeout, str(diag["status"])])
	if not published:
		_finish()
		return
	await process_frame
	await process_frame
	diag = _diag()
	_check(int(diag["front"]) == 0 and int(diag["prev"]) == -1 and is_equal_approx(float(diag["crossfade"]), 1.0) and int(diag["light"]) == 1, "first publish is a snap of layer 0 for the sun: " + str([diag["front"], diag["prev"], diag["crossfade"], diag["light"]]))

	# ---- globals
	print("HEIGHT_SHADOW_SMOKE raw globals: state=", _global("ringworld_height_shadow_state"), " l0_params=", _global("ringworld_height_shadow_l0_params"))
	var state = _global("ringworld_height_shadow_state")
	var p0 = _global("ringworld_height_shadow_l0_params")
	var p1 = _global("ringworld_height_shadow_l1_params")
	var b0 = _global("ringworld_height_shadow_l0_bounds")
	var b1 = _global("ringworld_height_shadow_l1_bounds")
	var shape = _global("ringworld_height_shadow_shape")
	_check(state.x == 0.0 and state.y == -1.0 and state.z == 1.0 and state.w == 1.0, "state global = (front 0, prev -1, t 1, sun): " + str(state))
	_check(p0.x == 4.0 and is_equal_approx(p0.y, 1.0 / (1024.0 * 4.0)) and p0.z == 1.0, "level 0 params: " + str(p0))
	_check(p1.x == 16.0 and is_equal_approx(p1.y, 1.0 / (1024.0 * 16.0)) and p1.z == 1.0, "level 1 params: " + str(p1))
	_check(b0.x < b0.y and b0.z < b0.w and b1.x < b1.y and b1.z < b1.w, "bounds are non-empty")
	_check(absf((b0.y - b0.x) - 1023.0 * 4.0) < 1.0 and absf((b1.y - b1.x) - 1023.0 * 16.0) < 1.0, "bounds span 1023 texels")
	_check(b1.x <= b0.x and b1.y >= b0.y and b1.z <= b0.z and b1.w >= b0.w, "the L1 window contains the L0 window")
	_check(shape.z > 1.0e6 and is_equal_approx(shape.y, 0.5), "shape global carries the floor radius and receiver bias: " + str(shape))

	# ---- frame: the camera decodes to its own canonical position inside the L0 bounds
	var camera := root.get_camera_3d()
	var frame = _global("ringworld_height_shadow_frame")
	var f: Vector4 = frame * Vector4(camera.global_position.x, camera.global_position.y, camera.global_position.z, 1.0)
	var radius: float = shape.z
	var around_local: float = radius * atan2(f.x, radius - f.z)
	var rho: float = Vector2(f.x, radius - f.z).length()
	var height: float = radius - rho
	var now: Dictionary = COMMON.pose_now(game)
	var anchor_around := float(diag["anchor_around"])
	var anchor_across := float(diag["anchor_across"])
	var circumference := 31415936.0
	var around_world := fposmod(anchor_around + around_local, circumference)
	var d_around := absf(around_world - float(now["around"]))
	d_around = minf(d_around, circumference - d_around)
	print("HEIGHT_SHADOW_SMOKE frame decode: around_local=%.2f across_local=%.2f height=%.2f vs player around=%.1f across=%.1f vertical=%.1f anchor=(%d, %d)" % [around_local, f.y, height, now["around"], now["across"], now["vertical"], anchor_around, anchor_across])
	_check(d_around < 6.0, "camera around decodes to the player's (%.2f m apart)" % d_around)
	_check(absf(anchor_across + f.y - float(now["across"])) < 6.0, "camera across decodes to the player's")
	_check(absf(height - float(now["vertical"])) < 8.0, "camera height above the floor decodes to the player's vertical (eye height tolerance)")
	_check(around_local >= b0.x and around_local <= b0.y and f.y >= b0.z and f.y <= b0.w, "camera lies inside the L0 bounds")

	# ---- receivers: ON vs OFF at forest_noon against the OFF-vs-OFF noise floor (the producer-only frame is the same as OFF)
	var disabled_vs_published := _image_diff(before_image, await _frame_image())
	print("HEIGHT_SHADOW_SMOKE frame diff (producer published + receivers on vs feature never enabled): mean=%.4f max=%.1f" % [disabled_vs_published["mean"], disabled_vs_published["max"]])
	await _measure_on_off("forest_noon")

	# ---- overlay pixels
	await _check_overlay()

	# ---- second publish: crossfade and atomicity while a build is in flight
	var generation := int(_diag()["generation"])
	var last_t := -1.0
	var atomic_ok := true
	var saw_fade := false
	var saw_build := false
	_nudge_sun(0.0008)
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d := _diag()
		var front := int(d["front"])
		var prev := int(d["prev"])
		if bool(d["build_in_flight"]):
			saw_build = true
			if front == int(d["build_layer"]) or prev == int(d["build_layer"]):
				atomic_ok = false
		if int(d["generation"]) > generation:
			var t := float(d["crossfade"])
			if prev >= 0 and t < 1.0:
				saw_fade = true
				if t < last_t - 1e-6:
					atomic_ok = false
			last_t = t
			if t >= 1.0:
				break
	var d2 := _diag()
	_check(int(d2["generation"]) == generation + 1, "second publish happened")
	_check(atomic_ok and saw_build, "no global pointed at the layer being built (saw build in flight: %s)" % str(saw_build))
	_check(saw_fade and int(d2["prev"]) == 0 and int(d2["front"]) == 1, "second publish crossfades layer 0 -> 1: " + str([d2["front"], d2["prev"], d2["crossfade"]]))
	_check(float(d2["crossfade"]) >= 0.999, "crossfade completed")

	# ---- hold / release (underground)
	var builds_before := int(_diag()["builds"])
	node.set("UndergroundHold", true)
	await process_frame
	_nudge_sun(0.0010)
	await COMMON.settle(self, 3.0)
	var held := _diag()
	_check(str(held["phase"]) == "Held" and bool(held["held"]), "underground hold -> phase Held: " + str(held["phase"]))
	_check(int(held["builds"]) == builds_before, "nothing started while held (builds %s -> %s)" % [builds_before, held["builds"]])
	var gen_held := int(held["generation"])
	node.set("UndergroundHold", false)
	var resumed: bool = await _wait_until(func(): return int(_diag()["generation"]) > gen_held, 120.0)
	_check(resumed, "first frames after release rebuild from the real light")
	await _wait_until(func(): return float(_diag()["crossfade"]) >= 0.999, 5.0)
	# release without any sun movement still rebuilds (the remembered direction is forgotten on hold)
	gen_held = int(_diag()["generation"])
	node.set("UndergroundHold", true)
	await COMMON.settle(self, 0.5)
	node.set("UndergroundHold", false)
	var resumed_same: bool = await _wait_until(func(): return int(_diag()["generation"]) > gen_held, 120.0)
	_check(resumed_same, "release rebuilds even though the sun did not move")
	await _wait_until(func(): return float(_diag()["crossfade"]) >= 0.999, 5.0)
	# eclipse hub hold
	var director := game.get_node_or_null("EclipseEvent")
	if director != null and director.has_method("SetHubLevelOverride"):
		var gen_hub := int(_diag()["generation"])
		director.call("SetHubLevelOverride", 1.0)
		var hub_held: bool = await _wait_until(func(): return bool(_diag()["held"]), 5.0)
		_check(hub_held, "eclipse hub (HoldMacroShadows) holds the producer")
		_nudge_sun(0.0010)
		await COMMON.settle(self, 2.0)
		_check(int(_diag()["generation"]) == gen_hub and str(_diag()["phase"]) == "Held", "nothing published while the hub holds")
		director.call("SetHubLevelOverride", -1.0)
		var hub_resumed: bool = await _wait_until(func(): return int(_diag()["generation"]) > gen_hub, 120.0)
		_check(hub_resumed, "release after the hub rebuilds")
		await _wait_until(func(): return float(_diag()["crossfade"]) >= 0.999, 5.0)

	# ---- unresolved statistics (optional readback)
	node.call("RequestUnresolvedStats")
	_nudge_sun(0.0012)
	var gen_stats := int(_diag()["generation"])
	await _wait_until(func(): return int(_diag()["generation"]) > gen_stats, 120.0)
	await COMMON.settle(self, 0.5)
	var stats := _diag()
	print("HEIGHT_SHADOW_SMOKE unresolved L0=%.2f%% L1=%.2f%% rows_per_frame=%s gpu_ms_last_build=%s gpu_ms_per_row=(%s, %s) render_callback_cpu_ms_max=%.3f main_ms_avg=%.3f" % [
		float(stats["unresolved_l0"]), float(stats["unresolved_l1"]), stats["rows_per_frame"], stats["gpu_ms_last_build"], stats["gpu_ms_per_row_l0"], stats["gpu_ms_per_row_l1"], float(stats["render_callback_cpu_ms_max"]), float(stats["main_thread_ms_avg"])])
	_check(float(stats["unresolved_l0"]) >= 0.0 and float(stats["unresolved_l0"]) < 50.0, "unresolved statistic is available and plausible at noon")

	# ---- ON vs OFF at the low-sun poses (hills shadow far terrain), then the receivers' visible use with the sun map off.
	# Elevations come after the named poses: a low sun leaves most texels unresolved (rays leave the window below the tallest terrain around),
	# so the receivers' visible-use check runs at the first elevation where the overlay shows resolved terrain shadows around the camera.
	var visible_elevation := -1.0
	for pose_name in ["ground_horizon", "dawn_8deg", "dawn_15deg", 20.0, 25.0, 30.0, 35.0]:
		node.call("RequestUnresolvedStats")
		var generation_before := int(_diag()["generation"])
		var label := str(pose_name)
		if pose_name is String:
			var gp: Dictionary = await COMMON.go_pose(self, game, base, pose_name, 3.0, 60.0)
			_check(not gp.has("error"), pose_name + " pose reachable: " + str(gp.get("error", "")))
		else:
			label = "sun_%ddeg" % int(pose_name)
			COMMON.apply_time(game, {"mode": "elevation", "value": pose_name, "rising": true})
			await COMMON.settle(self, 3.0)
		await _wait_until(func(): return int(_diag()["generation"]) > generation_before, 150.0)
		await _wait_until(func(): return float(_diag()["crossfade"]) >= 0.999 and str(_diag()["phase"]) == "Idle", 30.0)
		await COMMON.settle(self, 0.5)
		var pd := _diag()
		print("HEIGHT_SHADOW_SMOKE %s: status=%s authority=%s outside_max=%.0f m (pyramid=%s) unresolved L0=%.2f%% L1=%.2f%%" % [label, pd["status"], (_global("ringworld_height_shadow_shape") as Vector4).w, float(pd["outside_max"]), str(pd["outside_from_pyramid"]), float(pd["unresolved_l0"]), float(pd["unresolved_l1"])])
		await _measure_on_off(label)
		if visible_elevation < 0.0 and pose_name is float:
			var resolved_shadow := await _overlay_shadow_fraction()
			print("HEIGHT_SHADOW_SMOKE %s overlay: %.2f%% of resolved pixels are shadowed" % [label, resolved_shadow * 100.0])
			if resolved_shadow > 0.005:
				visible_elevation = pose_name
				await _check_receivers_visible()
	if visible_elevation < 0.0:
		print("HEIGHT_SHADOW_SMOKE no tested elevation resolved terrain shadows around the fixture: the visible-use check was skipped (the synthetic receiver test covers it)")

	# ---- disable
	graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", false)
	await COMMON.settle(self, 0.5)
	var off := _diag()
	var state_off = _global("ringworld_height_shadow_state")
	_check(str(off["phase"]) == "Disabled" and not bool(off["gpu_ready"]) and state_off.w == 0.0 and state_off.x < 0.0, "disabling invalidates the globals and releases the GPU session: " + str(state_off))
	_finish()


func _overlay_shadow_fraction() -> float:
	var overlay_script: Script = load("res://scripts/Shadows/RingHeightShadowOverlay.cs")
	var viewport := SubViewport.new()
	viewport.size = Vector2i(256, 256)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var overlay: Control = overlay_script.new()
	viewport.add_child(overlay)
	overlay.set("Mode", 0)
	for i in 6:
		await process_frame
	var image := viewport.get_texture().get_image()
	viewport.queue_free()
	var shadowed := 0
	var resolved := 0
	for y in range(0, 256, 2):
		for x in range(0, 256, 2):
			var c := image.get_pixel(x, y)
			if absf(c.r - 0.18) < 0.03 and absf(c.g - 0.18) < 0.03 and absf(c.b - 0.18) < 0.03:
				continue
			if c.r > 0.5 and c.g < 0.3:
				continue
			resolved += 1
			if c.b > c.r + 0.05 and c.r < 0.3:
				shadowed += 1
	return float(shadowed) / maxf(1.0, float(resolved))


func _luma_stats(image: Image) -> Dictionary:
	var count := 0
	var sum := 0.0
	var sum_sq := 0.0
	var dark := 0
	var x := 0
	while x < image.get_width():
		var y := 0
		while y < image.get_height():
			var c := image.get_pixel(x, y)
			var v := (c.r + c.g + c.b) / 3.0
			sum += v
			sum_sq += v * v
			count += 1
			if v < 0.5:
				dark += 1
			y += 7
		x += 7
	var mean := sum / maxf(1.0, float(count))
	return {"mean": mean, "std": sqrt(maxf(0.0, sum_sq / maxf(1.0, float(count)) - mean * mean)), "dark": float(dark) / maxf(1.0, float(count))}


## OFF (ReceiversEnabled false, producer still running) twice for the noise floor, then ON; prints the numbers and gates ON-vs-OFF at a few times the noise floor.
func _measure_on_off(label: String) -> Dictionary:
	node.set("ReceiversEnabled", false)
	await COMMON.settle(self, 0.4)
	var off_a: Image = await _frame_image()
	await COMMON.settle(self, 0.6)
	var off_b: Image = await _frame_image()
	node.set("ReceiversEnabled", true)
	await COMMON.settle(self, 0.4)
	var on: Image = await _frame_image()
	var noise := _image_diff(off_a, off_b)
	var on_off := _image_diff(off_a, on)
	print("HEIGHT_SHADOW_SMOKE ON-vs-OFF %s: noise floor (OFF vs OFF) mean=%.4f max=%.1f | ON vs OFF mean=%.4f max=%.1f (of 255, %d samples)" % [label, noise["mean"], noise["max"], on_off["mean"], on_off["max"], on_off["samples"]])
	_check(float(on_off["mean"]) <= maxf(1.0, float(noise["mean"]) * 3.0 + 0.5), "%s: ON stays close to OFF inside the sun map range (mean %.4f vs noise %.4f)" % [label, on_off["mean"], noise["mean"]])
	return {"noise": noise, "on_off": on_off}


## Sun map off (authority 0): the heightfield is authoritative, so debug view 4 (min of height and macro term) must be non-uniform with receivers ON and differ from OFF.
func _check_receivers_visible() -> void:
	var sun_requested = graphics.get("RequestedSunShadowsEnabled")
	graphics.call("SetShadowTuningOverride", "Enabled", false)
	graphics.call("SetShadowTuningOverride", "ShadowDebugMode", 4)
	await COMMON.settle(self, 1.0)
	var shape = _global("ringworld_height_shadow_shape")
	print("HEIGHT_SHADOW_SMOKE receivers-visible: shape=", shape, " (w = authority distance, 0 while the sun map is off)")
	_check(shape != null and (shape as Vector4).w == 0.0, "authority distance is 0 with the sun map off: " + str(shape))
	var shadowed := await _overlay_shadow_fraction()
	print("HEIGHT_SHADOW_SMOKE overlay (L0 visibility, 4.3 km around the camera): %.2f%% of resolved pixels are shadowed at this sun" % (shadowed * 100.0))
	var explorer := game.get_node_or_null("Explorer")
	var best_diff := 0.0
	var best_yaw := 0.0
	var best_on_dark := 0.0
	var best_off_dark := 0.0
	var best_std := 0.0
	var best_on: Image = null
	var best_off: Image = null
	for yaw in [0.0, 60.0, 120.0, 180.0, 240.0, 300.0]:
		explorer.call("SetPresentationView", yaw, 0.0)
		node.set("ReceiversEnabled", false)
		await COMMON.settle(self, 0.4)
		var off: Image = await _frame_image()
		node.set("ReceiversEnabled", true)
		await COMMON.settle(self, 0.4)
		var on: Image = await _frame_image()
		var stats_off := _luma_stats(off)
		var stats_on := _luma_stats(on)
		var diff := _image_diff(off, on)
		print("HEIGHT_SHADOW_SMOKE debug view 4 (sun map off) yaw %d: OFF std=%.4f dark=%.4f | ON std=%.4f dark=%.4f | ON vs OFF mean=%.4f max=%.1f" % [int(yaw), stats_off["std"], stats_off["dark"], stats_on["std"], stats_on["dark"], diff["mean"], diff["max"]])
		if float(diff["mean"]) > best_diff:
			best_diff = float(diff["mean"]); best_yaw = yaw; best_on_dark = float(stats_on["dark"]); best_off_dark = float(stats_off["dark"]); best_std = float(stats_on["std"])
			best_on = on; best_off = off
	if best_on != null:
		best_on.save_png(ProjectSettings.globalize_path("res://.godot/height-shadow-smoke/debug4_on.png"))
		best_off.save_png(ProjectSettings.globalize_path("res://.godot/height-shadow-smoke/debug4_off.png"))
	print("HEIGHT_SHADOW_SMOKE debug view 4 best yaw %d: ON-vs-OFF mean diff %.4f, dark ON %.4f vs OFF %.4f" % [int(best_yaw), best_diff, best_on_dark, best_off_dark])
	_check(best_std > 0.02, "debug view 4 is non-uniform with the heightfield authoritative (std %.4f)" % best_std)
	if shadowed > 0.005:
		_check(best_diff > 1.0 and best_on_dark > best_off_dark, "receivers visibly use the heightfield (ON darker than OFF, best mean diff %.4f)" % best_diff)
	else:
		print("HEIGHT_SHADOW_SMOKE no terrain shadow resolved around the camera at this sun: visible-use check skipped (the synthetic receiver test covers it)")
	explorer.call("SetPresentationView", 0.0, -10.0)
	graphics.call("SetShadowTuningOverride", "ShadowDebugMode", 0)
	graphics.call("SetShadowTuningOverride", "Enabled", sun_requested if sun_requested != null else true)
	await COMMON.settle(self, 0.5)


func _check_overlay() -> void:
	var overlay_script: Script = load("res://scripts/Shadows/RingHeightShadowOverlay.cs")
	var viewport := SubViewport.new()
	viewport.size = Vector2i(256, 256)
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var overlay: Control = overlay_script.new()
	viewport.add_child(overlay)
	for mode in [0, 1, 2, 3, 4]:
		overlay.set("Mode", mode)
		for i in 4:
			await process_frame
		var image := viewport.get_texture().get_image()
		var coloured := 0
		var total := 0
		for y in range(64, 192, 4):
			for x in range(64, 192, 4):
				var c := image.get_pixel(x, y)
				total += 1
				if absf(c.r - 0.18) > 0.03 or absf(c.g - 0.18) > 0.03 or absf(c.b - 0.18) > 0.03:
					coloured += 1
		print("HEIGHT_SHADOW_SMOKE overlay mode %d (%s): %d / %d sampled pixels carry data" % [mode, ["L0 vis", "L1 vis", "blocker", "unresolved", "bounds"][mode], coloured, total])
		_check(coloured > total / 2, "overlay mode %d shows published data around the camera (%d / %d)" % [mode, coloured, total])
		if mode == 4:
			var c4 := image.get_pixel(150, 128)
			_check(c4.b > c4.r, "overlay bounds mode tints the L0 window cyan")
	viewport.queue_free()


func _finish() -> void:
	var passed := failures.is_empty()
	if passed:
		print("HEIGHT_SHADOW_SMOKE=PASS checks=%d" % checks)
	else:
		push_error("HEIGHT_SHADOW_SMOKE=FAIL %d of %d checks failed: %s" % [failures.size(), checks, "; ".join(failures)])
	quit(0 if passed else 1)
