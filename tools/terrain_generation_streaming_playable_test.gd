extends SceneTree

const PLAYABLE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const FIXTURE := "res://.godot/terrain-generation-tests/m3-streaming/recipe.tres"
const PREFS := "res://.godot/terrain-generation-tests/m3-streaming/playable_preferences.tres"
const REPORT := "res://.godot/terrain-generation-tests/m3-streaming/report.json"
const USER_PREFS := ["user://gameplay_surface_terrain_defaults.tres", "user://terrain_generation_preview/playable_preferences.tres"]
const MINIMUM_CHECKS := 40
var _checks := 0
var _failures := PackedStringArray()
var _done := false
var _completed := false

func _init() -> void:
	call_deferred("_run")
	create_timer(90.0).timeout.connect(func():
		if not _done:
			_failures.append("wall-clock watchdog expired")
			_finish())

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE.get_base_dir()))
	TerrainGenerationPreviewStore.save_recipe(TerrainGenerationRecipe.new(), FIXTURE)
	_expect(not FileAccess.file_exists(PREFS) or DirAccess.remove_absolute(PREFS) == OK, "isolated fixture preferences reset for a fresh persistence test")
	var user_existed := []
	var user_bytes := []
	for path in USER_PREFS:
		user_existed.append(FileAccess.file_exists(path))
		user_bytes.append(FileAccess.get_file_as_bytes(path) if FileAccess.file_exists(path) else PackedByteArray())
	var playable := PLAYABLE.instantiate()
	playable.configure_test_fixture(FIXTURE, 0, 159000)
	root.add_child(playable)
	await _wait_ready(playable)
	var d: Dictionary = playable.get_preview_diagnostics()
	_expect(d.ready, "initial window becomes active")
	_expect(playable.get("_worker_count") == 2 and playable.get("_prefetch_m") == 64.0 and playable.get("_cache_mib") == 8, "fresh fixture uses streaming defaults")
	_expect(is_equal_approx(d.upload_budget_ms, 1.0) and is_equal_approx(d.collision_budget_ms, .75) and d.collider_tile_cells == 32, "fresh fixture uses publication defaults")
	var surface: Node = playable.get_node("RingTerrain3DSurface")
	var player := playable.get_node("Player") as CharacterBody3D
	var old_revision: int = d.committed_revision
	var old_height: float = surface.call("SampleCommittedHeight", 0.0, 159000.0)
	_expect(is_finite(old_height), "active query is finite")
	_expect(_ground_ray(playable, player), "initial curved collision ray hits")
	playable.apply_recipe_setting("land_balance", .02)
	playable.apply_recipe_setting("land_balance", -.04)
	playable.apply_recipe_setting("land_balance", .06)
	await physics_frame; await physics_frame
	d = playable.get_preview_diagnostics()
	_expect(d.committed_revision == old_revision and d.ready, "recipe debounce retains old active revision")
	_expect(is_finite(surface.call("SampleCommittedHeight", 0.0, 159000.0)), "old query remains usable during recipe stream")
	_expect(_ground_ray(playable, player), "old collision remains usable during recipe stream")
	await _wait_latest(playable)
	d = playable.get_preview_diagnostics()
	_expect(d.committed_revision > old_revision, "latest recipe request wins")
	_expect(d.native.query_revision == d.committed_revision and d.native.collision_revision == d.committed_revision, "active render query collision revisions agree")
	_expect(not playable.request_teleport(20.0, 0.0), "unreachable across teleport is rejected")
	_expect(playable.request_teleport(31415800.0, 159000.0), "wrapped teleport accepted")
	_expect(playable.request_teleport(512.0, 159000.0), "superseding teleport accepted")
	await _wait_latest(playable)
	d = playable.get_preview_diagnostics()
	_expect(absf(d.canonical_around - 512.0) < 2.1, "latest teleport alone publishes")
	_expect(_ground_ray(playable, player), "teleport destination is supported")
	# Force two real canonical edge crossings while flying. Reset the controller's
	# motion sample after each direct placement so fixture positioning is not
	# interpreted as player velocity.
	playable.set("_flying", true)
	for step in 2:
		d = playable.get_preview_diagnostics()
		var previous_anchor: int = d.anchor_around
		var target_around := previous_anchor + 128.0
		var target_height: float = surface.call("SampleCommittedHeight", target_around, d.anchor_across)
		player.global_position = surface.call("MapCanonicalPosition", target_around, d.anchor_across, target_height + 3.0)
		playable.set("_have_motion_sample", false)
		await physics_frame; await process_frame
		await _wait_anchor(playable, previous_anchor + 256)
		d = playable.get_preview_diagnostics()
		_expect(d.anchor_around == previous_anchor + 256, "automatic recenter advances one 256m region")
		_expect(d.native.query_revision == d.committed_revision and d.native.collision_revision == d.committed_revision, "recenter keeps active query and collision aligned")
		_expect(_ground_ray(playable, player), "recenter handoff retains curved support")
		var settled_revision: int = d.revision
		await create_timer(.15).timeout
		_expect(playable.get_preview_diagnostics().revision == settled_revision, "stationary overlap does not request the retired patch")
	_expect(playable.get_preview_diagnostics().stream.get("cache_hits", 0) >= 6, "axial recenters reuse cached overlap tiles")
	# Drive two canonical motion samples deterministically, then let the normal
	# process loop prepare ahead while physics is held outside the new coverage.
	playable.set_physics_process(false)
	d = playable.get_preview_diagnostics()
	var reversal_anchor: int = d.anchor_around
	_place(surface, player, reversal_anchor + 76.0, d.anchor_across)
	playable.set("_have_motion_sample", false)
	playable.call("_measure_canonical_motion", 1.0 / 60.0)
	_place(surface, player, reversal_anchor + 77.0, d.anchor_across)
	playable.call("_measure_canonical_motion", 1.0 / 60.0)
	playable.call("_maybe_stream")
	d = playable.get_preview_diagnostics()
	_expect(absf(d.actual_velocity.x - 60.0) < .1, "prediction measures canonical displacement rather than requested speed")
	_expect(d.lookahead >= 105.9, "prefetch includes conservative readiness and actual velocity")
	_expect(d.updating and d.request_kind == "movement", "prediction starts the forward window before recenter threshold")
	var cancelled_revision: int = d.revision
	await _wait_staged(playable)
	var staged: Dictionary = playable.get("_staged")
	_expect(not staged.is_empty(), "forward window reaches staged readiness")
	_expect(not staged.get("covered_at_ready", true), "prefetched window waits outside usable destination coverage")
	await create_timer(.2).timeout
	_expect(playable.get_preview_diagnostics().committed_revision < cancelled_revision, "waiting ahead retains active revision")
	_expect(_ground_ray(playable, player), "old collider supports player while replacement waits")
	_place(surface, player, reversal_anchor + 76.0, d.anchor_across)
	playable.call("_measure_canonical_motion", 1.0 / 60.0)
	playable.call("_maybe_stream")
	_expect(playable.get("_staged").is_empty(), "direction reversal discards the forward staged window")
	_expect(not playable.get_preview_diagnostics().updating, "reversal cancels unnecessary forward work")
	_place(surface, player, reversal_anchor - 129.0, d.anchor_across)
	playable.set("_have_motion_sample", false)
	playable.set("_measured_velocity", Vector2.ZERO)
	playable.set_physics_process(true)
	await _wait_anchor(playable, reversal_anchor - 256)
	d = playable.get_preview_diagnostics()
	_expect(d.committed_revision > cancelled_revision, "reverse travel publishes a new frame revision")
	_expect(_ground_ray(playable, player), "reverse handoff retains curved support")
	playable.set("_flying", false)
	var edge_height: float = surface.call("SampleCommittedHeight", d.anchor_around + 190.0, d.anchor_across)
	player.global_position = surface.call("MapCanonicalPosition", d.anchor_around + 190.0, d.anchor_across, edge_height + 2.0)
	await physics_frame; await process_frame; await physics_frame; await process_frame
	d = playable.get_preview_diagnostics()
	_expect(d.edge_waits >= 1, "coverage edge clamp records an incident")
	_expect(absf(d.canonical_around - d.anchor_around) <= 176.1, "clamp uses committed coverage")
	playable.apply_preview_edits({Vector2i(1024, 159000): 123.5})
	_expect(playable.request_teleport(1024.0, 159000.0), "edit/rebase race teleport accepted")
	await _wait_latest(playable)
	_expect(absf(float(surface.call("SampleCommittedHeight", 1024.0, 159000.0)) - 123.5) < .01, "latest absolute edit override survives rebase")
	old_revision = playable.get_preview_diagnostics().committed_revision
	playable.apply_recipe_setting("circumference_m", NAN)
	await create_timer(.5).timeout
	d = playable.get_preview_diagnostics()
	_expect(d.ready and not d.updating and not d.error.is_empty(), "invalid recipe terminates preparation and reports an error")
	_expect(d.committed_revision == old_revision and _ground_ray(playable, player), "invalid recipe preserves active terrain and collision")
	playable.apply_recipe_setting("circumference_m", TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)
	await _wait_latest(playable)
	_expect(playable.get_preview_diagnostics().error.is_empty(), "valid recipe change recovers after failure")
	var drawer: CanvasLayer = playable.get_node("RingLiveTuningDrawer")
	_key(KEY_QUOTELEFT); await process_frame
	var workers := drawer.find_child("Generationworkers", true, false) as HSlider
	var prefetch := drawer.find_child("Prefetchdistance(m)", true, false) as HSlider
	var cache := drawer.find_child("Generationcache(MiB)", true, false) as HSlider
	var upload := drawer.find_child("Uploadbudget(ms)", true, false) as HSlider
	var collision := drawer.find_child("Collisionbudget(ms)", true, false) as HSlider
	var cells := drawer.find_child("Collidertilecells", true, false) as HSlider
	var urgent := drawer.find_child("Urgentpublicationbudget(ms)", true, false) as HSlider
	_expect(urgent != null and is_equal_approx(urgent.value, 6.0) and is_equal_approx(urgent.max_value, 16.0), "urgent publication budget slider exists with the default")
	_expect(workers != null and prefetch != null and cache != null, "three streaming controls exist")
	_expect(upload != null and collision != null and cells != null, "publication budgets and collider tile control exist")
	if upload != null and collision != null and cells != null:
		var recipe_epoch: int = playable.get_preview_diagnostics().recipe_epoch
		upload.value = 2.0; collision.value = 1.5; cells.value = 16
		await _wait_latest(playable)
		d = playable.get_preview_diagnostics()
		_expect(d.recipe_epoch == recipe_epoch and d.cache_hits == 9, "tile size change reuses canonical data without changing terrain recipe")
		_expect(d.native.preparation.tile_cells == 16 and d.native.preparation.tiles_total == 144 and _ground_ray(playable, player), "tile size change safely publishes all 144 collision tiles")
		_expect(is_equal_approx(d.upload_budget_ms, 2.0) and is_equal_approx(d.collision_budget_ms, 1.5), "live publication budgets take effect")
	if workers != null and prefetch != null and cache != null:
		workers.value = 4; prefetch.value = 160; cache.value = 64
		if urgent != null: urgent.value = 10.0
		await create_timer(.7).timeout
		_expect(FileAccess.file_exists(PREFS), "isolated streaming preferences persist")
		var saved = ResourceLoader.load(PREFS, "", ResourceLoader.CACHE_MODE_IGNORE)
		_expect(saved != null and is_equal_approx(float(saved.get("GenerationUrgentBudgetMs")), 10.0), "urgent publication budget persists")
	_key(KEY_QUOTELEFT)
	var held_stream = playable.get("_stream")
	playable.request_teleport(2048.0, 159000.0)
	await create_timer(.02).timeout
	_expect(held_stream.get_diagnostics().inflight > 0, "teardown begins with scheduler work pending")
	playable.queue_free(); await process_frame
	_expect(held_stream.get_diagnostics().tasks_submitted == held_stream.get_diagnostics().tasks_joined, "teardown joins pending scheduler tasks")
	for index in USER_PREFS.size():
		_expect(FileAccess.file_exists(USER_PREFS[index]) == user_existed[index], "fixture preserves real preference existence")
		if user_existed[index]: _expect(FileAccess.get_file_as_bytes(USER_PREFS[index]) == user_bytes[index], "fixture preserves real preference bytes")
	var reload := PLAYABLE.instantiate()
	reload.configure_test_fixture(FIXTURE, 0, 159000); root.add_child(reload)
	await _wait_ready(reload)
	_expect(reload.get("_worker_count") == 4 and is_equal_approx(reload.get("_prefetch_m"), 160.0) and reload.get("_cache_mib") == 64, "streaming preferences reload from fixture")
	var reloaded = ResourceLoader.load(PREFS, "", ResourceLoader.CACHE_MODE_IGNORE)
	_expect(reloaded != null and is_equal_approx(float(reloaded.get("GenerationUrgentBudgetMs")), 10.0), "urgent publication budget reloads from fixture")
	d = reload.get_preview_diagnostics()
	_expect(is_equal_approx(d.upload_budget_ms, 2.0) and is_equal_approx(d.collision_budget_ms, 1.5) and d.collider_tile_cells == 16, "publication preferences reload from fixture")
	reload.queue_free(); await process_frame
	_completed=true
	_finish()

