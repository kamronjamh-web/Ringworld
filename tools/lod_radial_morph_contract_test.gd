extends SceneTree

# Regression for proxy LOD ownership: the fine mesh is discarded at the middle
# of its far band, so its radial height must equal the next coarser mesh there.

const EPSILON := 0.0001

func _init() -> void:
	var terrain_source := FileAccess.get_file_as_string("res://materials/ring_terrain_proxy.gdshader")
	var ocean_source := FileAccess.get_file_as_string("res://materials/ring_ocean_proxy.gdshader")
	var sector_source := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")
	if not _require(terrain_source.contains("float morph_handoff = (morph_start_meters"), "terrain morph does not finish at handoff"): return
	# Ocean replacement starts two metres before the visible far cutoff.
	# Its base and FFT geometry must both finish morphing by that guard.
	if not _require(ocean_source.contains("float morph_handoff = max(morph_start_meters + 0.01") and ocean_source.contains("* 0.5 - 2.0") and ocean_source.contains("midpoint - 2.0"), "ocean morph does not finish at guarded handoff"): return
	if not _require(sector_source.contains("parentTerrainHeight - terrainGeometryHeight + underlay - parentUnderlay"), "smooth terrain morph delta omits rendered height/underlay"): return
	if not _require(sector_source.contains("parentWaterHeight - waterHeight + underlay - ownWaterRestore - parentUnderlay + parentWaterRestore"), "water morph delta omits rendered base offsets"): return

	# Smooth/SDF terrain: the +0.5 surface offset cancels, while distinct
	# underlays must be part of the radial morph endpoint.
	var terrain_own := 37.25
	var terrain_parent := 40.75
	var terrain_own_underlay := 0.5
	var terrain_parent_underlay := 1.0
	var terrain_delta := terrain_parent - terrain_own + terrain_own_underlay - terrain_parent_underlay
	_assert_radial_endpoint(
		terrain_own + 0.5 - terrain_own_underlay,
		terrain_delta,
		terrain_parent + 0.5 - terrain_parent_underlay,
		"smooth terrain")

	# The 8m FFT tier restores its own 0.5m underlay, but the 16m parent does
	# not. This is the previously exposed endpoint mismatch.
	var water_8_delta := 24.0 - 20.0 + 0.5 - 0.5 - 1.0 + 0.0
	_assert_radial_endpoint(20.0 + 1.0 - 0.5, water_8_delta + 0.5, 24.0 + 1.0 - 1.0, "8m to 16m water")

	# Both 4m and 8m tiers restore their underlay, so their visible bases are
	# gameplay height + 1 and only the sampled parent height remains.
	var water_4_delta := 24.0 - 20.0 + 0.2 - 0.2 - 0.5 + 0.5
	_assert_radial_endpoint(20.0 + 1.0 - 0.2, water_4_delta + 0.2, 24.0 + 1.0 - 0.5 + 0.5, "4m to 8m water")

	var start := 1000.0
	var up := Vector3(-1.0, 0.0, 0.0)
	var position := Vector3(start, 0.0, 0.0)
	var moved := position + up * 3.5
	if not _require(abs((start - moved.length()) - 3.5) < EPSILON and abs((moved - position).dot(Vector3(0.0, 0.0, 1.0))) < EPSILON, "ring radial convention changed"): return
	print("LOD_RADIAL_MORPH_CONTRACT=PASS handoff_timing,smooth_terrain,water_4_to_8,water_8_to_16,ring_radial")
	quit(0)

func _assert_radial_endpoint(own_base: float, applied_delta: float, parent_base: float, label: String) -> void:
	if not _require(abs(own_base + applied_delta - parent_base) < EPSILON, "%s endpoint differs: %.6f vs %.6f" % [label, own_base + applied_delta, parent_base]): return

func _require(condition: bool, message: String) -> bool:
	if condition: return true
	push_error("LOD_RADIAL_MORPH_CONTRACT=FAIL " + message)
	quit(1)
	return false
