extends RefCounted
## Shared boot and helpers for the tree visibility harnesses (tree_visibility_oracle.gd, tree_frustum_identity_gpu_test.gd, tree_cull_verify_sweep.gd).
## Boots the REAL start menu on the isolated slot 3 copy that tools/tree_visibility_run.ps1 prepares and presses Continue, as the player does.
## Refuses to run when user:// is not an isolated test profile, so it can never touch the player's save.

const COMMON := preload("res://tools/shadow_harness_common.gd")


static func profile_is_isolated() -> bool:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming):
		return false
	return user_dir.contains("/.godot/") and (user_dir.contains("/appdata_") or user_dir.contains("perf_pass_real"))


## Returns the game node, or null (reason in out_error[0]).
## layer_props are set on the BiomeDecorations layer as soon as the warm world creates it, before its first tree outputs (TreeCullVerify needs that).
static func boot_game(tree: SceneTree, out_error: Array, layer_props := {}) -> Node:
	var applied := layer_props.is_empty()
	tree.change_scene_to_file("res://scenes/StartMenu.tscn")
	await tree.process_frame
	await tree.process_frame
	var menu: Node = tree.current_scene
	if menu == null or menu.name != "StartMenu":
		out_error.append("start menu did not load")
		return null
	var deadline := Time.get_ticks_msec() + 900000
	while Time.get_ticks_msec() < deadline:
		await tree.process_frame
		if not applied:
			applied = _apply_layer_props(tree, layer_props)
		if bool(menu.get("_cover_retired")) and menu.get("flight_director") != null and not bool(menu.get("menu_locked")):
			break
	var warm: Node = tree.root.get_node_or_null("RingWarmWorld")
	if warm == null:
		out_error.append("RingWarmWorld missing")
		return null
	var handed := [null]
	warm.handed_off.connect(func(w: Node): handed[0] = w)
	if not applied:
		applied = _apply_layer_props(tree, layer_props)
	(menu.get("continue_button") as Button).pressed.emit()
	deadline = Time.get_ticks_msec() + 180000
	while handed[0] == null and Time.get_ticks_msec() < deadline:
		await tree.process_frame
	if handed[0] == null:
		out_error.append("continue hand-off never happened")
		return null
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	await COMMON.settle(tree, 1.0)
	return handed[0]


static func _apply_layer_props(tree: SceneTree, props: Dictionary) -> bool:
	var layer: Node = tree.root.find_child("BiomeDecorations", true, false)
	if layer == null:
		return false
	for k in props:
		layer.set(k, props[k])
	print("TVH layer props %s applied to %s at %d ms (GpuTreeCullingActive=%s)" % [str(props), str(layer.get_path()), Time.get_ticks_msec(), str(layer.get("GpuTreeCullingActive"))])
	return true


static func load_poses() -> Dictionary:
	var path := "res://tools/fixtures/tree_visibility_poses.json"
	var poses: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(path))
	poses["spawn_ground"] = {"name": "spawn_ground", "around_m": poses["save_around_m"], "across_m": poses["save_across_m"], "yaw_deg": 0.0, "pitch_deg": 0.0}
	return poses


## True when the node exposes the property. The OC-2 switches are plain C# properties (not [Export]), which get_property_list() does not list but get() reads.
static func has_property(node: Object, prop: String) -> bool:
	if node == null:
		return false
	if node.get(prop) != null:
		return true
	for p in node.get_property_list():
		if String(p.get("name", "")) == prop:
			return true
	return false