func _wait_ready(playable: Node) -> void:
	var deadline := Time.get_ticks_msec() + 30000
	while Time.get_ticks_msec() < deadline:
		await create_timer(.005).timeout
		if playable.get_preview_diagnostics().ready: return
	_failures.append("initial readiness timed out")

func _wait_latest(playable: Node) -> void:
	var deadline := Time.get_ticks_msec() + 30000
	while Time.get_ticks_msec() < deadline:
		await create_timer(.005).timeout
		var d: Dictionary = playable.get_preview_diagnostics()
		if d.ready and d.committed_revision == d.revision: return
	_failures.append("latest request timed out")

func _wait_anchor(playable: Node, expected: int) -> void:
	var deadline := Time.get_ticks_msec() + 30000
	while Time.get_ticks_msec() < deadline:
		await create_timer(.005).timeout
		var d: Dictionary = playable.get_preview_diagnostics()
		if d.ready and d.anchor_around == expected and d.committed_revision == d.revision: return
	_failures.append("recenter anchor timed out")

func _wait_staged(playable: Node) -> void:
	var deadline := Time.get_ticks_msec() + 30000
	while Time.get_ticks_msec() < deadline:
		await create_timer(.005).timeout
		if not playable.get("_staged").is_empty(): return
	_failures.append("prefetch staging timed out")

