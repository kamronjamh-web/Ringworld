class_name TerrainGenerationPreview
extends Control

const PREVIEW_HANDOFF := preload("res://scripts/Terrain/Generation/terrain_generation_preview_handoff.gd")

const DETAIL_SIDE := 129
const DETAIL_SPACING_M := 2.0
const DEFAULT_AROUND_SPAN_M := 256000.0
const DEFAULT_ACROSS_SPAN_M := 318000.0
const DEFAULT_SPACING_M := 512.0
const DEBOUNCE_SECONDS := 0.35
const ROWS_PER_FRAME := 2

var _recipe: TerrainGenerationRecipe
var _model: TerrainHeightModel
var _around_span_m := DEFAULT_AROUND_SPAN_M
var _across_span_m := DEFAULT_ACROSS_SPAN_M
var _spacing_m := DEFAULT_SPACING_M
var _origin_around := -DEFAULT_AROUND_SPAN_M * .5
var _origin_across := 0.0
var _selected_around := 0.0
var _selected_across := DEFAULT_ACROSS_SPAN_M * .5
var _view_mode := 0
var _revision := 0
var _committed_revision := 0
var _pending_seconds := 0.0
var _phase := "idle"
var _row := 0
var _cols := 0
var _rows := 0
var _overview := PackedFloat32Array()
var _detail := PackedFloat32Array()
var _sea := PackedFloat32Array()
var _alpine := PackedFloat32Array()
var _mask := PackedFloat32Array()
var _slope := PackedFloat32Array()
var _detail_sea := PackedFloat32Array()
var _detail_alpine := PackedFloat32Array()
var _detail_mask := PackedFloat32Array()
var _detail_slope := PackedFloat32Array()
var _landform_view := PackedColorArray()
var _detail_landform_view := PackedColorArray()
var _diagnostics: Dictionary = {}
var _started_usec := 0
var _error := ""
var _preview_ready := false
var _map: TextureRect
var _detail_map: TextureRect
var _status: Label
var _stats: Label
var _location_buttons: Array[Button] = []
var _locations: Dictionary = {}
var _test_path := ""
var _save_blocked := false

func configure_test_fixture(recipe_path: String, around_span_m: float = 4096.0, spacing_m: float = 512.0, across_span_m: float = -1.0) -> void:
	assert(recipe_path.begins_with("res://.godot/terrain-generation-tests/"))
	assert(is_finite(around_span_m) and around_span_m > 0.0 and around_span_m <= DEFAULT_AROUND_SPAN_M)
	assert(is_finite(spacing_m) and spacing_m >= 2.0 and spacing_m <= 4096.0)
	_test_path = recipe_path
	_around_span_m = around_span_m
	_across_span_m = min(DEFAULT_ACROSS_SPAN_M, across_span_m if across_span_m > 0.0 else around_span_m)
	_spacing_m = spacing_m
	_origin_around = -around_span_m * .5
	_origin_across = 0.0
	_selected_across = _across_span_m * .5

func _enter_tree() -> void:
	var handoff := PREVIEW_HANDOFF.consume()
	if not handoff.is_empty():
		_test_path = handoff.recipe_path
	_initialize_recipe()
	if not handoff.is_empty():
		_recipe = handoff.recipe
		var circumference := TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M
		_selected_around = fposmod(float(handoff.around) + circumference * .5, circumference) - circumference * .5
		_selected_across = handoff.across
		_origin_around = clampf(_selected_around - _around_span_m * .5, -circumference * .5, circumference * .5)
		_save_blocked = _save_blocked or handoff.save_blocked
		if _error.is_empty(): _error = handoff.error

func _initialize_recipe() -> void:
	if _recipe != null: return
	var loaded := TerrainGenerationPreviewStore.load_recipe(_recipe_path())
	_recipe = loaded.recipe if loaded.recipe != null else TerrainGenerationRecipe.create_erosion_default()
	_error = loaded.error
	_save_blocked = not _error.is_empty()

func _recipe_path() -> String: return _test_path if not _test_path.is_empty() else TerrainGenerationPreviewStore.RECIPE_PATH

