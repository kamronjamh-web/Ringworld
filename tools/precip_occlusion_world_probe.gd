extends SceneTree
# Real-world check of the precipitation occlusion map (diagnostic, windowed Vulkan, not --headless; no other Godot process):
#   Godot --path . --resolution 960x540 --script res://tools/precip_occlusion_world_probe.gd [-- --out <dir>]
# Loads scenes/VoxelRingIntegrationTest.tscn, captures the main view with and without the producer (lights move to their own layer),
# then reports per-update GPU cost, render info and map-vs-CPU-terrain agreement. Prints PRECIP_WORLD lines and PRECIP_WORLD=DONE.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
const HELPER := preload("res://tools/PrecipOcclusionWorldProbe.cs")
var world: Node
var out_dir := ""
var _ts: Array = []

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("PRECIP_WORLD=FAIL requires Vulkan"); quit(1); return
	call_deferred("_run")

func frames(n: int) -> void:
	for i in n: await process_frame

func scan(map_id: int) -> void:
	var rd := RenderingServer.get_rendering_device()
	RenderingServer.call_on_render_thread(func():
		var b := -1.0; var e := -1.0
		for i in rd.get_captured_timestamps_count():
			var nm := rd.get_captured_timestamp_name(i)
			if nm.find(str(map_id)) < 0: continue
			var t := float(rd.get_captured_timestamp_gpu_time(i)) / 1000000.0
			if nm.begins_with("vp_begin"): b = t
			elif nm.begins_with("vp_end"): e = t
		_ts.append([b, e]))

func _run() -> void:
	create_timer(420.0).timeout.connect(func(): push_error("PRECIP_WORLD=FAIL watchdog"); quit(1))
	out_dir = ProjectSettings.globalize_path("res://.godot/storm_s1/world_probe")
	var args := OS.get_cmdline_user_args()
	for i in args.size():
		if args[i] == "--out" and i + 1 < args.size(): out_dir = args[i + 1]
	DirAccess.make_dir_recursive_absolute(out_dir)
	print("PRECIP_WORLD device=", RenderingServer.get_video_adapter_name())
	world = WORLD.instantiate()
	var isolation: Dictionary = ISOLATION.new().ExecuteFixture("isolation", world, out_dir + "/preferences")
	print("PRECIP_WORLD isolation=", isolation.get("pass", false))
	var producer := world.get_node_or_null("PrecipitationOcclusion")
	if producer != null: world.remove_child(producer); producer.free()
	root.add_child(world)
	await frames(8)
	for node_name in ["RingVolumetricClouds", "SunshineCloudBridge", "DistantRainCurtains", "LocalVolumetricCloudRenderer", "WeatherAmbience", "TerrainMinimap"]:
		var n := world.get_node_or_null(node_name)
		if n != null: n.set_process(false); n.set_physics_process(false)
	var day_night := world.get_node_or_null("RingDayNightCycle")
	if day_night != null: day_night.set("Paused", true)
	# Let streaming settle.
	for i in 600:
		await process_frame
	var explorer := world.get_node("Explorer")
	print("PRECIP_WORLD explorer_position=", explorer.global_position)
	await frames(30)
	var before := root.get_texture().get_image(); before.save_png(out_dir + "/main_without_producer.png")
	var occlusion: Node = preload("res://scripts/Weather/RingPrecipitationOcclusion.cs").new()
	occlusion.name = "PrecipitationOcclusion"; world.add_child(occlusion)
	await frames(240)
	print("PRECIP_WORLD producer ready=", occlusion.get("Ready"), " renders=", occlusion.get("RenderCount"), " last=", occlusion.get("LastRenderReason"), " supported=", occlusion.get("Supported"))
	var after := root.get_texture().get_image(); after.save_png(out_dir + "/main_with_producer.png")
	# A-B-A: lights on their own layer vs on layer 1 vs back, same world state, 6 frames apart (noise floor = A vs A2).
	var lights: Array = []
	var stack2: Array = [root]
	while not stack2.is_empty():
		var n2: Node = stack2.pop_back(); for c2 in n2.get_children(): stack2.append(c2)
		if n2 is Light3D: lights.append(n2)
	print("PRECIP_WORLD lights_found=", lights.size(), " layers=", lights.map(func(l): return l.layers))
	await frames(10); root.get_texture().get_image().save_png(out_dir + "/aba_a1.png")
	for l in lights: l.layers = 1
	await frames(8); root.get_texture().get_image().save_png(out_dir + "/aba_b_lights_layer1.png")
	for l in lights: l.layers = 1 << 19
	await frames(8); root.get_texture().get_image().save_png(out_dir + "/aba_a2.png")
	var info: Dictionary = occlusion.call("GetRenderInfoDictionary")
	print("PRECIP_WORLD map_render_info ", info)
	# Cost per update (gpu timestamps vp_begin/vp_end), 20 forced renders.
	var map_rid: RID = (occlusion.get("MapViewport") as SubViewport).get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(map_rid, true)
	await frames(4)
	var samples: Array[float] = []
	for i in 20:
		var before_count := int(occlusion.get("RenderCount")); occlusion.call("RequestRender")
		var guard := 0
		while int(occlusion.get("RenderCount")) <= before_count and guard < 200:
			await process_frame; guard += 1
		var best := 0.0
		for f in 8:
			_ts.clear(); scan(map_rid.get_id()); await process_frame
			for pair in _ts:
				if float(pair[0]) >= 0.0 and float(pair[1]) > float(pair[0]): best = maxf(best, float(pair[1]) - float(pair[0]))
		if best > 0.0: samples.append(best)
	samples.sort()
	if samples.size() > 0:
		print("PRECIP_WORLD cost_per_update gpu_ms median=%.3f p95=%.3f max=%.3f samples=%d" % [samples[samples.size() / 2], samples[mini(samples.size() - 1, int(samples.size() * 0.95))], samples[-1], samples.size()])
	else: print("PRECIP_WORLD cost_per_update no samples")
	info = occlusion.call("GetRenderInfoDictionary")
	print("PRECIP_WORLD map_render_info_final ", info)
	# Map vs CPU terrain.
	var rid: RID = occlusion.get("PublishedTextureRid")
	var holder: Array = []
	var rd := RenderingServer.get_rendering_device()
	RenderingServer.call_on_render_thread(func(): holder.append(rd.texture_get_data(rid, 0)))
	var guard2 := 0
	while holder.is_empty() and guard2 < 60:
		await process_frame; guard2 += 1
	if not holder.is_empty():
		var cmp: Dictionary = HELPER.new().CompareOpen(occlusion, world.get_node("ChunkGridManager"), world.get_node_or_null("SmoothTerrainGrid"), holder[0])
		print("PRECIP_WORLD open_vs_cpu_terrain ", cmp)
	print("PRECIP_WORLD probe fresh=", occlusion.get("IsFresh"), " coverHeight=", occlusion.get("ListenerCoverHeightMeters"), " open=", occlusion.get("ListenerOpenFraction"), " completed=", occlusion.get("ProbeCompleted"), " error=", occlusion.get("ProbeError"))
	occlusion.call("SetDebugOverlay", true)
	await frames(30)
	root.get_texture().get_image().save_png(out_dir + "/overlay_world.png")
	pass
	print("PRECIP_WORLD=DONE out=", out_dir)
	quit(0)

