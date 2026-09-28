extends SceneTree

const Sampler := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")
const Recipe := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")

func _init() -> void:
	var v2: TerrainGenerationRecipe = Recipe.new()
	var legacy: TerrainGenerationRecipe = Recipe.new()
	legacy.generator_id = Recipe.LEGACY_GENERATOR_ID
	var passed := true
	var sampler = Sampler.new()
	var legacy_sampler = Sampler.new()
	passed = _require(sampler.Configure(v2.to_normalized_dictionary()), "configure v2") and passed
	passed = _require(legacy_sampler.Configure(legacy.to_normalized_dictionary()), "configure v1") and passed
	passed = _require(not sampler.CanonicalJson.contains("NaN") and not sampler.CanonicalJson.contains("Infinity"), "finite canonical") and passed
	passed = _require(sampler.DefinitionHash != legacy_sampler.DefinitionHash, "coast fields affect v2 hash") and passed
	var previous := -1.0
	var depth_2 := -1.0
	var depth_6 := -1.0
	for d in range(0, 50001, 10):
		var depth: float = sampler.OceanDepthForDistance(float(d))
		passed = _require(is_finite(depth) and depth >= previous - 0.001, "monotone finite profile") and passed
		if depth_2 < 0.0 and depth >= 2.0: depth_2 = d
		if depth_6 < 0.0 and depth >= 6.0: depth_6 = d
		previous = depth
	passed = _require(depth_2 >= 70.0 and depth_2 <= 140.0, "default two-metre reach") and passed
	passed = _require(depth_6 >= 220.0 and depth_6 <= 420.0, "default six-metre reach") and passed
	# v1's canonical JSON deliberately has no coastal fields; saved worlds retain
	# that exact identity and continue through the unmodified height transfer.
	passed = _require(not legacy_sampler.CanonicalJson.contains("beach_slope"), "v1 canonical frozen") and passed
	passed = _recipe_validation(v2, sampler) and passed
	passed = _local_strip_is_smooth(sampler) and passed
	passed = _shore_transects(sampler, legacy_sampler) and passed
	sampler.Release(); legacy_sampler.Release()
	print("Terrain coastal profile contract: ", "PASS" if passed else "FAIL", " | 2m=", depth_2, " 6m=", depth_6)
	quit(0 if passed else 1)

func _require(condition: bool, label: String) -> bool:
	if condition: return true
	push_error("Terrain coastal profile contract failed: " + label)
	return false

func _recipe_validation(recipe: TerrainGenerationRecipe, sampler) -> bool:
	var passed := true
	var baseline: String = sampler.DefinitionHash
	for field in ["beach_slope", "shelf_width_m", "shelf_depth_m", "continental_slope_width_m"]:
		var copy := recipe.snapshot()
		copy.set(field, float(copy.get(field)) + (0.001 if field == "beach_slope" else 1.0))
		var probe = Sampler.new()
		passed = _require(probe.Configure(copy.to_normalized_dictionary()) and probe.DefinitionHash != baseline, field + " hash sensitivity") and passed
		probe.Release()
	var missing: String = sampler.CanonicalJson.replace(",\"beach_slope\":0.025", "")
	var bad = Sampler.new()
	passed = _require(not bad.ConfigureJson(missing), "missing v2 coastal field rejected") and passed
	passed = _require(not bad.ConfigureJson(sampler.CanonicalJson.left(-1) + ",\"unknown_coast\":1}"), "unknown v2 field rejected") and passed
	bad.Release()
	return passed

func _local_strip_is_smooth(sampler) -> bool:
	for across in [158968.0, 159000.0, 159032.0]:
		var previous: float = sampler.CalculateHeight(0.0, across)
		var maximum := 0.0
		for around in range(2, 66, 2):
			var height: float = sampler.CalculateHeight(float(around), across)
			maximum = maxf(maximum, absf(height - previous)); previous = height
		if not _require(maximum < .15, "v2 local strip max 2m delta < 0.15m"): return false
	return true