## Hides the moving or noisy scene parts that are not the tree path (clouds, cloud bridge and driver, grass, gust-front leaf debris) so two captures of one state can be byte-identical.
static func quiet_scene(game: Node) -> Array:
	var hidden: Array = []
	for n in ["RingVolumetricClouds", "SunshineCloudBridge", "SunshineCloudsDriver", "StandingGrass"]:
		var node := game.get_node_or_null(n)
		if node == null:
			continue
		if "visible" in node:
			node.set("visible", false)
		else:
			node.process_mode = Node.PROCESS_MODE_DISABLED
		hidden.append(n)
	# RingGustFrontDebris (leaf and dust quads carried by gusts) advances on real frame time and the live wind whatever the held clocks say, so a leaf crossing the
	# view in one capture and not the next reads as a tree difference. ForcedLevel 0 hides it.
	var debris := game.get_node_or_null("GustFrontDebris")
	if debris != null:
		debris.set("ForcedLevel", 0.0)
		debris.set("MegastormLevel", 0.0)
		hidden.append("GustFrontDebris(ForcedLevel=0)")
	return hidden


## Teleports to a pose at a yaw, waits for streaming to go idle, sets noon.
static func go_pose(tree: SceneTree, game: Node, pose: Dictionary, yaw: float, hold_clocks: bool) -> bool:
	var explorer: Node = game.get_node("Explorer")
	var surface: Node = game.get_node("Terrain3DGameplay")
	var a := float(pose["around_m"])
	var z := float(pose["across_m"])
	var ground := float(surface.call("SampleHeight", a, z))
	explorer.call("TeleportToCanonicalMeters", a, ground + 1.7, z, yaw, float(pose.get("pitch_deg", 0.0)), true)
	await COMMON.settle(tree, 0.3)
	var idle: bool = await COMMON.wait_builds_idle(tree, game, 180.0)
	COMMON.apply_time(game, {"mode": "noon"})
	if hold_clocks:
		COMMON.hold_world_clocks(game)
	await COMMON.settle(tree, 4.0)
	idle = idle and await COMMON.wait_builds_idle(tree, game, 60.0)
	return idle


## Everything that moves on its own, held. The game re-asserts the wind globals, so call hold_wind() every frame it matters.
static func freeze_world(game: Node) -> void:
	COMMON.hold_world_clocks(game)
	var local_weather := game.get_node_or_null("LocalWeatherEffects")
	if local_weather != null:
		local_weather.set("Enabled", false)
	var bridge := game.get_node_or_null("SunshineCloudBridge")
	if bridge != null:
		bridge.set("CloudWindMultiplier", 0.0)
	hold_wind()


## Removes per-frame noise that is not part of the tree path, so two captures of one state can be byte-identical: "clouds" turns the Sunshine cloud
## interleave and resolution jitter off; "taa" turns temporal AA off on the root viewport. Returns what the viewport looks like (for the report).
static func freeze_noise(tree: SceneTree, game: Node, what: PackedStringArray) -> Dictionary:
	var info := {"use_taa": tree.root.use_taa, "msaa_3d": tree.root.msaa_3d, "screen_space_aa": tree.root.screen_space_aa, "scaling_3d_mode": tree.root.scaling_3d_mode,
		"scaling_3d_scale": tree.root.scaling_3d_scale, "frozen": what}
	if "clouds" in what:
		var driver := game.get_node_or_null("SunshineCloudsDriver")
		var clouds: Resource = driver.get("clouds_resource") as Resource if driver != null else null
		if clouds != null:
			info["cloud_interleave_before"] = clouds.get("cloud_interleave")
			clouds.set("cloud_interleave", 0)
			clouds.set("resolution_jitter_enabled", false)
	if "taa" in what:
		tree.root.use_taa = false
	return info


static func hold_wind() -> void:
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 0.0)


static func hide_overlays(game: Node) -> void:
	# HUD and menu layers are not part of the comparison.
	for child in game.get_children():
		if child is CanvasLayer:
			(child as CanvasLayer).visible = false
	var tree := game.get_tree()
	for child in tree.root.get_children():
		if child is CanvasLayer:
			(child as CanvasLayer).visible = false


## Main viewport RGBA8 image.
static func grab(tree: SceneTree) -> Image:
	var img := tree.root.get_viewport().get_texture().get_image()
	if img.get_format() != Image.FORMAT_RGBA8:
		img.convert(Image.FORMAT_RGBA8)
	return img
