extends SceneTree
## Headless entry point for tools/new_generation_world.ps1. Tests call execute() with a temporary WorldSession.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const BRIDGE := preload("res://scripts/Terrain/Generation/WorldGenerationBridge.cs")
const SAMPLER := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const SESSION := preload("res://scripts/world_session.gd")
const PIN := preload("res://tools/world_generation_pin.gd")
const V9 := "ringworld_surface_world_v9"


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	var request := _parse_args(OS.get_cmdline_user_args())
	var result := request
	var session := root.get_node_or_null("WorldSession")
	if request.get("ok") == true and session != null:
		result = execute(self, session, request)
	if result.get("ok") != true or session == null:
		push_error("NEW_GENERATION_WORLD=FAIL " + str(result.get("error", "tool did not complete")))
		quit(1)
		return
	if request.get("delete_review", false):
		print("NEW_GENERATION_WORLD=PASS %sreview_slots=%s" % ["dry_run " if request.get("dry_run", false) else "", str(result.get("slots", []))])
	else:
		if request.get("dry_run", false):
			print("NEW_GENERATION_WORLD_PLAN action=%s recipe=%s" % [result.get("action"), result.get("recipe")])
		print("NEW_GENERATION_WORLD=PASS slot=%d name=%s generation=%d definition_hash=%s" % [
			result.get("slot"), result.get("name"), result.get("generation"), result.get("definition_hash")])
	quit(0)


static func execute(tree: SceneTree, session: Node, request: Dictionary) -> Dictionary:
	var step := str(request.get("step", ""))
	var dry_run: bool = request.get("dry_run", false)
	var root_path := ProjectSettings.globalize_path(str(request.get("worlds_root", session.get("worlds_root")))).replace("\\", "/").simplify_path()
	if not _matches("^[A-Za-z0-9][A-Za-z0-9_-]*\\z", step):
		return _failure("Step must start with a letter or digit and contain only letters, digits, underscores or hyphens.")
	if request.has("worlds_root") and not dry_run:
		return _failure("A worlds root override is allowed only for an isolated dry run.")
	if not root_path.is_absolute_path() or _path_has_link(root_path) or FileAccess.file_exists(root_path):
		return _failure("Invalid worlds root: " + root_path)
	if bool(session.get("warm_world_active")):
		return _failure("A warm world is active.")
	if request.get("delete_review", false):
		return _delete_review(session, root_path, step, dry_run)

	var slot: int = request.get("slot", 0)
	var seed: int = request.get("seed", 1337)
	if slot < 1 or slot > SESSION.SLOT_COUNT:
		return _failure("Choose an explicit slot from 1 to %d." % SESSION.SLOT_COUNT)
	if seed < 0 or seed > 2147483647:
		return _failure("Seed must be in 0..2147483647.")

	var bridge := BRIDGE.new()
	var latest: int = bridge.get("LatestFrozen")
	var generation: int = request.get("generation", latest)
	var review := str(request.get("review", ""))
	var review_requested := request.has("review")
	if review_requested:
		if request.has("generation") and generation != latest:
			return _failure("Review worlds require the latest frozen generation (%d)." % latest)
		generation = latest
		if review.is_empty():
			var open_features: PackedStringArray = bridge.call("ListOpenFeatures")
			return _failure("no open features" if open_features.is_empty() else "Choose at least one review feature.")
	var name := "%s-%s-s%d" % ["review" if review_requested else "g%02d" % generation, step, seed]
	if name.length() > SESSION.MAX_NAME_LENGTH:
		return _failure("World name exceeds %d characters: %s" % [SESSION.MAX_NAME_LENGTH, name])
	var target := root_path.path_join("slot_%d" % slot)
	var previous := _inspect_slot(target, slot)
	if previous.get("ok") != true:
		return previous
	var occupied: bool = previous.get("occupied")
	if occupied:
		if request.get("replace", false) != true:
			return _failure("Slot %d is occupied. Pass -Replace for a managed test world." % slot)
		if not _matches("^(g[0-9]{2}|review)-", str(previous.get("name"))):
			return _failure("Slot %d is not a managed test world. Free it in the game before creating a test world." % slot)
		if not _is_safe_tree(target):
			return _failure("Slot %d contains a link or unreadable directory." % slot)

	# Check the ledger before creating a launcher, including review ID errors when no features are open.
	var sampler := SAMPLER.new()
	var review_ids := review.split(",") if review_requested else PackedStringArray()
	var defaults: Variant = sampler.call("WorldDefaultFields", generation, seed, review_ids)
	if not (defaults is Dictionary) or defaults.is_empty():
		return _failure(str(sampler.get("LastError")) if not str(sampler.get("LastError")).is_empty() else "Recipe defaults are unavailable.")
	if review_requested:
		var open_features: PackedStringArray = bridge.call("ListOpenFeatures")
		if open_features.is_empty():
			return _failure("no open features")
		for feature in review_ids:
			if not open_features.has(feature):
				return _failure("Review feature '%s' is not open." % feature)

	var built := build_settings(tree, session, generation, seed, review)
	if built.get("ok") != true:
		return built
	var settings: Dictionary = built.get("settings")
	var recipe := str(settings.get("surface_generation_recipe_json", ""))
	var validation := _validate_recipe(bridge, recipe, generation, seed)
	if validation.get("ok") != true:
		return _failure(str(validation.get("error", "Recipe validation did not complete.")))
	var sorted_review := Array(review_ids)
	sorted_review.sort()
	var expected_review := ",".join(PackedStringArray(sorted_review))
	if validation.get("review_features") != expected_review:
		return _failure("Launcher did not preserve the requested review features.")
	if sampler.call("ValidateJson", recipe) != true or str(sampler.get("CanonicalJson")) != recipe:
		return _failure("Launcher did not produce a canonical recipe.")
	var hash := str(sampler.get("DefinitionHashHex"))
	if not _matches("^[0-9A-F]{16}\\z", hash):
		return _failure("Canonical recipe definition hash unavailable.")
	if not dry_run:
		var written := _write_slot(session, root_path, slot, name, generation, seed, settings, previous)
		if written.get("ok") != true:
			return written
	return {"ok": true, "slot": slot, "name": name, "generation": generation, "definition_hash": "0x" + hash,
		"recipe": recipe, "settings": settings, "action": "replace" if occupied else "create"}


