extends SceneTree

# Run: Godot --headless --path . --script res://tools/world_generation_publish_test.gd
# Drop --headless to check the shader global through a rendered pixel.
const TEST := preload("res://tools/WorldGenerationPublishTest.cs")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const SESSION := preload("res://scripts/world_session.gd")
const RUNTIME_BACKEND := "surface_runtime_generation_v1"
const V9_ID := "ringworld_surface_world_v9"

var _failure := "publication test stopped before completing its checks"

func _init() -> void:
	_run.call_deferred()

func _run() -> void:
	var runner := TEST.new()
	var previous := root.get_node_or_null("WorldSession")
	if previous != null:
		previous.name = "WorldSessionOriginalGenerationPublish"
	var session := SESSION.new()
	session.name = "WorldSession"
	session.worlds_root = "res://.godot/world-generation-publish-%d" % Time.get_ticks_usec()
	root.add_child(session)
	var passed: bool = await _test(runner, session)
	if session.slot_exists(1) and not session.delete_slot(1) and passed:
		passed = _reject("could not remove the isolated saved slot")
	var fixture := ProjectSettings.globalize_path(session.worlds_root)
	if DirAccess.dir_exists_absolute(fixture) and DirAccess.remove_absolute(fixture) != OK and passed:
		passed = _reject("could not remove the isolated fixture directory")
	session.free()
	if previous != null:
		previous.name = "WorldSession"
	if not passed:
		push_error("WORLD_GENERATION_PUBLISH=FAIL " + _failure)
		print("WORLD_GENERATION_PUBLISH_WRAPPER=FAIL")
		quit(1)
		return
	print("WORLD_GENERATION_PUBLISH=PASS")
	print("WORLD_GENERATION_PUBLISH_WRAPPER=PASS")
	quit(0)

func _test(runner: RefCounted, session: Node) -> bool:
	for method in ["CheckState", "RecipeJson", "CheckRuntimeSampler", "CheckContracts", "PublishShaderProbe"]:
		if not runner.has_method(method):
			return _reject("publication test bridge has no " + method + " method")
	var error := str(runner.call("CheckState", "default", 1, 0))
	if not error.is_empty(): return _reject(error)
	if not bool(session.call("create_world", 1, "Generation publish fixture")):
		return _reject("could not create the isolated saved slot")

	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", RUNTIME_BACKEND)
	var setup := LAUNCHER.instantiate()
	root.add_child(setup)
	setup.call("_on_certified_preset_pressed")
	var selector := setup.get("terrain_backend_selector") as OptionButton
	selector.select(2)
	selector.item_selected.emit(2)
	var settings: Dictionary = setup.call("_build_selected_slot_settings")
	setup.free()
	settings["surface_generation_contract_id"] = RUNTIME_BACKEND
	settings["surface_generation_recipe_json"] = runner.call("RecipeJson", true)
	if not bool(session.call("save_active_world_settings", settings)):
		return _reject("could not save the v9 fixture")
	if not _check_saved_slot(runner, str(settings["surface_generation_recipe_json"]), "default", 1, 0, V9_ID):
		return false

	settings["surface_generation_recipe_json"] = runner.call("RecipeJson", false)
	if not bool(session.call("save_active_world_settings", settings)):
		return _reject("could not save the v8 fixture")
	if not _check_saved_slot(runner, str(settings["surface_generation_recipe_json"]), "test", 3, 5, "legacy"):
		return false
	error = str(runner.call("CheckContracts"))
	if not error.is_empty(): return _reject(error)
	return await _check_shader_global(runner)

func _check_shader_global(runner: RefCounted) -> bool:
	if DisplayServer.get_name() == "headless":
		print("WORLD_GENERATION_PUBLISH shader pixel check skipped (headless)")
		return true
	var viewport := SubViewport.new()
	viewport.disable_3d = true
	viewport.size = Vector2i(8, 8)
	viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
	var rect := ColorRect.new()
	rect.size = Vector2(viewport.size)
	var shader := Shader.new()
	shader.code = """
shader_type canvas_item;
render_mode unshaded;
global uniform int ringworld_world_features;
void fragment() {
	if (ringworld_world_features == 5) {
		COLOR = vec4(1.0, 0.0, 1.0, 1.0);
	} else if (ringworld_world_features == 0) {
		COLOR = vec4(0.0, 1.0, 0.0, 1.0);
	} else {
		COLOR = vec4(1.0, 0.0, 0.0, 1.0);
	}
}
"""
	var material := ShaderMaterial.new()
	material.shader = shader
	rect.material = material
	viewport.add_child(rect)
	root.add_child(viewport)
	var passed := bool(runner.call("PublishShaderProbe", true))
	if passed:
		passed = await _check_shader_pixel(viewport, 5, Color(1.0, 0.0, 1.0))
	else:
		_reject("could not publish synthetic shader mask")
	var restored := bool(runner.call("PublishShaderProbe", false))
	if passed:
		passed = await _check_shader_pixel(viewport, 0, Color(0.0, 1.0, 0.0)) if restored else _reject("could not restore the legacy shader mask")
	viewport.free()
	return passed

func _check_shader_pixel(viewport: SubViewport, mask: int, expected: Color) -> bool:
	viewport.render_target_update_mode = SubViewport.UPDATE_ONCE
	await RenderingServer.frame_post_draw
	var rendered := viewport.get_texture().get_image()
	if rendered == null or rendered.is_empty(): return _reject("shader probe produced no image")
	var pixel := rendered.get_pixel(4, 4)
	if not pixel.is_equal_approx(expected): return _reject("shader mask %d drew %s instead of %s" % [mask, pixel, expected])
	print("WORLD_GENERATION_PUBLISH shader mask %d drew %s" % [mask, pixel])
	return true

func _check_saved_slot(runner: RefCounted, recipe_json: String, previous_source: String, previous_generation: int, previous_mask: int, source: String) -> bool:
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	if not bool(launcher.get("selected_slot_has_saved_settings")) or not bool(launcher.call("_prepare_runtime_surface_generation", true)) \
		or str(launcher.get("pending_surface_generation_recipe_json")) != recipe_json:
		launcher.free()
		return _reject("launcher did not prepare the stored recipe for %s" % source)
	var error := str(runner.call("CheckState", previous_source, previous_generation, previous_mask))
	if not error.is_empty():
		launcher.free()
		return _reject("preparing the slot published early: " + error)

	var game := GAME.instantiate()
	launcher.call("_configure_game", game, 5000000.0)
	if game.is_inside_tree():
		error = "game entered the tree before the publication check"
	else:
		error = str(runner.call("CheckState", source, 1, 0))
		if error.is_empty() and source == V9_ID:
			error = str(runner.call("CheckRuntimeSampler", game.get_node("ChunkGridManager").get("TerrainSettings")))
	game.free()
	launcher.free()
	return true if error.is_empty() else _reject(error)

func _reject(message: String) -> bool:
	_failure = message
	return false