func _ready() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_build_ui()
	_initialize_recipe()
	regenerate()


func _build_ui() -> void:
	var root := VBoxContainer.new(); root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT); root.offset_left = 20; root.offset_right = -20; root.offset_top = 20; root.offset_bottom = -20; add_child(root)
	var title := Label.new(); title.text = "TERRAIN REFERENCE GENERATION"; title.add_theme_font_size_override("font_size", 22); root.add_child(title)
	var help := Label.new(); help.text = "Regional 512 m diagnostic overview. Click the map to select a 129 x 129 sample, 2 m detail patch. Open the grave-key Terrain3D tab to tune the isolated recipe."; help.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART; root.add_child(help)
	var modes := HBoxContainer.new(); root.add_child(modes)
	var mode_group := ButtonGroup.new()
	for label in ["Height (-8km..8km)", "Zone weights", "Detail mask (0..1)", "Slope (0..60 deg)", "Landforms (R, Q, gate)"]:
		var button := Button.new(); button.text = label; button.toggle_mode = true; button.button_group = mode_group; button.button_pressed = label.begins_with("Height"); button.pressed.connect(func(): set_view_mode(modes.get_children().find(button))); modes.add_child(button)
	var pan := HBoxContainer.new(); root.add_child(pan)
	pan.add_child(Label.new()); pan.get_child(0).text = "Around origin (m)"
	var origin_spin := SpinBox.new(); origin_spin.name = "AroundOrigin"; origin_spin.min_value = -15707968.0; origin_spin.max_value = 15707968.0; origin_spin.step = DETAIL_SPACING_M; origin_spin.value = _origin_around; origin_spin.value_changed.connect(func(value): _origin_around = value; regenerate()); pan.add_child(origin_spin)
	var maps := HBoxContainer.new(); maps.size_flags_vertical = Control.SIZE_EXPAND_FILL; root.add_child(maps)
	var left := VBoxContainer.new(); left.size_flags_horizontal = Control.SIZE_EXPAND_FILL; maps.add_child(left)
	var overview_label := Label.new(); overview_label.text = "Regional overview"; left.add_child(overview_label)
	_map = TextureRect.new(); _map.expand_mode = TextureRect.EXPAND_IGNORE_SIZE; _map.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED; _map.custom_minimum_size = Vector2(680, 420); _map.size_flags_vertical = Control.SIZE_EXPAND_FILL; _map.gui_input.connect(_on_map_input); left.add_child(_map)
	var right := VBoxContainer.new(); right.custom_minimum_size = Vector2(300, 0); maps.add_child(right)
	var detail_label := Label.new(); detail_label.text = "Selected 2 m patch"; right.add_child(detail_label)
	_detail_map = TextureRect.new(); _detail_map.expand_mode = TextureRect.EXPAND_IGNORE_SIZE; _detail_map.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED; _detail_map.custom_minimum_size = Vector2(280, 280); right.add_child(_detail_map)
	var locations := VBoxContainer.new(); locations.name = "RepresentativeLocations"; right.add_child(locations)
	for label in ["Basin", "Coast", "Valley", "Alpine"]:
		var button := Button.new(); button.text = "Select " + label; button.disabled = true; button.pressed.connect(func(): _select_representative(label.to_lower())); locations.add_child(button); _location_buttons.append(button)
	var explore := Button.new(); explore.name = "ExploreSelectedPatch"; explore.text = "Explore selected patch"; explore.pressed.connect(open_playable_selected_patch); right.add_child(explore)
	_status = Label.new(); _status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART; root.add_child(_status)
	_stats = Label.new(); _stats.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART; root.add_child(_stats)

func apply_recipe_setting(name: String, value: Variant) -> void:
	_initialize_recipe()
	if _recipe == null: return
	_recipe.set(name, value)
	regenerate()

func get_recipe_snapshot() -> Resource:
	_initialize_recipe()
	return _recipe.snapshot()
func regenerate() -> void:
	_revision += 1; _preview_ready = false; _pending_seconds = DEBOUNCE_SECONDS; _phase = "debouncing"; _error = "" if _error.is_empty() else _error; _set_status()
