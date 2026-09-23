extends SceneTree

const SCENE := preload("res://scenes/BiomeAuthoringPreview.tscn")
const DEADLINE_MS := 30000
var _failed := false
var _watchdog: Timer

func _initialize() -> void: call_deferred("_run")
func _watchdog_timeout() -> void: _fail("global watchdog expired"); quit(1)
func _diag(preview: Node) -> Dictionary:
	var value: Variant = preview.call("GetPreviewDiagnostics")
	return value as Dictionary if value is Dictionary else {}
func _fail(message: String) -> void: _failed = true; push_error("BIOME_AUTHORING_PREVIEW=FAIL " + message)
func _wait_ready(preview: Node, label: String) -> void:
	var start := Time.get_ticks_msec()
	while Time.get_ticks_msec() - start < DEADLINE_MS:
		var d := _diag(preview)
		if String(d.get("error", "")) != "": _fail(label + ": " + String(d.get("error"))); return
		if bool(d.get("ready", false)): return
		await process_frame
	_fail(label + " timed out")
func _capture(name: String) -> void:
	if "--capture" not in OS.get_cmdline_user_args(): return
	var dir := "res://.godot/biome_workshop"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(dir))
	for frame in 3: await RenderingServer.frame_post_draw
	var image := root.get_viewport().get_texture().get_image()
	if image.is_empty(): _fail("empty capture " + name)
	elif image.save_png(dir + "/" + name) != OK: _fail("capture " + name)