func _shore_transects(v2, v1) -> bool:
	const CIRC := 31415936.0
	const ACROSS := 159000.0
	var crossings: Array[float] = []
	var previous := 0.0
	var previous_c := float(v2.SampleDiagnostics(0.0, ACROSS)["continentalness"])
	for around_i in range(4096, int(CIRC), 4096):
		var around := float(around_i)
		var current_c := float(v2.SampleDiagnostics(around, ACROSS)["continentalness"])
		if (previous_c < 0.0 and current_c >= 0.0) or (previous_c >= 0.0 and current_c < 0.0):
			var lo := previous; var hi := around
			for iteration in 18:
				var mid := (lo + hi) * .5
				if (float(v2.SampleDiagnostics(mid, ACROSS)["continentalness"]) < 0.0) == (previous_c < 0.0): lo = mid
				else: hi = mid
			crossings.append((lo + hi) * .5)
			if crossings.size() == 8: break
		previous = around; previous_c = current_c
	var passed := _require(crossings.size() == 8, "eight shoreline crossings")
	var report: Array = []
	for shore in crossings:
		var da := (float(v2.SampleDiagnostics(shore + 64.0, ACROSS)["continentalness"]) - float(v2.SampleDiagnostics(shore - 64.0, ACROSS)["continentalness"])) / 128.0
		var dz := (float(v2.SampleDiagnostics(shore, ACROSS + 64.0)["continentalness"]) - float(v2.SampleDiagnostics(shore, ACROSS - 64.0)["continentalness"])) / 128.0
		var normal := Vector2(-da, -dz).normalized()
		passed = _require(normal.length() > 0.0, "shore gradient normal") and passed
		var v2_2 := _reach(v2, shore, ACROSS, normal, 2.0, 20000, 10)
		var v2_6 := _reach(v2, shore, ACROSS, normal, 6.0, 20000, 10)
		var v1_6 := _reach(v1, shore, ACROSS, normal, 6.0, 20000, 10)
		var shelf := _reach(v2, shore, ACROSS, normal, 150.0, 100000, 250)
		var v1_shelf := _reach(v1, shore, ACROSS, normal, 150.0, 100000, 250)
		var deep: Dictionary = {}
		for target in [1000.0, 2000.0, 4000.0]:
			var key := str(int(target))
			deep[key] = {"v2":_reach(v2, shore, ACROSS, normal, target, 100000, 250), "v1":_reach(v1, shore, ACROSS, normal, target, 100000, 250)}
		report.append({"shore":shore,"normal":[normal.x,normal.y],"v2_2":v2_2,"v2_6":v2_6,"v1_6":v1_6,"v2_shelf150":shelf,"v1_shelf150":v1_shelf,"deep_reaches":deep})
		if v1_6 >= 0.0 and v2_6 >= 0.0: passed = _require(v2_6 <= v1_6, "v2 six metre reach faster") and passed
	var path := "res://.godot/terrain-generation-tests/coastal-report.json"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path.get_base_dir()))
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify({"profile":{"two_m":_profile_reach(v2,2.0),"six_m":_profile_reach(v2,6.0),"shelf_150":_profile_reach(v2,150.0)},"transects":report})); file.close()
	print("Terrain coastal transects (canonical normal approximation): ", JSON.stringify(report))
	return passed

func _reach(sampler, shore: float, across: float, normal: Vector2, depth: float, maximum: int, step: int) -> float:
	for distance in range(0, maximum + 1, step):
		var sample_across := across + normal.y * distance
		if sample_across < 0.0 or sample_across > 318000.0: return -1.0
		var height := float(sampler.CalculateHeight(shore + normal.x * distance, sample_across))
		if is_finite(height) and -height >= depth: return float(distance)
	return -1.0

func _profile_reach(sampler, depth: float) -> float:
	for distance in range(0, 100001, 10):
		if float(sampler.OceanDepthForDistance(float(distance))) >= depth: return float(distance)
	return -1.0