func set_view_mode(index: int) -> void:
	_view_mode = clampi(index, 0, 4)
	if _preview_ready: _rebuild_textures()
func select_location(around: float, across: float) -> void:
	if not _preview_ready: return
	_selected_around = around; _selected_across = clampf(across, _origin_across, _origin_across + (_rows - 1) * _spacing_m); _begin_detail()

func _process(delta: float) -> void:
	if _phase == "debouncing":
		_pending_seconds -= delta
		if _pending_seconds <= 0: _begin_overview()
	elif _phase == "overview" or _phase == "detail": _sample_rows()

func _begin_overview() -> void:
	var errors := _recipe.validation_errors()
	if not errors.is_empty(): _error = ", ".join(errors); _phase = "idle"; _set_status(); return
	_recipe = _recipe.snapshot(); _model = TerrainHeightModel.new(_recipe); _cols = int(floor(_around_span_m / _spacing_m)) + 1; _rows = int(floor(_across_span_m / _spacing_m)) + 1; _overview.resize(_cols * _rows); _sea.resize(_overview.size()); _alpine.resize(_overview.size()); _mask.resize(_overview.size()); _slope.resize(_overview.size()); _landform_view.resize(_overview.size()); _row = 0; _locations.clear(); _phase = "overview"; _started_usec = Time.get_ticks_usec(); _diagnostics = {"min": INF, "max": -INF, "clamp": 0, "ocean": 0, "lowland": 0, "alpine": 0, "histogram": PackedInt32Array(), "slopes": PackedFloat32Array()}; _diagnostics.histogram.resize(16); _set_status()

func _begin_detail() -> void:
	if _model == null: return
	_preview_ready = false; _detail.resize(DETAIL_SIDE * DETAIL_SIDE); _detail_sea.resize(_detail.size()); _detail_alpine.resize(_detail.size()); _detail_mask.resize(_detail.size()); _detail_slope.resize(_detail.size()); _detail_landform_view.resize(_detail.size()); _row = 0; _phase = "detail"; _set_status()

func _sample_rows() -> void:
	var target_rows := _rows if _phase == "overview" else DETAIL_SIDE
	var columns := _cols if _phase == "overview" else DETAIL_SIDE
	for unused in ROWS_PER_FRAME:
		if _row >= target_rows: _finish_phase(); return
		for x in columns:
			var around := _origin_around + x * _spacing_m if _phase == "overview" else _selected_around + (x - (DETAIL_SIDE - 1) * .5) * DETAIL_SPACING_M
			var across := _origin_across + _row * _spacing_m if _phase == "overview" else _selected_across + (_row - (DETAIL_SIDE - 1) * .5) * DETAIL_SPACING_M
			var d := _model.sample_diagnostics(around, across)
			var height: float = d.height
			var index := _row * columns + x
			if _phase == "overview": _overview[index] = height; _sea[index] = d.sea_weight; _alpine[index] = d.alpine_weight; _mask[index] = d.detail_mask; _landform_view[index] = Color(d.ruggedness, d.plateau_weight, d.landform_gate); _accumulate(d, height)
			else: _detail[index] = height; _detail_sea[index] = d.sea_weight; _detail_alpine[index] = d.alpine_weight; _detail_mask[index] = d.detail_mask; _detail_landform_view[index] = Color(d.ruggedness, d.plateau_weight, d.landform_gate)
		_row += 1
	_set_status()

func _accumulate(d: Dictionary, height: float) -> void:
	if is_finite(height): _diagnostics.min = minf(_diagnostics.min, height); _diagnostics.max = maxf(_diagnostics.max, height)
	if d.clamped: _diagnostics.clamp += 1
	if height < 0.0: _diagnostics.ocean += 1
	elif d.alpine_weight > .5: _diagnostics.alpine += 1
	else: _diagnostics.lowland += 1
	var bin := clampi(int(floor((height + 8000.0) / 1000.0)), 0, 15); _diagnostics.histogram[bin] += 1

