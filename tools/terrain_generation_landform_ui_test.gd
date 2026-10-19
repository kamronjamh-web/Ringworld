extends SceneTree

## Isolated playable/UI v7 landform path. It must never use the user recipe path.
const PLAYABLE_SCENE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const FIXTURE := "res://.godot/terrain-generation-tests/landform-ui-v3.tres"
const REPORT := "res://.godot/terrain-generation-tests/landform-ui-report.json"
const UPGRADE_TEXT := "Use hill-country landforms (v7)"
const FLOAT_CONTROLS := ["landform_scale_m", "plains_threshold", "hills_threshold", "plateau_scale_m", "plateau_threshold", "plateau_full_threshold", "hill_amplitude_m", "hill_scale_m", "hill_detail_weight", "hill_valley_exponent", "plains_hill_fraction", "hill_valley_floor", "plateau_lift_m", "plateau_step_m", "terrace_riser_width", "terrace_strength", "escarpment_jitter_m", "escarpment_scale_m", "breaks_strength", "breaks_step_m", "landform_shore_start_m", "landform_shore_full_m", "lowland_detail_floor", "plains_detail_fraction", "lowland_erosion_strength_m"]

var _checks := 0
var _failures := PackedStringArray()

func _init() -> void:
	create_timer(180.0).timeout.connect(func(): _failures.append("180 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE.get_base_dir()))
	var v6: TerrainGenerationRecipe = TerrainGenerationRecipe.create_climate_default()
	v6.seed = 90210; v6.erosion_strength_m = 83.0; v6.land_balance = 0.07
	_expect(TerrainGenerationPreviewStore.save_recipe(v6, FIXTURE).is_empty(), "isolated playable v6 fixture saves")
	var bytes_before := FileAccess.get_file_as_bytes(FIXTURE)
	var playable: Node = PLAYABLE_SCENE.instantiate()
	playable.configure_test_fixture(FIXTURE, 0, 159000)
	root.add_child(playable)
	await _wait_ready(playable)
	var drawer: Node = playable.get_node_or_null("RingLiveTuningDrawer")
	_expect(drawer != null, "playable contains grave-key tuning drawer")
	if drawer == null: _finish(); return
	var missing := PackedStringArray()
	var editable := PackedStringArray()
	for property in FLOAT_CONTROLS:
		var slider := drawer.find_child(property, true, false) as HSlider
		if slider == null: missing.append(property)
		elif slider.editable: editable.append(property)
	var octaves := drawer.find_child("lowland_erosion_octaves", true, false) as SpinBox
	_expect(missing.is_empty() and octaves != null, "v6 drawer exposes every v7 control (missing: %s)" % ",".join(missing))
	_expect(editable.is_empty() and octaves != null and not octaves.editable, "v6 landform controls are disabled (editable: %s)" % ",".join(editable))
	var upgrade := _button_named(drawer, UPGRADE_TEXT)
	_expect(upgrade != null and upgrade.visible, "v6 drawer offers the landform upgrade")
	var climate := _checkbox_named(drawer, "Climate machines")
	_expect(climate != null and climate.button_pressed, "v6 climate checkbox shows climate machines on")
	if upgrade != null: upgrade.emit_signal("pressed")
	await _wait_ready(playable)
	var upgraded: TerrainGenerationRecipe = playable.get_recipe_snapshot()
	_expect(upgraded.generator_id == TerrainGenerationRecipe.LANDFORM_GENERATOR_ID and upgraded.seed == 90210 and is_equal_approx(upgraded.erosion_strength_m, 83.0) and is_equal_approx(upgraded.land_balance, 0.07), "upgrade preserves existing v6 settings")
	_expect(upgraded.climate_machines == 1, "upgrade from v6 keeps climate machines")
	_expect(FileAccess.get_file_as_bytes(FIXTURE) == bytes_before, "upgrade does not autosave over the v6 fixture")
	editable.clear()
	for property in FLOAT_CONTROLS:
		var slider := drawer.find_child(property, true, false) as HSlider
		if slider == null or not slider.editable: editable.append(property)
	octaves = drawer.find_child("lowland_erosion_octaves", true, false) as SpinBox
	upgrade = _button_named(drawer, UPGRADE_TEXT)
	_expect(editable.is_empty() and octaves != null and octaves.editable and upgrade != null and not upgrade.visible, "v7 enables landform controls and hides the upgrade (not editable: %s)" % ",".join(editable))
	# Edit values, including the climate checkbox on v7.
	var amplitude := drawer.find_child("hill_amplitude_m", true, false) as HSlider
	var gullies := drawer.find_child("lowland_erosion_strength_m", true, false) as HSlider
	if amplitude != null: amplitude.value = 350.0
	if gullies != null: gullies.value = 12.0
	if octaves != null: octaves.value = 1
	climate = _checkbox_named(drawer, "Climate machines")
	if climate != null: climate.button_pressed = false
	await _wait_ready(playable)
	_expect(playable.get_recipe_snapshot().climate_machines == 0, "climate checkbox clears climate_machines on v7")
	if climate != null: climate.button_pressed = true
	await _wait_ready(playable)
	_expect(playable.get_recipe_snapshot().climate_machines == 1, "climate checkbox sets climate_machines on v7")
	_expect(playable.save_recipe(), "explicit playable v7 save succeeds")
	var committed: Dictionary = playable.get_preview_diagnostics()
	_expect(committed.ready and committed.committed_revision == committed.native.get("collision_revision", -1), "upgraded playable commits collision with latest height revision")
	playable.queue_free(); await process_frame
	var reopened: Node = PLAYABLE_SCENE.instantiate(); reopened.configure_test_fixture(FIXTURE, 0, 159000); root.add_child(reopened)
	await _wait_ready(reopened)
	var persisted: TerrainGenerationRecipe = reopened.get_recipe_snapshot()
	_expect(persisted.generator_id == TerrainGenerationRecipe.LANDFORM_GENERATOR_ID and is_equal_approx(persisted.hill_amplitude_m, 350.0) and is_equal_approx(persisted.lowland_erosion_strength_m, 12.0) and persisted.lowland_erosion_octaves == 1 and persisted.climate_machines == 1, "saved v7 tuning reloads")
	var reopened_drawer: Node = reopened.get_node_or_null("RingLiveTuningDrawer")
	var reloaded_slider := reopened_drawer.find_child("hill_amplitude_m", true, false) as HSlider if reopened_drawer != null else null
	_expect(reloaded_slider != null and reloaded_slider.editable and is_equal_approx(reloaded_slider.value, 350.0), "reloaded drawer shows the saved v7 value")
	for frame in 20: await physics_frame
	var player := reopened.get_node_or_null("Player") as CharacterBody3D
	var surface := reopened.get_node_or_null("RingTerrain3DSurface")
	_expect(player != null and player.is_on_floor(), "reopened v7 player settles on committed terrain collision")
	if player != null and surface != null:
		var ray := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 3.0, player.global_position + Vector3.DOWN * 8.0, 1)
		ray.exclude = [player.get_rid()]
		var hit: Dictionary = reopened.get_world_3d().direct_space_state.intersect_ray(ray)
		_expect(not hit.is_empty(), "reopened v7 ray reaches committed terrain collision")
	reopened.queue_free()
	_finish()

func _button_named(node: Node, text: String) -> Button:
	if node is Button and not node is CheckBox and (node as Button).text == text: return node as Button
	for child in node.get_children():
		var found := _button_named(child, text)
		if found != null: return found
	return null

func _checkbox_named(node: Node, prefix: String) -> CheckBox:
	if node is CheckBox and (node as CheckBox).text.begins_with(prefix): return node as CheckBox
	for child in node.get_children():
		var found := _checkbox_named(child, prefix)
		if found != null: return found
	return null

func _wait_ready(playable: Node) -> void:
	for frame in 1800:
		await process_frame
		var diagnostics: Dictionary = playable.get_preview_diagnostics()
		if diagnostics.ready and diagnostics.committed_revision == diagnostics.revision: return
	_failures.append("playable did not commit latest recipe revision")

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition: _failures.append(label)

func _finish() -> void:
	var file := FileAccess.open(REPORT, FileAccess.WRITE)
	if file: file.store_string(JSON.stringify({"checks": _checks, "failures": _failures}, "\t") + "\n"); file.close()
	if _failures.is_empty(): print("TERRAIN_LANDFORM_UI=PASS checks=", _checks); quit(0)
	else: push_error("TERRAIN_LANDFORM_UI=FAIL ", "; ".join(_failures)); quit(1)