func _place(surface: Node, player: CharacterBody3D, around: float, across: float) -> void:
	var height: float = surface.call("SampleCommittedHeight", around, across)
	player.global_position = surface.call("MapCanonicalPosition", around, across, height + 3.0)

func _ground_ray(playable: Node, player: CharacterBody3D) -> bool:
	var ray := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 3.0, player.global_position + Vector3.DOWN * 8.0, 1)
	ray.exclude = [player.get_rid()]
	return not playable.get_world_3d().direct_space_state.intersect_ray(ray).is_empty()

func _key(code: Key) -> void:
	var e := InputEventKey.new(); e.keycode=code; e.pressed=true; root.push_input(e,true)
func _expect(ok: bool, label: String) -> void:
	_checks += 1
	if not ok: _failures.append(label)
func _finish() -> void:
	if _done: return
	_done=true
	if not _completed: _failures.append("test did not complete all stages")
	if _checks<MINIMUM_CHECKS: _failures.append("too few checks")
	var output:=FileAccess.open(REPORT,FileAccess.WRITE)
	if output!=null: output.store_string(JSON.stringify({"checks":_checks,"completed":_completed,"failures":_failures},"  "));output.close()
	else: _failures.append("could not write report")
	if _failures.is_empty(): print("TERRAIN_GENERATION_STREAMING_PLAYABLE=PASS ",JSON.stringify({"checks":_checks}));quit(0)
	else: push_error("TERRAIN_GENERATION_STREAMING_PLAYABLE=FAIL ","; ".join(_failures));quit(1)