func _run() -> void:
	_watchdog=Timer.new(); _watchdog.one_shot=true; _watchdog.timeout.connect(_watchdog_timeout); root.add_child(_watchdog); _watchdog.start(90)
	var preview: Node = SCENE.instantiate(); root.add_child(preview); await process_frame
	await _wait_ready(preview, "initial")
	if _failed: quit(1); return
	var initial := _diag(preview)
	if int(initial.get("extent_meters", 0)) != 10000 or int(initial.get("terrain_vertex_count", 0)) <= 0: _fail("10km terrain diagnostics")
	if int(initial.get("tree_count", 0)) <= 0 or int(initial.get("grass_count", 0)) <= 0: _fail("default vegetation diagnostics")
	print("BIOME_AUTHORING_PREVIEW_METRICS ", initial)
	preview.call("FrameOverview"); await _capture("overview_default.png")
	preview.call("FrameDetail"); await _capture("detail_default.png")
	# A duplicate must remain authoring-local and visibly alter every requested channel.
	var preset: Resource = preview.get("Preset")
	if preset == null: _fail("starter preset missing")
	else:
		var edited: Resource = preset.call("CreateDuplicateWithNewIdentity")
		edited.set("MeanElevationMeters", float(edited.get("MeanElevationMeters")) + 200.0)
		edited.set("GrassHeightMeters", 2.0); edited.set("GrassTint", Color.MAGENTA)
		edited.set("GroundMaterial", 2); edited.set("MapleWeight", 0.0); edited.set("FirWeight", 0.0); edited.set("PineWeight", 1.0)
		preview.call("SetPreset", edited); await _wait_ready(preview, "edited preset")
		var changed := _diag(preview); print("BIOME_AUTHORING_PREVIEW_EDITED_METRICS ", changed)
		if int(changed.get("built_revision",-1)) != int(changed.get("requested_revision",-2)): _fail("edited preset was not published")
		if not is_equal_approx(float(changed.get("elevation", -99999)), float(initial.get("elevation", -99999)) + 200.0): _fail("published elevation did not change")
		if not is_equal_approx(float(changed.get("grass_height", -1)), 2.0): _fail("published grass height did not change")
		if Color(changed.get("grass_tint", Color.BLACK)) != Color.MAGENTA: _fail("published grass tint did not change")
		if int(changed.get("ground_material", -1)) != 2: _fail("published ground material did not change")
		preview.call("FrameDetail"); await _capture("detail_edited.png")
	var old_root := preview.get_node_or_null("GeneratedAuthoringPreview")
	preview.call("RequestRebuild"); await process_frame
	if old_root == null: _fail("published geometry root missing")
	elif not is_instance_valid(old_root) or preview.get_node_or_null("GeneratedAuthoringPreview") != old_root: _fail("published geometry changed during rebuild")
	preview.call("RequestRebuild"); preview.call("RequestRebuild"); await _wait_ready(preview, "superseded rebuild")
	var completed := _diag(preview)
	if int(completed.get("built_revision", -1)) != int(completed.get("requested_revision", -2)): _fail("latest request not published")
	var drawer := preview.get_node_or_null("RingLiveTuningDrawer")
	if drawer == null: _fail("grave-key drawer missing")
	else:
		var grave := InputEventKey.new(); grave.keycode = KEY_QUOTELEFT; grave.pressed = true; Input.parse_input_event(grave)
		await process_frame
		await process_frame
		var panel := drawer.get_node_or_null("LiveTuningDrawer")
		if panel == null or not (panel as Control).visible: _fail("grave key did not open drawer")
		var name_edit := _find_named(drawer, "DisplayName") as LineEdit
		var material_menu := _find_named(drawer, "GroundMaterial") as OptionButton
		if name_edit == null or (name_edit as LineEdit).focus_mode == Control.FOCUS_NONE: _fail("preset name editor missing/focus disabled")
		if material_menu == null: _fail("ground material dropdown missing")
		var grass_height := _find_named(drawer, "GrassHeightMeters") as SpinBox
		if grass_height == null: _fail("grass height live control missing")
		else:
			grass_height.value = 1.5
			await _wait_ready(preview, "live grass height")
			if not is_equal_approx(float(_diag(preview).get("grass_height", -1)), 1.5): _fail("live grass height did not publish")
			preview.call("FrameDetail")
		await _capture("drawer.png")
	# Water/zero-density cases must complete, rather than leaving bounded placement loops active.
	if preset != null:
		var water: Resource = preset.call("CreateDuplicateWithNewIdentity")
		water.set("MeanElevationMeters", -20.0); water.set("ElevationVariationMeters", 0.0); water.set("DetailAmplitudeMeters", 0.0)
		preview.call("SetPreset", water); await _wait_ready(preview, "water preset")
		var wd := _diag(preview)
		if int(wd.get("tree_count", -1)) != 0 or int(wd.get("grass_count", -1)) != 0: _fail("water preset generated vegetation")
		if preview.get_node_or_null("GeneratedAuthoringPreview/AuthoredSeaPlane") == null: _fail("water preset lacks sea plane")
		var empty: Resource = preset.call("CreateDuplicateWithNewIdentity")
		empty.set("MeanElevationMeters", 100.0); empty.set("TreeDensity", 0.0); empty.set("GrassDensity", 0.0)
		preview.call("SetPreset", empty); await _wait_ready(preview, "zero density preset")
		var ed := _diag(preview)
		if int(ed.get("tree_count", -1)) != 0 or int(ed.get("grass_count", -1)) != 0: _fail("zero density generated vegetation")
		var sparse: Resource = preset.call("CreateDuplicateWithNewIdentity")
		sparse.set("MeanElevationMeters", 100.0); sparse.set("ElevationVariationMeters", 0.0); sparse.set("DetailAmplitudeMeters", 0.0)
		sparse.set("TreeDensity", 0.0); sparse.set("GrassDensity", 1.25)
		preview.call("SetPreset", sparse); await _wait_ready(preview, "sparse grass")
		var sparse_count := int(_diag(preview).get("grass_count", 0))
		var dense: Resource = sparse.call("CreateDuplicateWithNewIdentity")
		dense.set("GrassDensity", 2.0)
		preview.call("SetPreset", dense); await _wait_ready(preview, "dense grass")
		var dense_count := int(_diag(preview).get("grass_count", 0))
		if sparse_count <= 0 or dense_count <= sparse_count: _fail("grass density range is not monotonic")
		var forest: Resource = preset.call("CreateDuplicateWithNewIdentity")
		forest.set("MeanElevationMeters", 100.0); forest.set("ElevationVariationMeters", 0.0); forest.set("DetailAmplitudeMeters", 0.0)
		forest.set("BaseBiome", 6); forest.set("TreeDensity", 1.0); forest.set("MinimumTreeSpacingMeters", 2.0)
		forest.set("Humidity", 1.0); forest.set("Temperature", .55); forest.set("ClearingStrength", 0.0); forest.set("TreeClustering", 0.0); forest.set("GrassDensity", 0.0)
		preview.call("SetPreset", forest); await _wait_ready(preview, "dense forest")
		var tree_total := 0; var quadrants := [false, false, false, false]
		var generated := preview.get_node_or_null("GeneratedAuthoringPreview")
		if generated != null:
			for child in generated.get_children():
				if child is MultiMeshInstance3D and String(child.name).begins_with("PreviewTrees_"):
					var multi := (child as MultiMeshInstance3D).multimesh
					for index in multi.instance_count:
						var origin := multi.get_instance_transform(index).origin; tree_total += 1
						quadrants[(0 if origin.x < 0.0 else 1) + (0 if origin.z < 0.0 else 2)] = true
		if tree_total <= 0 or tree_total > 1200 or false in quadrants: _fail("dense forest is not distributed across all quadrants")
	preview.call("RequestRebuild"); await process_frame; preview.queue_free(); await process_frame
	_watchdog.stop(); if not _failed: print("BIOME_AUTHORING_PREVIEW=PASS")
	quit(1 if _failed else 0)

func _find_node_type(node: Node, type) -> Node:
	if is_instance_of(node, type): return node
	for child in node.get_children():
		var found := _find_node_type(child, type)
		if found != null: return found
	return null

func _find_named(node: Node, wanted: String) -> Node:
	if String(node.name) == wanted: return node
	for child in node.get_children():
		var found := _find_named(child, wanted)
		if found != null: return found
	return null