func list_map_instances(occlusion: Node) -> void:
	# Which geometry the map camera can draw: layers inside the cull mask, visible, AABB overlapping the map prism.
	var t: Transform3D = occlusion.get("PublishedTransform")
	var far: float = occlusion.get("PublishedFarMeters")
	var inv := t.affine_inverse()
	var rows: Array = []
	var stack: Array = [root]
	while not stack.is_empty():
		var n: Node = stack.pop_back()
		for c in n.get_children(): stack.append(c)
		if not (n is GeometryInstance3D): continue
		var g := n as GeometryInstance3D
		if (g.layers & 0x3FFFF) == 0 or not g.is_visible_in_tree() or g.cast_shadow == GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY: continue
		var box := g.global_transform * g.get_aabb()
		var local := inv * box
		if absf(local.position.x + local.size.x * 0.5) > 64.0 + local.size.x * 0.5 or absf(local.position.y + local.size.y * 0.5) > 64.0 + local.size.y * 0.5: continue
		if local.position.z + local.size.z < -far or local.position.z > 0.5: continue
		rows.append("%s layers=%d size=(%.0f,%.0f,%.0f) zrange=(%.0f..%.0f) class=%s" % [g.get_path(), g.layers, local.size.x, local.size.y, local.size.z, local.position.z, local.position.z + local.size.z, g.get_class()])
	print("PRECIP_WORLD instances_in_map=%d" % rows.size())
	for i in mini(rows.size(), 40): print("PRECIP_WORLD   ", rows[i])

func map_stats(occlusion: Node) -> String:
	var rid: RID = occlusion.get("PublishedTextureRid")
	var holder: Array = []
	var rd := RenderingServer.get_rendering_device()
	RenderingServer.call_on_render_thread(func(): holder.append(rd.texture_get_data(rid, 0)))
	var guard := 0
	while holder.is_empty() and guard < 60:
		await process_frame; guard += 1
	if holder.is_empty(): return "no data"
	var data := (holder[0] as PackedByteArray).to_float32_array()
	var values: Array = []
	for i in range(0, data.size(), 61): values.append(data[i])
	values.sort()
	return "min=%.1f p10=%.1f median=%.1f p90=%.1f max=%.1f plane_to_listener=%.1f" % [values[0], values[values.size() / 10], values[values.size() / 2], values[values.size() * 9 / 10], values[-1], float(occlusion.get("PublishedFarMeters")) - 64.0]

func bisect(occlusion: Node) -> void:
	print("PRECIP_WORLD stats_all ", await map_stats(occlusion))
	for child in world.get_children():
		if not (child is Node3D) or not (child as Node3D).visible: continue
		var count := 0
		var stack: Array = [child]
		while not stack.is_empty():
			var n: Node = stack.pop_back(); for c in n.get_children(): stack.append(c)
			if n is GeometryInstance3D: count += 1
		if count == 0: continue
		(child as Node3D).visible = false
		var before_count := int(occlusion.get("RenderCount")); occlusion.call("RequestRender")
		var guard := 0
		while int(occlusion.get("RenderCount")) <= before_count and guard < 200:
			await process_frame; guard += 1
		await frames(3)
		print("PRECIP_WORLD hide ", child.name, " (", count, " geometry) -> ", await map_stats(occlusion))
		(child as Node3D).visible = true