## Build with the real launcher and leave the active session untouched. The optional preview switch supports the negative control.
static func build_settings(tree: SceneTree, session: Node, generation: int, seed: int, review: String, use_preview := false, preview_path := "") -> Dictionary:
	var active_slot: int = session.get("active_slot")
	session.set("active_slot", 0) # _ready() must not recover metadata from a real slot.
	PIN.pin(generation, review)
	var launcher := LAUNCHER.instantiate()
	launcher.set("use_preview_recipe", use_preview)
	if not preview_path.is_empty():
		launcher.set("preview_recipe_path", preview_path)
	tree.root.add_child(launcher)
	launcher.call("_apply_new_world_request", {"preset": "standard", "terrain_seed": seed, "auto_start": false})
	var result: Dictionary
	if launcher.call("_prepare_runtime_surface_generation", true) == true:
		var settings: Variant = launcher.call("_build_selected_slot_settings")
		if settings is Dictionary and not settings.is_empty():
			result = {"ok": true, "settings": settings}
		else:
			result = _failure("Launcher did not return world settings.")
	else:
		var warning := launcher.get("warning_label") as Label
		result = _failure("Launcher rejected the requested recipe." if warning == null else "Launcher rejected the requested recipe: " + warning.text)
	launcher.free()
	PIN.unpin()
	session.set("active_slot", active_slot)
	return result