func _finish_phase() -> void:
	if _phase == "overview":
		_compute_slopes_and_locations(); _rebuild_textures(); _begin_detail(); return
	_compute_detail_slopes(); _rebuild_detail_texture(); _phase = "idle"; _preview_ready = true; _committed_revision = _revision; _diagnostics.time_ms = (Time.get_ticks_usec() - _started_usec) / 1000.0; _set_status(); _set_stats()

func _compute_detail_slopes() -> void:
	for y in DETAIL_SIDE:
		for x in DETAIL_SIDE:
			var left: int = y * DETAIL_SIDE + max(0, x - 1)
			var right: int = y * DETAIL_SIDE + min(DETAIL_SIDE - 1, x + 1)
			var down: int = max(0, y - 1) * DETAIL_SIDE + x
			var up: int = min(DETAIL_SIDE - 1, y + 1) * DETAIL_SIDE + x
			var divisor_x: float = maxf(DETAIL_SPACING_M, float(min(DETAIL_SIDE - 1, x + 1) - max(0, x - 1)) * DETAIL_SPACING_M)
			var divisor_y: float = maxf(DETAIL_SPACING_M, float(min(DETAIL_SIDE - 1, y + 1) - max(0, y - 1)) * DETAIL_SPACING_M)
			var dx: float = (_detail[right] - _detail[left]) / divisor_x
			var dy: float = (_detail[up] - _detail[down]) / divisor_y
			_detail_slope[y * DETAIL_SIDE + x] = rad_to_deg(atan(sqrt(dx * dx + dy * dy)))

func _compute_slopes_and_locations() -> void:
	var slopes: Array[float] = []
	var basin := INF; var coast := INF; var valley := -INF; var alpine := -INF
	for y in range(_rows):
		for x in range(_cols):
			var i := y * _cols + x; var h: float = _overview[i]
			var dx: float = (_overview[y * _cols + min(x + 1, _cols - 1)] - _overview[y * _cols + max(x - 1, 0)]) / (max(1, min(x + 1, _cols - 1) - max(x - 1, 0)) * _spacing_m)
			var dy: float = (_overview[min(y + 1, _rows - 1) * _cols + x] - _overview[max(y - 1, 0) * _cols + x]) / (max(1, min(y + 1, _rows - 1) - max(y - 1, 0)) * _spacing_m)
			var angle: float = rad_to_deg(atan(sqrt(dx * dx + dy * dy))); _slope[i] = angle; slopes.append(angle)
			var point := {"around": _origin_around + x * _spacing_m, "across": _origin_across + y * _spacing_m}
			if h < -1000.0 and h < basin: basin = h; _locations.basin = point
			if absf(h) <= 100.0 and absf(h) < coast: coast = absf(h); _locations.coast = point
			if h > 1500.0 and angle < 3.0 and _mask[i] < .2 and h > valley: valley = h; _locations.valley = point
			if h > 1500.0 and _alpine[i] > .5 and h > alpine: alpine = h; _locations.alpine = point
	slopes.sort(); _diagnostics.slopes = PackedFloat32Array(slopes)
	for button in _location_buttons: button.disabled = not _locations.has(button.text.trim_prefix("Select ").to_lower())

func _mode_value(d: Dictionary, height: float, around: float, across: float, spacing: float) -> float:
	match _view_mode:
		1: return d.alpine_weight - d.sea_weight
		2: return d.detail_mask
		3:
			var a := _model.calculate_1to1_height(around + spacing, across) - _model.calculate_1to1_height(around - spacing, across)
			var b := _model.calculate_1to1_height(around, across + spacing) - _model.calculate_1to1_height(around, across - spacing)
			return rad_to_deg(atan(sqrt(a * a + b * b) / (2.0 * spacing)))
	return height

func _rebuild_textures() -> void:
	_map.texture = ImageTexture.create_from_image(_color_image(_overview, _cols, _rows))
	if not _detail.is_empty(): _rebuild_detail_texture()

func _rebuild_detail_texture() -> void:
	_detail_map.texture = ImageTexture.create_from_image(_color_image(_detail, DETAIL_SIDE, DETAIL_SIDE, true))

