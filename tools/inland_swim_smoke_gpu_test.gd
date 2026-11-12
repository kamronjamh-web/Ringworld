extends SceneTree

## T2R-10: the player swims in still lake and river-pool water of a water.rivers world (docs/work-orders/terrain-vegetation/T2.md). Boots the landscape harness world (generation 1 with
## water.rivers; the harness bakes the river graph), teleports above a deep lake point and a deep river pool, lets the production swim motor take over, and checks IsSwimming, the water
## surface height against the sampler's exact level (0.05 m), that the player floats, and that a bank point beside the water is dry land (not swimming).
## RUN: tools/run_tests.ps1 -Tests inland_swim_smoke_gpu_test  (manifest user_args: --generation=1 --review-features=water.rivers). Prints INLAND_SWIM_SMOKE=PASS|FAIL.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const PROBE := preload("res://tools/InlandSwimProbe.cs")
const MARKER := "INLAND_SWIM_SMOKE"
const SETTLE_PHYSICS_FRAMES := 300

var booted := {}
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(1200.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1200 s)"))
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
	var explorer := game.get_node_or_null("Explorer")
	if explorer == null:
		_fail("Explorer missing")
		return
	# The systems that resolve the grid's shared sampler in _Ready (global LOD, far viewport, minimap, clouds, weather) must find it in a river world. The grid is configured before the
	# graph is baked, so its sampler is built on the first read after the bake; without that these systems switch themselves off and distant terrain and water lose their shading.
	var minimap := game.get_node_or_null("TerrainMinimap")
	var far := game.get_node_or_null("ScaledFarViewport")
	if (minimap != null and not minimap.is_processing()) or (far != null and bool(far.get("Enabled")) and not far.is_processing()):
		_fail("the shared terrain sampler was missing when the world was attached (the far viewport or the minimap switched itself off)")
		return
	var probe: RefCounted = PROBE.new()
	var points: Dictionary = probe.call("FindPoints", game)
	if not bool(points.get("ok", false)):
		_fail("no swim points: %s" % str(points.get("error", "")))
		return
	print("%s lake=%s" % [MARKER, str(points["lake"])])
	print("%s river=%s" % [MARKER, str(points["river"])])
	var failures: Array = []
	for key in ["lake", "river"]:
		var p: Dictionary = points[key]
		var level := float(p["level"])
		var pose := {"name": "swim_" + key, "around_m": p["around"], "across_m": p["across"], "lift_m": level + 4.0, "vertical_mode": "abs", "ground_m": level, "yaw_deg": 0.0, "pitch_deg": 0.0}
		var info: Dictionary = await HARNESS.go_pose(self, game, pose, "noon")
		if info.has("error"):
			failures.append("%s: %s" % [key, str(info["error"])])
			continue
		# Release into the production motor above the water and let it fall in.
		explorer.call("TeleportToCanonicalMeters", float(p["around"]), level + 2.0, float(p["across"]), 0.0, 0.0, false)
		for i in SETTLE_PHYSICS_FRAMES:
			await physics_frame
		var state: Dictionary = probe.call("Read", explorer)
		var surface_error := absf(float(state["surface"]) - level)
		var floating := float(state["vertical"]) > level - 2.5 and float(state["vertical"]) < level + 0.2
		print("%s %s swimming=%s surface=%.3f level=%.3f vertical=%.3f error=%.4f" % [MARKER, key, str(state["swimming"]), float(state["surface"]), level, float(state["vertical"]), surface_error])
		if not bool(state["swimming"]):
			failures.append("%s: not swimming" % key)
		if surface_error > 0.05:
			failures.append("%s: water surface %.3f is %.3f m from the sampler level %.3f" % [key, float(state["surface"]), surface_error, level])
		if not floating:
			failures.append("%s: not floating (vertical %.3f, level %.3f)" % [key, float(state["vertical"]), level])
		# The bank: dry land beside the water is not swimming.
		if float(p["bank_distance"]) < 1e8:
			var dry_ground := float(p["dry_ground"])
			explorer.call("TeleportToCanonicalMeters", float(p["dry_around"]), dry_ground + 1.0, float(p["dry_across"]), 0.0, 0.0, true)
			await HARNESS.COMMON.settle(self, 0.5)
			await HARNESS.COMMON.wait_builds_idle(self, game, 120.0)
			explorer.call("TeleportToCanonicalMeters", float(p["dry_around"]), dry_ground + 1.0, float(p["dry_across"]), 0.0, 0.0, false)
			for i in 120:
				await physics_frame
			var bank: Dictionary = probe.call("Read", explorer)
			print("%s %s bank swimming=%s vertical=%.3f ground=%.3f distance=%.0f m" % [MARKER, key, str(bank["swimming"]), float(bank["vertical"]), dry_ground, float(p["bank_distance"])])
			if bool(bank["swimming"]):
				failures.append("%s: still swimming on the bank" % key)
		else:
			failures.append("%s: no bank found within 600 m" % key)
	if not failures.is_empty():
		_fail("; ".join(failures))
		return
	_cleanup()
	finished = true
	print("%s=PASS" % MARKER)
	quit(0)


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