static func _write_slot(session: Node, root_path: String, slot: int, name: String, generation: int, seed: int, settings: Dictionary, previous: Dictionary) -> Dictionary:
	var target := root_path.path_join("slot_%d" % slot)
	var stage := root_path.path_join(".generation-stage-%d-%d" % [OS.get_process_id(), Time.get_ticks_usec()])
	var absolute_stage := stage.simplify_path()
	if absolute_stage.get_base_dir() != root_path or DirAccess.dir_exists_absolute(stage) or FileAccess.file_exists(stage) or _is_link(stage):
		return _failure("Could not reserve a safe staging path.")
	if DirAccess.make_dir_recursive_absolute(root_path) != OK or DirAccess.make_dir_absolute(stage) != OK:
		return _failure("Could not create slot staging directory: " + stage)
	var staged_slot := stage.path_join("slot_%d" % slot)
	var backup := stage.path_join("previous-slot")
	var old_root: String = session.get("worlds_root")
	var old_slot: int = session.get("active_slot")
	const ACTIVE_DIRECTORY := "ringworld/runtime/active_world_directory"
	var old_active: Variant = ProjectSettings.get_setting(ACTIVE_DIRECTORY)
	session.set("worlds_root", stage)
	session.set("active_slot", 0)
	var saved: bool = session.call("create_world", slot, name) == true and session.call("save_active_world_settings", settings) == true
	session.set("worlds_root", old_root)
	session.set("active_slot", old_slot)
	ProjectSettings.set_setting(ACTIVE_DIRECTORY, old_active)
	if not saved:
		return _stage_failed(session, stage, "WorldSession could not save the staged slot.")
	var staged := _inspect_slot(staged_slot, slot)
	if staged.get("ok") != true:
		return _stage_failed(session, stage, "Could not read the staged slot: " + str(staged.get("error")))
	if not _stored_matches(staged, name, generation, seed, settings):
		return _stage_failed(session, stage, "Staged slot name, recipe or settings differ from the launcher.")
	var current := _inspect_slot(target, slot)
	if current.get("ok") != true or current != previous:
		return _stage_failed(session, stage, "Target slot changed while the world was being prepared.")
	var occupied: bool = previous.get("occupied")
	if occupied and (not _is_safe_tree(target) or DirAccess.rename_absolute(target, backup) != OK):
		return _stage_failed(session, stage, "Could not move the old managed slot to staging.")
	if DirAccess.rename_absolute(staged_slot, target) != OK:
		if occupied and DirAccess.rename_absolute(backup, target) != OK:
			return _failure("Could not install the new slot or restore the old one. Old slot: " + backup)
		return _stage_failed(session, stage, "Could not install the new slot.")
	var installed := _inspect_slot(target, slot)
	if installed.get("ok") != true or not _stored_matches(installed, name, generation, seed, settings):
		if DirAccess.rename_absolute(target, staged_slot) != OK or (occupied and DirAccess.rename_absolute(backup, target) != OK):
			return _failure("Could not roll back the installed slot. Inspect " + stage)
		return _stage_failed(session, stage, "Installed slot did not pass readback validation.")
	if not _clean_stage(session, stage):
		return _failure("World created, but staging cleanup failed: " + stage)
	return {"ok": true}


static func _stored_matches(slot: Dictionary, name: String, generation: int, seed: int, settings: Dictionary) -> bool:
	if slot.get("occupied") != true or slot.get("name") != name:
		return false
	var stored: Variant = slot.get("settings")
	# WorldSession writes JSON and JSON.parse_string reads its numbers as floats.
	var expected: Variant = JSON.parse_string(JSON.stringify(settings, "  "))
	if not (stored is Dictionary) or not (expected is Dictionary) or stored != expected:
		return false
	return _validate_recipe(BRIDGE.new(), str(stored.get("surface_generation_recipe_json", "")), generation, seed).get("ok") == true


static func _validate_recipe(bridge: RefCounted, recipe: String, generation: int, seed: int) -> Dictionary:
	if bridge.call("ValidateRecipeJson", recipe) != true:
		return _failure("Invalid world recipe: " + str(bridge.get("LastError")))
	var decoded: Variant = JSON.parse_string(recipe)
	if not (decoded is Dictionary) or decoded.get("generator_id") != V9 or decoded.get("world_generation") != generation or decoded.get("seed") != seed:
		return _failure("Launcher produced a recipe for the wrong generation or seed.")
	return {"ok": true, "review_features": decoded.get("review_features")}


static func _inspect_slot(path: String, slot: int) -> Dictionary:
	if _is_link(path) or FileAccess.file_exists(path):
		return _failure("Slot %d is occupied by an invalid path." % slot)
	if not DirAccess.dir_exists_absolute(path):
		return {"ok": true, "occupied": false}
	var metadata_path := path.path_join(SESSION.METADATA_FILE)
	if _is_link(metadata_path) or not FileAccess.file_exists(metadata_path):
		return _failure("Slot %d has no readable world.json. Inspect the slot in the game." % slot)
	var raw := FileAccess.get_file_as_string(metadata_path)
	var metadata: Variant = JSON.parse_string(raw)
	if (not (metadata is Dictionary) or metadata.get("slot") != slot
			or not (metadata.get("name") is String) or str(metadata.get("name")).is_empty()
			or not (metadata.get("generation_settings") is Dictionary)):
		return _failure("Slot %d has invalid metadata." % slot)
	return {"ok": true, "occupied": true, "name": metadata.get("name"), "settings": metadata.get("generation_settings"), "raw": raw}