func _color_image(values: PackedFloat32Array, width: int, height: int, is_detail: bool = false) -> Image:
	var image := Image.create(width, height, false, Image.FORMAT_RGBA8)
	for y in height:
		for x in width:
			var i := y * width + x; var t := clampf((values[i] + 8000.0) / 16000.0, 0, 1); var color := Color(t, t, t)
			if _view_mode == 0: color = Color(lerpf(.02, .95, t), lerpf(.08, .85, t), lerpf(.22, .65, t)) if values[i] < 0 else Color(lerpf(.08, .92, t), lerpf(.22, .92, t), lerpf(.05, .95, t))
			elif _view_mode == 1:
				var sea := (_detail_sea if is_detail else _sea)[i]; var alpine := (_detail_alpine if is_detail else _alpine)[i]; color = Color(alpine, 1.0 - sea - alpine, sea)
			elif _view_mode == 2: var mask := (_detail_mask if is_detail else _mask)[i]; color = Color(mask, mask, mask)
			elif _view_mode == 4: color = (_detail_landform_view if is_detail else _landform_view)[i]
			elif _view_mode == 3: var slope := (_detail_slope if is_detail else _slope)[i]; color = Color(clampf(slope / 60.0, 0, 1), clampf(slope / 60.0, 0, 1), clampf(slope / 60.0, 0, 1))
			image.set_pixel(x, y, color)
	return image

func _on_map_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT and _map.texture != null:
		var size := _map.size; var ratio := minf(size.x / _cols, size.y / _rows); var drawn := Vector2(_cols, _rows) * ratio; var offset := (size - drawn) * .5; var p: Vector2 = (event.position - offset) / ratio
		if p.x >= 0 and p.y >= 0 and p.x < _cols and p.y < _rows: select_location(_origin_around + p.x * _spacing_m, _origin_across + p.y * _spacing_m)

func _select_representative(kind: String) -> void:
	if _locations.has(kind):
		var point: Dictionary = _locations[kind]
		select_location(float(point.around), float(point.across))
func open_playable_selected_patch() -> void:
	if not _preview_ready:
		return
	PREVIEW_HANDOFF.stage(_recipe, _selected_around, _selected_across, _recipe_path(), _save_blocked, _error)
	get_tree().change_scene_to_file("res://scenes/TerrainGenerationPlayable.tscn")
func _set_status() -> void:
	if _status == null: return
	var samples := _row * (_cols if _phase == "overview" else DETAIL_SIDE)
	_status.text = _error if not _error.is_empty() else "Revision %d: %s (%d samples), selected around %.1f m across %.1f m" % [_revision, _phase, samples, _selected_around, _selected_across]
func _set_stats() -> void:
	var slopes: PackedFloat32Array = _diagnostics.slopes; var count: int = _overview.size(); var p50: float = slopes[int(slopes.size() * .5)] if not slopes.is_empty() else 0.0; var p95: float = slopes[int(slopes.size() * .95)] if not slopes.is_empty() else 0.0; var maximum: float = slopes[-1] if not slopes.is_empty() else 0.0
	_stats.text = "Samples: %d Height: %.1f to %.1f m Ocean/lowland/alpine: %.1f%% / %.1f%% / %.1f%%\nSlope p50/p95/max: %.2f deg / %.2f deg / %.2f deg Clamp count: %d Time: %.1f ms\nHistogram (-8 km to +8 km): %s" % [count, _diagnostics.min, _diagnostics.max, 100.0 * _diagnostics.ocean / count, 100.0 * _diagnostics.lowland / count, 100.0 * _diagnostics.alpine / count, p50, p95, maximum, _diagnostics.clamp, _diagnostics.time_ms, _diagnostics.histogram]

