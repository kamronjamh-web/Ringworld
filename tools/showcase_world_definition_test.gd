extends SceneTree
## Headless definition guard: fails when res://resources/menu/showcase_world.json is missing, invalid, not a Standard runtime-generation
## world with seed 1337, differs from what tools/showcase_world_definition.gd generates today, or its bundled picture belongs to
## another showcase_id. Run through tools/showcase_world_definition_test.ps1 (isolated APPDATA).

const DEFINITION := preload("res://tools/showcase_world_definition.gd")
const SAMPLER := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")

var _errors := PackedStringArray()
var _checks := 0


func _initialize() -> void:
	call_deferred("_run")


func _expect(condition: bool, message: String) -> bool:
	_checks += 1
	if not condition:
		_errors.append(message)
		print("FAIL: ", message)
	return condition


func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("showcase_tools"):
		push_error("Refusing to run: user:// (%s) is not an isolated showcase_tools APPDATA." % user_dir)
		quit(2)
		return
	_check()
	if _errors.is_empty():
		print("SHOWCASE_DEFINITION_TEST=PASS checks=%d" % _checks)
		quit(0)
	else:
		for error in _errors:
			push_error(error)
		print("SHOWCASE_DEFINITION_TEST=FAIL checks=%d" % _checks)
		quit(1)


func _check() -> void:
	if not _expect(FileAccess.file_exists(DEFINITION.OUTPUT_PATH), "showcase_world.json is missing; run tools/generate_showcase_world.ps1"):
		return
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(DEFINITION.OUTPUT_PATH))
	if not _expect(parsed is Dictionary, "showcase_world.json is not a JSON object"):
		return
	var file_definition: Dictionary = parsed
	_expect(str(file_definition.get("showcase_id", "")) == DEFINITION.SHOWCASE_ID, "showcase_id is '%s', the generator says '%s'" % [file_definition.get("showcase_id", ""), DEFINITION.SHOWCASE_ID])
	var settings: Variant = file_definition.get("generation_settings", null)
	if not _expect(settings is Dictionary, "generation_settings is missing"):
		return
	var standard := WorldPresets.describe("standard")
	_expect(str(settings.get("terrain_backend", "")) == "surface_runtime_generation_v1", "terrain_backend is not surface_runtime_generation_v1")
	_expect(absf(float(settings.get("radius_km", 0.0)) - float(standard.radius_km)) < 0.01 and absf(float(settings.get("ring_width_km", 0.0)) - float(standard.width_km)) < 0.01, "radius/width are not the Standard preset")
	_expect(int(settings.get("terrain_seed", -1)) == DEFINITION.SHOWCASE_SEED, "terrain_seed is not %d" % DEFINITION.SHOWCASE_SEED)
	_expect(int(settings.get("surface_definition_version", 0)) == 1 and str(settings.get("surface_generation_contract_id", "")) == "surface_runtime_generation_v1", "surface contract metadata is missing")
	var recipe_json := str(settings.get("surface_generation_recipe_json", ""))
	var sampler = SAMPLER.new()
	_expect(not recipe_json.is_empty() and bool(sampler.call("ValidateJson", recipe_json)), "the frozen recipe is invalid: " + str(sampler.get("LastError")))
	_expect(str(sampler.get("CanonicalJson")) == recipe_json, "the frozen recipe is not canonical")
	var recipe: Variant = JSON.parse_string(recipe_json)
	_expect(recipe is Dictionary and int(recipe.get("seed", -1)) == DEFINITION.SHOWCASE_SEED, "the frozen recipe seed is not %d" % DEFINITION.SHOWCASE_SEED)
	sampler.call("Release")
	# The live session must accept it as a showcase (same eligibility rules as the warm-load).
	var session := root.get_node_or_null("WorldSession")
	if _expect(session != null, "WorldSession autoload missing"):
		_expect(not session.get_showcase_definition().is_empty(), "WorldSession rejects the showcase definition")
	# Drift: what the generator would write today must equal the file.
	var built: Dictionary = DEFINITION.build(self)
	if _expect(str(built["error"]).is_empty(), "generator failed: " + str(built["error"])):
		_expect(DEFINITION.to_text(JSON.parse_string(DEFINITION.to_text(built["definition"]))) == DEFINITION.to_text(file_definition), "showcase_world.json differs from the generator output; run tools/generate_showcase_world.ps1 (and bump SHOWCASE_ID plus regenerate the picture if this is deliberate)")
	# The bundled picture must belong to this definition.
	var sidecar_path := "res://resources/menu/showcase_snapshot.json"
	if _expect(FileAccess.file_exists(sidecar_path), "bundled showcase picture sidecar is missing; run tools/showcase_snapshot_capture.ps1"):
		var sidecar: Variant = JSON.parse_string(FileAccess.get_file_as_string(sidecar_path))
		_expect(sidecar is Dictionary and str(sidecar.get("showcase_id", "")) == DEFINITION.SHOWCASE_ID, "bundled showcase picture was captured for another showcase_id; regenerate it with tools/showcase_snapshot_capture.ps1")
		_expect(FileAccess.file_exists("res://resources/menu/showcase_snapshot.png"), "bundled showcase picture PNG is missing")