static func _delete_review(session: Node, root_path: String, step: String, dry_run: bool) -> Dictionary:
	var matches: Array[int] = []
	var slots := {}
	var prefix := "review-%s-" % step
	for slot in range(1, SESSION.SLOT_COUNT + 1):
		var path := root_path.path_join("slot_%d" % slot)
		var inspected := _inspect_slot(path, slot)
		if inspected.get("ok") != true or inspected.get("occupied") != true:
			continue
		if str(inspected.get("name")).begins_with(prefix):
			if not _is_safe_tree(path):
				return _failure("Review slot %d contains a link or unreadable directory." % slot)
			matches.append(slot)
			slots[slot] = inspected
			print("NEW_GENERATION_WORLD_REVIEW slot=%d name=%s" % [slot, inspected.get("name")])
	if not dry_run:
		for slot in matches:
			var path := root_path.path_join("slot_%d" % slot)
			if _inspect_slot(path, slot) != slots[slot] or not _is_safe_tree(path):
				return _failure("Review slot %d changed before deletion." % slot)
			if session.call("delete_slot", slot) != true:
				return _failure("Could not delete review slot %d." % slot)
	return {"ok": true, "slots": matches}


static func _stage_failed(session: Node, stage: String, message: String) -> Dictionary:
	return _failure(message if _clean_stage(session, stage) else message + " Inspect staging at " + stage)


static func _clean_stage(session: Node, stage: String) -> bool:
	return _is_safe_tree(stage) and session.call("_delete_directory_recursive", stage) == true


static func _is_safe_tree(path: String) -> bool:
	if _is_link(path):
		return false
	var directory := DirAccess.open(path)
	if directory == null:
		return false
	directory.include_hidden = true
	for entry in directory.get_files():
		if directory.is_link(entry):
			return false
	for entry in directory.get_directories():
		if not _is_safe_tree(path.path_join(entry)):
			return false
	return true


static func _is_link(path: String) -> bool:
	var parent := DirAccess.open(path.get_base_dir())
	return parent != null and parent.is_link(path.get_file())


static func _path_has_link(path: String) -> bool:
	var current := path
	while not current.get_file().is_empty():
		if _is_link(current):
			return true
		var parent := current.get_base_dir()
		if parent == current:
			break
		current = parent
	return false


static func _matches(pattern: String, value: String) -> bool:
	var regex := RegEx.new()
	return regex.compile(pattern) == OK and regex.search(value) != null


static func _failure(message: String) -> Dictionary:
	return {"ok": false, "error": message}


static func _parse_args(args: PackedStringArray) -> Dictionary:
	var request := {"ok": true}
	for arg in args:
		if arg in ["--replace", "--dry-run", "--delete-review"]:
			var key := arg.substr(2).replace("-", "_")
			if request.has(key):
				return _failure("Duplicate argument: " + arg)
			request[key] = true
			continue
		var parts := arg.split("=", true, 1)
		if parts.size() != 2 or parts[0] not in ["--slot", "--step", "--seed", "--generation", "--review", "--worlds-root"]:
			return _failure("Unknown argument: " + arg)
		var key := parts[0].substr(2).replace("-", "_")
		if request.has(key):
			return _failure("Duplicate argument: " + arg)
		if key in ["slot", "seed", "generation"]:
			if not parts[1].is_valid_int():
				return _failure("Expected an integer: " + arg)
			request[key] = int(parts[1])
		else:
			request[key] = parts[1]
	if request.has("worlds_root") and not request.get("dry_run", false):
		return _failure("--worlds-root is allowed only for an isolated dry run.")
	if request.get("delete_review", false):
		for key in ["slot", "seed", "generation", "review", "replace"]:
			if request.has(key):
				return _failure("--delete-review does not take " + key)
	return request