func get_preview_diagnostics() -> Dictionary:
	var slopes: PackedFloat32Array = _diagnostics.get("slopes", PackedFloat32Array())
	return {"ready": _preview_ready, "revision": _revision, "committed_revision": _committed_revision, "sample_count": _overview.size(), "detail_sample_count": _detail.size(), "min_height": _diagnostics.get("min", 0.0), "max_height": _diagnostics.get("max", 0.0), "clamp_count": _diagnostics.get("clamp", 0), "generation_time_ms": _diagnostics.get("time_ms", 0.0), "histogram": _diagnostics.get("histogram", PackedInt32Array()), "ocean_count": _diagnostics.get("ocean", 0), "lowland_count": _diagnostics.get("lowland", 0), "alpine_count": _diagnostics.get("alpine", 0), "slope_p50_degrees": slopes[int(slopes.size() * .5)] if not slopes.is_empty() else 0.0, "slope_p95_degrees": slopes[int(slopes.size() * .95)] if not slopes.is_empty() else 0.0, "slope_max_degrees": slopes[-1] if not slopes.is_empty() else 0.0, "selected_around": _selected_around, "selected_across": _selected_across, "view_mode": _view_mode, "error": _error, "save_blocked": _save_blocked}
func save_recipe() -> bool:
	if _save_blocked: _error = "Saved recipe is protected. Use Reset recipe before saving."; _set_status(); return false
	var error := TerrainGenerationPreviewStore.save_recipe(_recipe, _recipe_path()); _error = error; _set_status(); return error.is_empty()

func upgrade_to_enhanced_erosion() -> void:
	_initialize_recipe()
	if _recipe == null or _recipe.generator_id != TerrainGenerationRecipe.DETAIL_GENERATOR_ID:
		return
	var upgraded := _recipe.snapshot()
	upgraded.generator_id = TerrainGenerationRecipe.EROSION_GENERATOR_ID
	upgraded.erosion_ridge_preservation = 0.75
	upgraded.gully_normalization = 0.6
	_recipe = upgraded.snapshot()
	_error = ""
	_save_blocked = false
	regenerate()
## Moves a v4/v5/v6 preview recipe to the v7 hill-country landforms, keeping every other setting. A v6 recipe keeps its climate machines.
func upgrade_to_landforms() -> void:
	_initialize_recipe()
	if _recipe == null:
		return
	var from_id := _recipe.generator_id
	if from_id != TerrainGenerationRecipe.EROSION_GENERATOR_ID and from_id != TerrainGenerationRecipe.SITES_GENERATOR_ID and from_id != TerrainGenerationRecipe.CLIMATE_GENERATOR_ID:
		return
	var upgraded := _recipe.snapshot()
	upgraded.generator_id = TerrainGenerationRecipe.LANDFORM_GENERATOR_ID
	upgraded.climate_machines = int(from_id == TerrainGenerationRecipe.CLIMATE_GENERATOR_ID)
	_recipe = upgraded.snapshot()
	_error = ""
	_save_blocked = false
	regenerate()
func reset_recipe() -> void:
	_recipe = TerrainGenerationRecipe.create_landform_default(); _error = ""; _save_blocked = false; regenerate(); save_recipe()

## Preview toggle for the climate-machine forecourts (off by default). v5/v6 switch recipe id; v7 sets its climate_machines field. Other recipes are left untouched.
func set_climate_machines(enabled: bool) -> void:
	_initialize_recipe()
	if _recipe == null:
		return
	if _recipe.generator_id == TerrainGenerationRecipe.LANDFORM_GENERATOR_ID:
		if _recipe.climate_machines == int(enabled):
			return
		var landform := _recipe.snapshot()
		landform.climate_machines = int(enabled)
		_recipe = landform.snapshot()
		_error = ""
		_save_blocked = false
		regenerate()
		return
	var target := TerrainGenerationRecipe.CLIMATE_GENERATOR_ID if enabled else TerrainGenerationRecipe.SITES_GENERATOR_ID
	if _recipe.generator_id == target or (not enabled and _recipe.generator_id == TerrainGenerationRecipe.EROSION_GENERATOR_ID):
		return
	if _recipe.generator_id != TerrainGenerationRecipe.EROSION_GENERATOR_ID and _recipe.generator_id != TerrainGenerationRecipe.SITES_GENERATOR_ID and _recipe.generator_id != TerrainGenerationRecipe.CLIMATE_GENERATOR_ID:
		return
	var changed := _recipe.snapshot()
	changed.generator_id = target
	_recipe = changed.snapshot()
	_error = ""
	_save_blocked = false
	regenerate()
